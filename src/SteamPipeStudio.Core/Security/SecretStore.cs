using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SteamPipeStudio.Core.Security;

public interface ISecretStore
{
    string? Read(string name);
    void Write(string name, string value);
    void Delete(string name);
}

public static class SecretStoreFactory
{
    public const string PublisherApiKey = "publisher-web-api-key";

    /// <summary>
    /// Secret name holding the Steam password for one account. Per account rather than
    /// per profile, because two profiles uploading different apps from the same account
    /// share one login, and asking twice for the same password is how people end up
    /// storing it somewhere worse.
    ///
    /// The name becomes a file name on Windows and Linux, so it is reduced to a
    /// conservative character set. Steam account names are already limited to letters,
    /// digits and underscores, which makes the escape hatch below unreachable in
    /// practice — it exists so a typo in the account field cannot write outside the
    /// secrets folder.
    /// </summary>
    public static string SteamPassword(string accountName)
    {
        var safe = new StringBuilder("steam-password-");

        foreach (var c in accountName.Trim().ToLowerInvariant())
            safe.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' ? c : '_');

        return safe.ToString();
    }

    public static ISecretStore Create(string storageDirectory)
    {
        if (OperatingSystem.IsWindows()) return new WindowsDpapiSecretStore(storageDirectory);
        if (OperatingSystem.IsMacOS()) return new MacKeychainSecretStore();
        return new LinuxSecretStore(storageDirectory);
    }
}

/// <summary>
/// Windows: DPAPI, scoped to the current user, called through P/Invoke so the Core
/// library keeps zero NuGet dependencies.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiSecretStore : ISecretStore
{
    private readonly string _directory;

    public WindowsDpapiSecretStore(string directory)
    {
        _directory = Path.Combine(directory, "secrets");
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(string name) => Path.Combine(_directory, name + ".bin");

    public string? Read(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;

        try
        {
            var plaintext = Unprotect(File.ReadAllBytes(path));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception e) when (e is CryptographicException or IOException)
        {
            // Typically means the file was copied from another user or machine.
            return null;
        }
    }

    public void Write(string name, string value) =>
        File.WriteAllBytes(PathFor(name), Protect(Encoding.UTF8.GetBytes(value)));

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    // ---- DPAPI interop ----

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int CryptProtectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static byte[] Protect(byte[] plaintext) =>
        Transform(plaintext, encrypt: true);

    private static byte[] Unprotect(byte[] ciphertext) =>
        Transform(ciphertext, encrypt: false);

    private static byte[] Transform(byte[] data, bool encrypt)
    {
        var input = new DataBlob();
        var output = new DataBlob();

        try
        {
            input.cbData = data.Length;
            input.pbData = Marshal.AllocHGlobal(Math.Max(data.Length, 1));
            Marshal.Copy(data, 0, input.pbData, data.Length);

            var ok = encrypt
                ? CryptProtectData(ref input, "SteamPipe Studio", IntPtr.Zero, IntPtr.Zero,
                                   IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                     IntPtr.Zero, CryptProtectUiForbidden, out output);

            if (!ok)
                throw new CryptographicException(Marshal.GetLastWin32Error());

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }
}

/// <summary>
/// macOS: the login keychain, through the Security framework called in-process.
///
/// Not the <c>security</c> command line tool, which this used to run: it takes the secret
/// as an argument (<c>-w &lt;secret&gt;</c>), where <c>ps</c> shows it to every other
/// program on the machine for as long as the tool runs — the one exposure the rest of the
/// app is built to avoid. P/Invoke keeps the secret inside this process and the Core
/// library free of packages.
///
/// The item is the one the old code wrote — a generic password with service
/// <c>SteamPipeStudio</c>, the secret name as account and the value as UTF-8 — so
/// secrets saved before the change are still found. Their access list trusts
/// <c>/usr/bin/security</c> rather than this app, so macOS asks before letting the app
/// read or replace one; "Always Allow" settles it. The list identifies an app by its code
/// signature, which is also why an unsigned or ad-hoc signed build can be asked again
/// after an update.
///
/// <c>kSecUseDataProtectionKeychain</c> stays unset on purpose: that keychain needs a
/// signed app with a keychain access group entitlement, and it cannot see the items
/// already in the login keychain.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychainSecretStore : ISecretStore
{
    private const string Service = "SteamPipeStudio";

    private const int ErrSecSuccess = 0;
    private const int ErrSecDuplicateItem = -25299;
    private const int ErrSecItemNotFound = -25300;

    public string? Read(string name)
    {
        try
        {
            using var cf = new CFScope();
            var k = Keys.Load();
            var query = Query(cf, name, (k.ReturnData, k.True), (k.MatchLimit, k.MatchLimitOne));

            // Any failure — no item, a locked keychain, a denied prompt — means the same
            // thing to the caller: there is no secret it can use.
            if (SecItemCopyMatching(query, out var data) != ErrSecSuccess || data == IntPtr.Zero)
                return null;

            try
            {
                var length = checked((int)CFDataGetLength(data));
                if (length == 0) return string.Empty;

                var bytes = new byte[length];
                Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CFRelease(data);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Settings reads the key while the window is being built; a broken framework
            // load must cost the stored secret, not the app.
            return null;
        }
    }

    public void Write(string name, string value)
    {
        using var cf = new CFScope();
        var k = Keys.Load();
        var data = cf.Data(Encoding.UTF8.GetBytes(value));

        // The service name is the label the security tool gave its items by default, so
        // old and new entries look the same in Keychain Access.
        var status = SecItemAdd(
            Query(cf, name, (k.Label, cf.String(Service)), (k.ValueData, data)), IntPtr.Zero);

        if (status == ErrSecDuplicateItem)
            status = SecItemUpdate(Query(cf, name), cf.Dictionary((k.ValueData, data)));

        if (status != ErrSecSuccess)
            throw Failure("Could not save to the macOS keychain", status);
    }

    public void Delete(string name)
    {
        using var cf = new CFScope();
        var status = SecItemDelete(Query(cf, name));

        if (status is not (ErrSecSuccess or ErrSecItemNotFound))
            throw Failure("Could not remove it from the macOS keychain", status);
    }

    private static IntPtr Query(CFScope cf, string name, params (IntPtr Key, IntPtr Value)[] extra)
    {
        var k = Keys.Load();
        var identity = new[]
        {
            (k.Class, k.GenericPassword),
            (k.Service, cf.String(Service)),
            (k.Account, cf.String(name))
        };
        return cf.Dictionary(identity.Concat(extra).ToArray());
    }

    private static CryptographicException Failure(string what, int status)
    {
        var reason = Describe(status);
        return new CryptographicException(reason is null
            ? $"{what} (error {status})."
            : $"{what}: {reason} (error {status}).");
    }

    private static string? Describe(int status)
    {
        var message = SecCopyErrorMessageString(status, IntPtr.Zero);
        if (message == IntPtr.Zero) return null;

        try
        {
            var buffer = new char[checked((int)CFStringGetLength(message))];
            CFStringGetCharacters(message, new CFRange(0, buffer.Length), buffer);
            var text = new string(buffer).Trim().TrimEnd('.');
            return text.Length == 0 ? null : text;
        }
        finally
        {
            CFRelease(message);
        }
    }

    // ---- Security and CoreFoundation interop ----

    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>
    /// The CF objects created for one keychain call, released together when it ends.
    /// Dictionaries retain what they hold, so the order of release does not matter.
    /// </summary>
    private sealed class CFScope : IDisposable
    {
        private readonly List<IntPtr> _owned = new();

        public IntPtr String(string value) =>
            Own(CFStringCreateWithCharacters(IntPtr.Zero, value, value.Length));

        public IntPtr Data(byte[] bytes) =>
            Own(CFDataCreate(IntPtr.Zero, bytes, bytes.Length));

        public IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] entries)
        {
            var k = Keys.Load();
            var keys = new IntPtr[entries.Length];
            var values = new IntPtr[entries.Length];
            for (var i = 0; i < entries.Length; i++) (keys[i], values[i]) = entries[i];

            return Own(CFDictionaryCreate(IntPtr.Zero, keys, values, entries.Length,
                                          k.KeyCallBacks, k.ValueCallBacks));
        }

        private IntPtr Own(IntPtr cf)
        {
            // CoreFoundation's create functions return NULL only when allocation fails,
            // and CFRelease(NULL) would crash the process.
            if (cf == IntPtr.Zero) throw new OutOfMemoryException();
            _owned.Add(cf);
            return cf;
        }

        public void Dispose()
        {
            foreach (var cf in _owned) CFRelease(cf);
            _owned.Clear();
        }
    }

    /// <summary>
    /// The Keychain Services dictionary keys and values are exported as CFString globals,
    /// not functions, so DllImport cannot reach them; what those strings contain is not
    /// part of the API, which rules out spelling them out here.
    ///
    /// Loaded on first use rather than in a static initialiser, which would wrap a failed
    /// load in a TypeInitializationException and replay it on every later call. This way
    /// Read sees the loader's own exception, and the next call tries again.
    /// </summary>
    private sealed class Keys
    {
        private static Keys? _loaded;

        public readonly IntPtr Class, GenericPassword, Service, Account, Label, ValueData,
                               ReturnData, MatchLimit, MatchLimitOne, True,
                               KeyCallBacks, ValueCallBacks;

        private Keys()
        {
            var security = NativeLibrary.Load(SecurityFramework);
            var coreFoundation = NativeLibrary.Load(CoreFoundationFramework);

            Class = Global(security, "kSecClass");
            GenericPassword = Global(security, "kSecClassGenericPassword");
            Service = Global(security, "kSecAttrService");
            Account = Global(security, "kSecAttrAccount");
            Label = Global(security, "kSecAttrLabel");
            ValueData = Global(security, "kSecValueData");
            ReturnData = Global(security, "kSecReturnData");
            MatchLimit = Global(security, "kSecMatchLimit");
            MatchLimitOne = Global(security, "kSecMatchLimitOne");
            True = Global(coreFoundation, "kCFBooleanTrue");

            // These two are structs that CFDictionaryCreate takes by address, so the
            // address of the export is the argument, not something read from it.
            KeyCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks");
            ValueCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks");
        }

        public static Keys Load() => _loaded ??= new Keys();

        private static IntPtr Global(IntPtr library, string symbol) =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(library, symbol));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CFRange
    {
        public readonly nint Location;
        public readonly nint Length;

        public CFRange(nint location, nint length)
        {
            Location = location;
            Length = length;
        }
    }

    [DllImport(SecurityFramework)]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(SecurityFramework)]
    private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [DllImport(SecurityFramework)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(SecurityFramework)]
    private static extern int SecItemDelete(IntPtr query);

    [DllImport(SecurityFramework)]
    private static extern IntPtr SecCopyErrorMessageString(int status, IntPtr reserved);

    [DllImport(CoreFoundationFramework, CharSet = CharSet.Unicode)]
    private static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, string characters, nint length);

    [DllImport(CoreFoundationFramework)]
    private static extern nint CFStringGetLength(IntPtr text);

    [DllImport(CoreFoundationFramework, CharSet = CharSet.Unicode)]
    private static extern void CFStringGetCharacters(IntPtr text, CFRange range, [Out] char[] buffer);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CoreFoundationFramework)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values,
        nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr cf);
}

/// <summary>
/// Linux: <c>secret-tool</c> when a keyring is present, otherwise an AES-GCM file
/// encrypted with a key derived from the machine ID and the user name.
///
/// The fallback is honest about what it is — it protects against a stray backup or a
/// grep, not against another process running as the same user. That is still strictly
/// better than the plain-text file it replaces, and the UI says so on the settings
/// screen rather than implying a guarantee it cannot make.
/// </summary>
internal sealed class LinuxSecretStore : ISecretStore
{
    private const string Schema = "org.steampipestudio.Secret";
    private readonly string _directory;

    public LinuxSecretStore(string directory)
    {
        _directory = Path.Combine(directory, "secrets");
        Directory.CreateDirectory(_directory);
    }

    public string? Read(string name)
    {
        if (TrySecretTool("lookup", name, null, out var value)) return value;

        var path = PathFor(name);
        if (!File.Exists(path)) return null;

        try
        {
            return Decrypt(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is CryptographicException or IOException or ArgumentException)
        {
            return null;
        }
    }

    public void Write(string name, string value)
    {
        if (TrySecretTool("store", name, value, out _)) return;

        var path = PathFor(name);
        File.WriteAllBytes(path, Encrypt(value));

        // Guarded rather than suppressed: this type is only constructed on non-Windows,
        // but nothing in the type system says so, and the platform analyser is right to
        // ask. Owner-only permissions are the whole point of the file fallback.
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception e) when (e is IOException or PlatformNotSupportedException) { }
        }
    }

    public void Delete(string name)
    {
        TrySecretTool("clear", name, null, out _);
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string name) => Path.Combine(_directory, name + ".enc");

    private static bool TrySecretTool(string verb, string name, string? value, out string? output)
    {
        output = null;

        var arguments = verb switch
        {
            "lookup" => new[] { "lookup", "schema", Schema, "name", name },
            "clear" => new[] { "clear", "schema", Schema, "name", name },
            _ => new[] { "store", "--label=SteamPipe Studio", "schema", Schema, "name", name }
        };

        var startInfo = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = verb == "store",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return false;

            if (verb == "store" && value is not null)
            {
                process.StandardInput.Write(value);
                process.StandardInput.Close();
            }

            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5_000)) return false;
            if (process.ExitCode != 0) return false;

            output = stdout.TrimEnd('\n');
            return verb != "lookup" || !string.IsNullOrEmpty(output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false; // secret-tool not installed
        }
    }

    // ---- AES-GCM fallback ----

    private static byte[] DeriveKey()
    {
        var machineId = ReadFirstLine("/etc/machine-id")
                        ?? ReadFirstLine("/var/lib/dbus/machine-id")
                        ?? Environment.MachineName;

        var material = $"{machineId}|{Environment.UserName}|SteamPipeStudio";

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(material),
            Encoding.UTF8.GetBytes("steampipe-studio-v1"),
            iterations: 100_000,
            HashAlgorithmName.SHA256,
            outputLength: 32);
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;
        }
        catch (IOException) { return null; }
    }

    private static byte[] Encrypt(string plaintext)
    {
        var key = DeriveKey();
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var result = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, result, nonce.Length + tag.Length, cipher.Length);
        return result;
    }

    private static string Decrypt(byte[] payload)
    {
        var nonceLength = AesGcm.NonceByteSizes.MaxSize;
        var tagLength = AesGcm.TagByteSizes.MaxSize;

        if (payload.Length < nonceLength + tagLength)
            throw new CryptographicException("Secret file is truncated.");

        var nonce = payload.AsSpan(0, nonceLength);
        var tag = payload.AsSpan(nonceLength, tagLength);
        var cipher = payload.AsSpan(nonceLength + tagLength);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(DeriveKey(), tagLength);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
