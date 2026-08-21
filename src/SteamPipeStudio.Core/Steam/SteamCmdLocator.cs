using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SteamPipeStudio.Core.Steam;

/// <summary>
/// Finds the steamcmd binary inside a Steamworks SDK ContentBuilder folder.
///
/// The SDK ships four builder folders and the correct one depends on the machine
/// running the upload, not on the platform being shipped — you can upload a Windows
/// depot from a Mac. The original SteamPipeGUI was Windows-only and hard-coded
/// <c>builder\steamcmd.exe</c>.
/// </summary>
public static class SteamCmdLocator
{
    /// <summary>Relative locations of steamcmd within a ContentBuilder folder, per host OS.</summary>
    public static string[] CandidateRelativePaths()
    {
        if (OperatingSystem.IsWindows())
            return new[] { Path.Combine("builder", "steamcmd.exe") };

        if (OperatingSystem.IsMacOS())
            return new[] { Path.Combine("builder_osx", "steamcmd.sh") };

        // Linux: pick by process architecture, since the SDK ships x86 and arm64 builders.
        return RuntimeInformation.ProcessArchitecture is Architecture.Arm64
            ? new[]
            {
                Path.Combine("builder_linuxarm64", "steamcmd.sh"),
                Path.Combine("builder_linux", "steamcmd.sh")
            }
            : new[]
            {
                Path.Combine("builder_linux", "steamcmd.sh"),
                Path.Combine("builder_linuxarm64", "steamcmd.sh")
            };
    }

    public static bool TryLocate(string contentBuilderPath, out string steamCmdPath, out string error)
    {
        steamCmdPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(contentBuilderPath))
        {
            error = "No ContentBuilder folder is configured.";
            return false;
        }

        var root = contentBuilderPath.Trim();

        // Be forgiving about what the user dragged in: accept the SDK root, the tools
        // folder, or the ContentBuilder folder itself.
        foreach (var candidateRoot in new[]
                 {
                     root,
                     Path.Combine(root, "ContentBuilder"),
                     Path.Combine(root, "tools", "ContentBuilder"),
                     Path.Combine(root, "sdk", "tools", "ContentBuilder")
                 })
        {
            if (!Directory.Exists(candidateRoot)) continue;

            foreach (var relative in CandidateRelativePaths())
            {
                var full = Path.Combine(candidateRoot, relative);
                if (!File.Exists(full)) continue;

                steamCmdPath = Path.GetFullPath(full);
                return true;
            }
        }

        if (!Directory.Exists(root))
        {
            error = $"Folder does not exist: {root}";
            return false;
        }

        error = $"No steamcmd found under {root}. Expected {string.Join(" or ", CandidateRelativePaths())} " +
                "inside the SDK's tools/ContentBuilder folder.";
        return false;
    }

    /// <summary>
    /// On macOS and Linux the SDK ships shell scripts that git and zip archives happily
    /// strip the execute bit from. Restore it rather than failing with a confusing
    /// "Permission denied" from the process launcher.
    /// </summary>
    public static void EnsureExecutable(string steamCmdPath)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            var mode = File.GetUnixFileMode(steamCmdPath);
            var wanted = mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute;
            if (mode != wanted) File.SetUnixFileMode(steamCmdPath, wanted);

            // The script execs a sibling binary that needs the bit too.
            var directory = Path.GetDirectoryName(steamCmdPath);
            if (directory is null) return;

            foreach (var name in new[] { "steamcmd", "linux32/steamcmd", "linuxarm64/steamcmd" })
            {
                var binary = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(binary)) continue;

                var binaryMode = File.GetUnixFileMode(binary);
                File.SetUnixFileMode(binary, binaryMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort: if we cannot chmod, the launch error will say so clearly enough.
        }
    }
}
