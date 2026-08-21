using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SteamPipeStudio.Core.Model;
using SteamPipeStudio.Core.Vdf;

namespace SteamPipeStudio.Core.Build;

/// <summary>A generated build script and the path it should be written to.</summary>
public sealed record GeneratedScript(string FileName, string Contents);

/// <summary>
/// Turns a <see cref="BuildProfile"/> into the <c>app_build_&lt;appid&gt;.vdf</c> and
/// <c>depot_build_&lt;depotid&gt;.vdf</c> files steamcmd consumes, and reads them back.
///
/// Layout choice: depots always get their own file, even when there is only one.
/// Valve's <c>simple_app_build.vdf</c> inlines a single depot, but a separate file per
/// depot keeps diffs readable in version control and means adding a second depot never
/// requires restructuring the first.
/// </summary>
public static class BuildScriptGenerator
{
    public static string AppScriptName(uint appId) => $"app_build_{appId}.vdf";

    public static string DepotScriptName(uint depotId) => $"depot_build_{depotId}.vdf";

    /// <summary>
    /// Generates every script for the profile. Paths are written absolute and with
    /// forward slashes: the scripts are generated into a working folder that is not
    /// necessarily the SDK's <c>scripts</c> directory, so relative paths like Valve's
    /// <c>..\content\</c> would resolve against the wrong parent.
    /// </summary>
    public static IReadOnlyList<GeneratedScript> Generate(BuildProfile profile)
    {
        var scripts = new List<GeneratedScript>();
        var enabledDepots = profile.Depots.Where(d => d.Enabled).ToList();

        foreach (var depot in enabledDepots)
            scripts.Add(new GeneratedScript(DepotScriptName(depot.DepotId),
                                            VdfWriter.Write(BuildDepotNode(depot))));

        scripts.Insert(0, new GeneratedScript(AppScriptName(profile.AppId),
                                              VdfWriter.Write(BuildAppNode(profile, enabledDepots))));
        return scripts;
    }

    /// <summary>Writes the generated scripts into <paramref name="directory"/> and returns the app script path.</summary>
    public static string WriteTo(BuildProfile profile, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var script in Generate(profile))
            File.WriteAllText(Path.Combine(directory, script.FileName), script.Contents,
                              new System.Text.UTF8Encoding(false));

        return Path.Combine(directory, AppScriptName(profile.AppId));
    }

    public static VdfNode BuildAppNode(BuildProfile profile, IReadOnlyList<DepotDefinition>? depots = null)
    {
        depots ??= profile.Depots.Where(d => d.Enabled).ToList();

        var root = VdfNode.Block("AppBuild");
        root.Add("AppID", VdfWriter.Number(profile.AppId));
        root.Add("Desc", SanitiseDescription(profile.Description));
        root.Add("ContentRoot", VdfWriter.NormalisePath(profile.ContentRoot));
        root.Add("BuildOutput", VdfWriter.NormalisePath(profile.BuildOutput));
        root.Add("Preview", VdfWriter.Bool(profile.Preview));
        root.Add("Verbose", VdfWriter.Bool(profile.Verbose));

        if (!string.IsNullOrWhiteSpace(profile.LocalContentServerPath))
            root.Add("Local", VdfWriter.NormalisePath(profile.LocalContentServerPath));

        // Steam rejects SetLive on the default branch; only beta branches auto-publish.
        if (!string.IsNullOrWhiteSpace(profile.SetLiveBranch) && !IsDefaultBranch(profile.SetLiveBranch))
            root.Add("SetLive", profile.SetLiveBranch.Trim());

        var depotsBlock = root.AddBlock("Depots");
        foreach (var depot in depots)
            depotsBlock.Add(VdfWriter.Number(depot.DepotId), DepotScriptName(depot.DepotId));

        return root;
    }

    public static VdfNode BuildDepotNode(DepotDefinition depot)
    {
        var root = VdfNode.Block("DepotBuild");
        root.Add("DepotID", VdfWriter.Number(depot.DepotId));

        if (!string.IsNullOrWhiteSpace(depot.ContentRootOverride))
            root.Add("ContentRoot", VdfWriter.NormalisePath(depot.ContentRootOverride));

        foreach (var mapping in depot.FileMappings)
        {
            var block = root.AddBlock("FileMapping");
            block.Add("LocalPath", VdfWriter.NormalisePath(mapping.LocalPath));
            block.Add("DepotPath", VdfWriter.NormalisePath(mapping.DepotPath));

            // "Recursive" only has meaning when LocalPath contains a wildcard.
            if (mapping.Recursive && ContainsWildcard(mapping.LocalPath))
                block.Add("Recursive", "1");
        }

        foreach (var exclusion in depot.FileExclusions.Where(e => !string.IsNullOrWhiteSpace(e)))
            root.Add("FileExclusion", VdfWriter.NormalisePath(exclusion));

        if (!string.IsNullOrWhiteSpace(depot.InstallScript))
            root.Add("InstallScript", VdfWriter.NormalisePath(depot.InstallScript));

        foreach (var property in depot.FileProperties.Where(p => !string.IsNullOrWhiteSpace(p.LocalPath)))
        {
            var block = root.AddBlock("FileProperties");
            block.Add("LocalPath", VdfWriter.NormalisePath(property.LocalPath));
            block.Add("Attributes", property.Attributes);
        }

        return root;
    }

    // ------------------------------------------------------------------
    // Import — read an existing SDK build script back into a profile
    // ------------------------------------------------------------------

    /// <summary>
    /// Imports an existing <c>app_build_*.vdf</c>. Depots are read from inline blocks
    /// or from the sibling depot scripts they reference, so both of Valve's sample
    /// layouts round-trip.
    /// </summary>
    public static BuildProfile ImportAppScript(string appScriptPath)
    {
        var root = VdfParser.ParseFile(appScriptPath);
        if (!string.Equals(root.Key, "AppBuild", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Expected an 'AppBuild' script, found '{root.Key}' in {Path.GetFileName(appScriptPath)}.");

        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(appScriptPath)) ?? ".";

        var profile = new BuildProfile
        {
            Name = Path.GetFileNameWithoutExtension(appScriptPath),
            AppId = root.GetUInt("AppID"),
            Description = root.GetString("Desc", string.Empty),
            ContentRoot = ResolveAgainst(baseDirectory, root.GetString("ContentRoot", string.Empty)),
            BuildOutput = ResolveAgainst(baseDirectory, root.GetString("BuildOutput", string.Empty)),
            Preview = root.GetBool("Preview"),
            Verbose = root.GetBool("Verbose"),
            LocalContentServerPath = ResolveAgainst(baseDirectory, root.GetString("Local", string.Empty)),
            SetLiveBranch = root.GetString("SetLive", string.Empty)
        };

        var depotsBlock = root.Find("Depots");
        if (depotsBlock is null) return profile;

        foreach (var entry in depotsBlock.Children)
        {
            if (!uint.TryParse(entry.Key, out var depotId)) continue;

            if (entry.IsBlock)
            {
                profile.Depots.Add(ReadDepot(entry, depotId));
                continue;
            }

            var referenced = Path.Combine(baseDirectory, entry.Value ?? string.Empty);
            if (File.Exists(referenced))
            {
                var depotRoot = VdfParser.ParseFile(referenced);
                profile.Depots.Add(ReadDepot(depotRoot, depotId));
            }
            else
            {
                // Keep the depot with its ID so the user can repoint it, rather than
                // dropping it silently because a referenced file has moved.
                profile.Depots.Add(new DepotDefinition
                {
                    DepotId = depotId,
                    Label = $"missing script: {entry.Value}"
                });
            }
        }

        return profile;
    }

    private static DepotDefinition ReadDepot(VdfNode node, uint fallbackDepotId)
    {
        var depot = new DepotDefinition
        {
            DepotId = node.GetUInt("DepotID", fallbackDepotId),
            ContentRootOverride = node.GetString("ContentRoot", string.Empty),
            InstallScript = node.GetString("InstallScript", string.Empty),
            FileMappings = new List<FileMappingRule>()
        };

        foreach (var mapping in node.FindAll("FileMapping").Where(m => m.IsBlock))
        {
            depot.FileMappings.Add(new FileMappingRule
            {
                LocalPath = mapping.GetString("LocalPath", "*"),
                DepotPath = mapping.GetString("DepotPath", "."),
                Recursive = mapping.GetBool("Recursive")
            });
        }

        if (depot.FileMappings.Count == 0)
            depot.FileMappings.Add(new FileMappingRule());

        foreach (var exclusion in node.FindAll("FileExclusion").Where(e => !e.IsBlock))
            depot.FileExclusions.Add(exclusion.Value ?? string.Empty);

        foreach (var property in node.FindAll("FileProperties").Where(p => p.IsBlock))
        {
            depot.FileProperties.Add(new FilePropertyRule
            {
                LocalPath = property.GetString("LocalPath", string.Empty),
                Attributes = property.GetString("Attributes", "userconfig")
            });
        }

        return depot;
    }

    // ------------------------------------------------------------------

    internal static bool ContainsWildcard(string path) =>
        path.Contains('*') || path.Contains('?');

    internal static bool IsDefaultBranch(string branch) =>
        branch.Trim().Equals("public", StringComparison.OrdinalIgnoreCase) ||
        branch.Trim().Equals("default", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Newlines and quotes in a build description would corrupt the script; Steam also
    /// truncates long descriptions in the admin panel, so cap the length here.
    /// </summary>
    internal static string SanitiseDescription(string description)
    {
        var flattened = description.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'').Trim();
        return flattened.Length <= 250 ? flattened : flattened[..250];
    }

    private static string ResolveAgainst(string baseDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var normalised = path.Replace('\\', Path.DirectorySeparatorChar)
                             .Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(normalised)
            ? Path.GetFullPath(normalised)
            : Path.GetFullPath(Path.Combine(baseDirectory, normalised));
    }
}
