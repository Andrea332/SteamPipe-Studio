using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SteamPipeStudio.Core.Model;

/// <summary>One file-mapping rule inside a depot.</summary>
public sealed class FileMappingRule
{
    /// <summary>Path relative to the depot's content root; may contain <c>*</c> and <c>?</c>.</summary>
    public string LocalPath { get; set; } = "*";

    /// <summary>Destination inside the depot; <c>.</c> is the depot root.</summary>
    public string DepotPath { get; set; } = ".";

    public bool Recursive { get; set; } = true;

    public FileMappingRule Clone() => new()
    {
        LocalPath = LocalPath,
        DepotPath = DepotPath,
        Recursive = Recursive
    };
}

/// <summary>
/// Marks a file whose contents Steam must not overwrite or must version separately.
/// Maps to the <c>FileProperties</c> block.
/// </summary>
public sealed class FilePropertyRule
{
    public string LocalPath { get; set; } = string.Empty;

    /// <summary><c>userconfig</c> or <c>versionedconfig</c>.</summary>
    public string Attributes { get; set; } = "userconfig";

    public FilePropertyRule Clone() => new() { LocalPath = LocalPath, Attributes = Attributes };
}

public sealed class DepotDefinition
{
    public uint DepotId { get; set; }

    /// <summary>Friendly label shown in the UI only; never written to the build script.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Optional per-depot override of the app-level content root.</summary>
    public string ContentRootOverride { get; set; } = string.Empty;

    /// <summary>
    /// Empty by default, deliberately. A default-populated list looks convenient but a
    /// C# collection initializer <em>appends</em> to it, so
    /// <c>new DepotDefinition { FileMappings = { rule } }</c> would silently produce two
    /// mappings — the intended one plus a catch-all <c>*</c> that uploads everything.
    /// Use <see cref="Create"/> when you want a depot that ships the whole content root.
    /// </summary>
    public List<FileMappingRule> FileMappings { get; set; } = new();

    /// <summary>Glob patterns excluded from this depot, e.g. <c>*.pdb</c>.</summary>
    public List<string> FileExclusions { get; set; } = new();

    /// <summary>Path to an install script VDF, if this depot ships one.</summary>
    public string InstallScript { get; set; } = string.Empty;

    public List<FilePropertyRule> FileProperties { get; set; } = new();

    /// <summary>Excluded from the build without deleting the depot's configuration.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A depot that ships everything under the content root — the usual starting point.</summary>
    public static DepotDefinition Create(uint depotId, string? label = null) => new()
    {
        DepotId = depotId,
        Label = label ?? string.Empty,
        FileMappings = { new FileMappingRule { LocalPath = "*", DepotPath = ".", Recursive = true } }
    };

    public DepotDefinition Clone() => new()
    {
        DepotId = DepotId,
        Label = Label,
        ContentRootOverride = ContentRootOverride,
        FileMappings = FileMappings.ConvertAll(m => m.Clone()),
        FileExclusions = new List<string>(FileExclusions),
        InstallScript = InstallScript,
        FileProperties = FileProperties.ConvertAll(p => p.Clone()),
        Enabled = Enabled
    };
}

/// <summary>
/// A saved project: everything needed to produce a SteamPipe build, minus secrets.
/// Persisted as JSON under the user's application-data folder.
/// </summary>
public sealed class BuildProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New project";

    public uint AppId { get; set; }

    /// <summary>Internal build description shown in the Steamworks admin panel.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Absolute path to the folder holding the game files to upload.</summary>
    public string ContentRoot { get; set; } = string.Empty;

    /// <summary>Where steamcmd writes logs and its chunk cache. Keep it off the content root.</summary>
    public string BuildOutput { get; set; } = string.Empty;

    /// <summary>Steam account used for uploading. The password is never stored here.</summary>
    public string SteamAccountName { get; set; } = string.Empty;

    /// <summary>Overrides the global ContentBuilder path when set.</summary>
    public string ContentBuilderPathOverride { get; set; } = string.Empty;

    /// <summary>
    /// Beta branch to set live automatically. Empty means "upload only".
    /// Steam refuses to auto-set the default branch live, so <c>public</c> is rejected here.
    /// </summary>
    public string SetLiveBranch { get; set; } = string.Empty;

    /// <summary>Builds and validates everything but uploads nothing.</summary>
    public bool Preview { get; set; }

    public bool Verbose { get; set; }

    /// <summary>Path to a local content server root instead of uploading to Steam.</summary>
    public string LocalContentServerPath { get; set; } = string.Empty;

    public List<DepotDefinition> Depots { get; set; } = new();

    public DateTimeOffset? LastUploadedUtc { get; set; }

    public uint? LastBuildId { get; set; }

    public BuildProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        AppId = AppId,
        Description = Description,
        ContentRoot = ContentRoot,
        BuildOutput = BuildOutput,
        SteamAccountName = SteamAccountName,
        ContentBuilderPathOverride = ContentBuilderPathOverride,
        SetLiveBranch = SetLiveBranch,
        Preview = Preview,
        Verbose = Verbose,
        LocalContentServerPath = LocalContentServerPath,
        Depots = Depots.ConvertAll(d => d.Clone()),
        LastUploadedUtc = LastUploadedUtc,
        LastBuildId = LastBuildId
    };
}
