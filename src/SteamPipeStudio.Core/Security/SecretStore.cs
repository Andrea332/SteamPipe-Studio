using System;
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

/// <summary>macOS: the login keychain, via the <c>security</c> command line tool.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychainSecretStore : ISecretStore
{
    private const string Service = "SteamPipeStudio";

    public string? Read(string name)
    {
        var (exitCode, stdout) = Run("find-generic-password", "-s", Service, "-a", name, "-w");
        return exitCode == 0 ? stdout.TrimEnd('\n') : null;
    }

    public void Write(string name, string value) =>
        Run("add-generic-password", "-U", "-s", Service, "-a", name, "-w", value);

    public void Delete(string name) =>
        Run("delete-generic-password", "-s", Service, "-a", name);

    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return (-1, string.Empty);

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return (process.ExitCode, output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty);
        }
    }
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
