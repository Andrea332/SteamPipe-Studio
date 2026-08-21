using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SteamPipeStudio.Core.Model;

namespace SteamPipeStudio.Core.Build;

public enum IssueSeverity
{
    Info,
    Warning,
    /// <summary>Blocks the upload.</summary>
    Error
}

public sealed record ValidationIssue(IssueSeverity Severity, string Field, string Message)
{
    public override string ToString() => $"[{Severity}] {Field}: {Message}";
}

/// <summary>
/// Checks a profile before steamcmd is ever launched.
///
/// The point is to fail in a dialog in half a second rather than fifteen minutes into
/// an upload, which is the failure mode that makes the SteamPipe workflow unpleasant.
/// </summary>
public static class BuildValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(BuildProfile profile, AppSettings? settings = null)
    {
        var issues = new List<ValidationIssue>();

        if (profile.AppId == 0)
            issues.Add(new ValidationIssue(IssueSeverity.Error, "AppID",
                "Set the App ID assigned to your title in Steamworks."));

        if (string.IsNullOrWhiteSpace(profile.Description))
            issues.Add(new ValidationIssue(IssueSeverity.Warning, "Description",
                "An empty build description makes builds hard to tell apart in the admin panel."));

        ValidateContentRoot(profile, issues);
        ValidateBuildOutput(profile, issues);
        ValidateDepots(profile, issues);
        ValidateBranch(profile, issues);
        ValidateContentBuilder(profile, settings, issues);

        if (string.IsNullOrWhiteSpace(profile.SteamAccountName))
            issues.Add(new ValidationIssue(IssueSeverity.Error, "Account",
                "Set the Steam account used for uploading."));

        return issues;
    }

    public static bool HasBlockingIssues(IEnumerable<ValidationIssue> issues) =>
        issues.Any(i => i.Severity == IssueSeverity.Error);

    private static void ValidateContentRoot(BuildProfile profile, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(profile.ContentRoot))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "ContentRoot",
                "Choose the folder holding the build to upload."));
            return;
        }

        if (!Directory.Exists(profile.ContentRoot))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "ContentRoot",
                $"Folder does not exist: {profile.ContentRoot}"));
            return;
        }

        if (!Directory.EnumerateFileSystemEntries(profile.ContentRoot).Any())
            issues.Add(new ValidationIssue(IssueSeverity.Error, "ContentRoot",
                "The content folder is empty. Uploading it would publish a build with no files."));
    }

    private static void ValidateBuildOutput(BuildProfile profile, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(profile.BuildOutput))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "BuildOutput",
                "Choose a folder for build logs and the chunk cache."));
            return;
        }

        // The classic self-inflicted wound: the cache lands inside the content folder
        // and every subsequent build uploads its own previous cache.
        if (IsInside(profile.BuildOutput, profile.ContentRoot))
            issues.Add(new ValidationIssue(IssueSeverity.Error, "BuildOutput",
                "The build output folder is inside the content folder, so its logs and " +
                "chunk cache would be uploaded as part of your game. Move it outside."));

        if (IsInside(profile.ContentRoot, profile.BuildOutput))
            issues.Add(new ValidationIssue(IssueSeverity.Error, "ContentRoot",
                "The content folder is inside the build output folder."));
    }

    private static void ValidateDepots(BuildProfile profile, List<ValidationIssue> issues)
    {
        var enabled = profile.Depots.Where(d => d.Enabled).ToList();

        if (enabled.Count == 0)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "Depots",
                "Add at least one enabled depot."));
            return;
        }

        foreach (var duplicate in enabled.GroupBy(d => d.DepotId).Where(g => g.Count() > 1))
            issues.Add(new ValidationIssue(IssueSeverity.Error, "Depots",
                $"Depot {duplicate.Key} is listed more than once."));

        foreach (var depot in enabled)
        {
            if (depot.DepotId == 0)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "Depots",
                    "A depot has no ID. Depot IDs come from the Steamworks admin panel."));
                continue;
            }

            if (profile.AppId != 0 && depot.DepotId < profile.AppId)
                issues.Add(new ValidationIssue(IssueSeverity.Warning, $"Depot {depot.DepotId}",
                    $"Depot ID is lower than App ID {profile.AppId}. Depots are normally " +
                    "allocated just above the App ID — double-check you copied the right number."));

            if (!string.IsNullOrWhiteSpace(depot.ContentRootOverride) &&
                !Directory.Exists(depot.ContentRootOverride))
                issues.Add(new ValidationIssue(IssueSeverity.Error, $"Depot {depot.DepotId}",
                    $"Content root override does not exist: {depot.ContentRootOverride}"));

            if (depot.FileMappings.Count == 0)
                issues.Add(new ValidationIssue(IssueSeverity.Error, $"Depot {depot.DepotId}",
                    "Add at least one file mapping."));

            foreach (var mapping in depot.FileMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.LocalPath))
                    issues.Add(new ValidationIssue(IssueSeverity.Error, $"Depot {depot.DepotId}",
                        "A file mapping has an empty local path."));

                if (string.IsNullOrWhiteSpace(mapping.DepotPath))
                    issues.Add(new ValidationIssue(IssueSeverity.Error, $"Depot {depot.DepotId}",
                        "A file mapping has an empty depot path. Use '.' for the depot root."));

                if (Path.IsPathRooted(mapping.DepotPath))
                    issues.Add(new ValidationIssue(IssueSeverity.Error, $"Depot {depot.DepotId}",
                        $"Depot path '{mapping.DepotPath}' must be relative to the game's " +
                        "install folder, not an absolute path."));
            }

            if (!string.IsNullOrWhiteSpace(depot.InstallScript))
            {
                var root = string.IsNullOrWhiteSpace(depot.ContentRootOverride)
                    ? profile.ContentRoot
                    : depot.ContentRootOverride;

                if (!string.IsNullOrWhiteSpace(root) &&
                    !File.Exists(Path.Combine(root, depot.InstallScript)))
                    issues.Add(new ValidationIssue(IssueSeverity.Warning, $"Depot {depot.DepotId}",
                        $"Install script '{depot.InstallScript}' was not found under the content root."));
            }
        }
    }

    private static void ValidateBranch(BuildProfile profile, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(profile.SetLiveBranch)) return;

        if (BuildScriptGenerator.IsDefaultBranch(profile.SetLiveBranch))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, "SetLive",
                "Steam does not allow a build script to set the default branch live. " +
                "The build will upload, and you can promote it from the Builds tab afterwards."));
            return;
        }

        if (profile.SetLiveBranch.Any(char.IsWhiteSpace))
            issues.Add(new ValidationIssue(IssueSeverity.Error, "SetLive",
                "Branch names cannot contain spaces."));

        if (profile.Preview)
            issues.Add(new ValidationIssue(IssueSeverity.Warning, "Preview",
                "Preview mode is on, so nothing is uploaded and the branch will not change."));
    }

    private static void ValidateContentBuilder(BuildProfile profile, AppSettings? settings,
                                               List<ValidationIssue> issues)
    {
        var path = !string.IsNullOrWhiteSpace(profile.ContentBuilderPathOverride)
            ? profile.ContentBuilderPathOverride
            : settings?.ContentBuilderPath ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "ContentBuilder",
                "Point Settings at the ContentBuilder folder of your Steamworks SDK."));
            return;
        }

        if (Steam.SteamCmdLocator.TryLocate(path, out _, out var error)) return;
        issues.Add(new ValidationIssue(IssueSeverity.Error, "ContentBuilder", error));
    }

    /// <summary>True when <paramref name="candidate"/> sits inside <paramref name="parent"/>.</summary>
    internal static bool IsInside(string candidate, string parent)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(parent)) return false;

        try
        {
            var child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));

            var comparison = OperatingSystem.IsLinux()
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (child.Equals(root, comparison)) return true;
            return child.StartsWith(root + Path.DirectorySeparatorChar, comparison);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
