using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using SteamPipeStudio.Core.Model;

namespace SteamPipeStudio.Core.Build;

public sealed record PreflightFile(string RelativePath, string DepotPath, long Length);

public sealed record DepotPreflight(
    uint DepotId,
    IReadOnlyList<PreflightFile> Files,
    IReadOnlyList<string> Notes)
{
    public long TotalBytes => Files.Sum(f => f.Length);
    public int FileCount => Files.Count;
}

public sealed record PreflightResult(IReadOnlyList<DepotPreflight> Depots)
{
    public long TotalBytes => Depots.Sum(d => d.TotalBytes);
    public int FileCount => Depots.Sum(d => d.FileCount);
}

/// <summary>
/// Resolves a profile's file mappings against the real filesystem so the user can see
/// exactly which files a build would contain, and how large it is, before uploading.
///
/// This re-implements SteamPipe's matching rules and is therefore an approximation:
/// steamcmd remains the authority. It exists to catch the two mistakes that cost the
/// most time — a mapping that silently matches nothing, and debug symbols shipping to
/// customers — not to be a bit-exact simulation.
/// </summary>
public static class ContentPreflight
{
    /// <summary>Patterns that almost never belong in a shipped build.</summary>
    private static readonly (string Pattern, string Reason)[] SuspiciousPatterns =
    {
        ("*.pdb",              "debug symbols"),
        ("*.ilk",              "incremental linker output"),
        ("*.exp",              "linker export file"),
        ("*.lib",              "static library"),
        ("*_BurstDebugInformation_DoNotShip/*", "Unity Burst debug data"),
        ("*_BackUpThisFolder_ButDontShipItWithYourGame/*", "Unity symbol backup"),
        ("*.log",              "log file"),
        ("*.map",              "linker map"),
        (".git/*",             "version control metadata"),
        (".vs/*",              "IDE metadata"),
        ("Thumbs.db",          "Explorer thumbnail cache"),
        (".DS_Store",          "Finder metadata")
    };

    public static PreflightResult Run(BuildProfile profile, CancellationToken cancellation = default)
    {
        var depots = new List<DepotPreflight>();

        foreach (var depot in profile.Depots.Where(d => d.Enabled))
        {
            cancellation.ThrowIfCancellationRequested();

            var root = string.IsNullOrWhiteSpace(depot.ContentRootOverride)
                ? profile.ContentRoot
                : depot.ContentRootOverride;

            depots.Add(RunDepot(depot, root, cancellation));
        }

        return new PreflightResult(depots);
    }

    private static DepotPreflight RunDepot(DepotDefinition depot, string contentRoot,
                                           CancellationToken cancellation)
    {
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(contentRoot) || !Directory.Exists(contentRoot))
        {
            notes.Add($"Content root not found: {contentRoot}");
            return new DepotPreflight(depot.DepotId, Array.Empty<PreflightFile>(), notes);
        }

        // Relative path -> file. A dictionary because two mappings may match the same
        // file; SteamPipe takes the last mapping to win, so later writes overwrite.
        var selected = new Dictionary<string, PreflightFile>(PathComparer);
        var allFiles = EnumerateRelative(contentRoot, cancellation).ToList();

        foreach (var mapping in depot.FileMappings)
        {
            cancellation.ThrowIfCancellationRequested();
            var matched = 0;

            foreach (var relative in allFiles)
            {
                if (!MatchesMapping(relative, mapping)) continue;

                var depotPath = ToDepotPath(relative, mapping);
                var fullPath = Path.Combine(contentRoot, relative);
                long length;
                try { length = new FileInfo(fullPath).Length; }
                catch (IOException) { continue; }

                selected[relative] = new PreflightFile(relative, depotPath, length);
                matched++;
            }

            if (matched == 0)
                notes.Add($"Mapping '{mapping.LocalPath}' matched no files.");
        }

        foreach (var exclusion in depot.FileExclusions.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            var removed = selected.Keys.Where(k => MatchesExclusion(k, exclusion)).ToList();
            foreach (var key in removed) selected.Remove(key);

            if (removed.Count == 0)
                notes.Add($"Exclusion '{exclusion}' matched no files.");
        }

        foreach (var (pattern, reason) in SuspiciousPatterns)
        {
            var hits = selected.Keys.Count(k => MatchesExclusion(k, pattern));
            if (hits > 0)
                notes.Add($"{hits} file(s) matching '{pattern}' ({reason}) would be uploaded.");
        }

        if (selected.Count == 0)
            notes.Add("This depot would be uploaded empty.");

        var files = selected.Values.OrderBy(f => f.DepotPath, StringComparer.OrdinalIgnoreCase).ToList();
        return new DepotPreflight(depot.DepotId, files, notes);
    }

    // ------------------------------------------------------------------
    // Matching
    // ------------------------------------------------------------------

    private static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static IEnumerable<string> EnumerateRelative(string root, CancellationToken cancellation)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Following reparse points can loop forever and is never what a build wants.
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var full in Directory.EnumerateFiles(root, "*", options))
        {
            cancellation.ThrowIfCancellationRequested();
            yield return Normalise(Path.GetRelativePath(root, full));
        }
    }

    internal static string Normalise(string path) => path.Replace('\\', '/').TrimStart('.', '/');

    internal static bool MatchesMapping(string relativePath, FileMappingRule mapping)
    {
        var pattern = Normalise(mapping.LocalPath);
        if (pattern.Length == 0) return false;

        // A bare "*" means the whole content root.
        if (pattern == "*") return mapping.Recursive || !relativePath.Contains('/');

        // No wildcard: an exact file, or every file under a named folder.
        if (!BuildScriptGenerator.ContainsWildcard(pattern))
        {
            if (string.Equals(relativePath, pattern, PathComparison)) return true;
            return relativePath.StartsWith(pattern.TrimEnd('/') + "/", PathComparison);
        }

        var directory = GetDirectoryPart(pattern);
        var leaf = GetLeafPart(pattern);

        if (directory.Length > 0)
        {
            if (!relativePath.StartsWith(directory + "/", PathComparison)) return false;
            relativePath = relativePath[(directory.Length + 1)..];
        }

        // Without Recursive, the wildcard only applies to the immediate level.
        if (!mapping.Recursive && relativePath.Contains('/')) return false;

        var fileName = relativePath.Contains('/')
            ? relativePath[(relativePath.LastIndexOf('/') + 1)..]
            : relativePath;

        return GlobMatches(fileName, leaf);
    }

    /// <summary>
    /// FileExclusion is matched against the whole relative path, and its wildcards cross
    /// directory separators — that is what makes Valve's two documented examples work:
    /// <c>*.pdb</c> removes symbols at every depth, and <c>bin/tools*</c> removes a whole
    /// subtree. Mapping wildcards behave differently (they stay within one directory
    /// level and rely on <c>Recursive</c>), so the two must not share matching rules.
    /// </summary>
    internal static bool MatchesExclusion(string relativePath, string exclusion)
    {
        var pattern = Normalise(exclusion);
        if (pattern.Length == 0) return false;

        if (!BuildScriptGenerator.ContainsWildcard(pattern))
        {
            if (string.Equals(relativePath, pattern, PathComparison)) return true;
            return relativePath.StartsWith(pattern.TrimEnd('/') + "/", PathComparison);
        }

        return GlobMatches(relativePath, pattern, crossesDirectories: true);
    }

    private static string GetDirectoryPart(string pattern)
    {
        var index = pattern.LastIndexOf('/');
        return index < 0 ? string.Empty : pattern[..index];
    }

    private static string GetLeafPart(string pattern)
    {
        var index = pattern.LastIndexOf('/');
        return index < 0 ? pattern : pattern[(index + 1)..];
    }

    private static readonly Dictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);

    internal static bool GlobMatches(string input, string pattern, bool crossesDirectories = false)
    {
        var cacheKey = (crossesDirectories ? "**:" : "*:") + pattern;
        Regex regex;

        lock (RegexCache)
        {
            if (!RegexCache.TryGetValue(cacheKey, out regex!))
            {
                var options = RegexOptions.CultureInvariant;
                if (!OperatingSystem.IsLinux()) options |= RegexOptions.IgnoreCase;

                var anything = crossesDirectories ? ".*" : "[^/]*";
                var anyChar = crossesDirectories ? "." : "[^/]";

                var translated = "^" + Regex.Escape(pattern)
                    .Replace("\\*", anything)
                    .Replace("\\?", anyChar) + "$";

                regex = new Regex(translated, options, TimeSpan.FromSeconds(1));
                RegexCache[cacheKey] = regex;
            }
        }

        try { return regex.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static string ToDepotPath(string relativePath, FileMappingRule mapping)
    {
        var target = Normalise(mapping.DepotPath).Trim('/');
        var pattern = Normalise(mapping.LocalPath);

        // Strip the mapping's fixed directory prefix so "bin/*" -> "executables/"
        // lands files at executables/<name> rather than executables/bin/<name>.
        var prefix = BuildScriptGenerator.ContainsWildcard(pattern)
            ? GetDirectoryPart(pattern)
            : (relativePath.StartsWith(pattern.TrimEnd('/') + "/", PathComparison)
                ? pattern.TrimEnd('/')
                : GetDirectoryPart(pattern));

        var tail = relativePath;
        if (prefix.Length > 0 && tail.StartsWith(prefix + "/", PathComparison))
            tail = tail[(prefix.Length + 1)..];

        return target.Length == 0 ? tail : target + "/" + tail;
    }

    /// <summary>
    /// Formats a size for display, always with a dot as the decimal separator.
    ///
    /// Invariant on purpose, not by accident. The previous version used whatever
    /// <see cref="CultureInfo.CurrentCulture"/> happened to be, which meant an Italian
    /// machine rendered "1,5 KB" and an American one "1.5 KB" from identical input —
    /// a build size that changes shape depending on who is looking at it is a nuisance
    /// in screenshots, bug reports and CI logs. The desktop app also runs with
    /// InvariantGlobalization enabled, so a culture-sensitive format here would have
    /// disagreed with the app's own behaviour anyway.
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }

        return unit == 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} {1}", bytes, units[unit])
            : string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[unit]);
    }
}
