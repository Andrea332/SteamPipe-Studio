using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SteamPipeStudio.App.ViewModels;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace SteamPipeStudio.App.Services;

/// <summary>
/// Updates from this repository's GitHub releases, through Velopack — the same library
/// that builds the installer and the portable archive, so all three agree on where the
/// app lives and what a version is.
/// </summary>
public sealed class VelopackUpdater : IAppUpdater
{
    public const string RepositoryUrl = "https://github.com/Andrea332/SteamPipe-Studio";

    private readonly UpdateManager _manager;
    private UpdateInfo? _pending;

    /// <summary>
    /// Points the updater at a folder of packages produced by <c>vpk pack</c> instead of
    /// GitHub, so a release can be tried end to end — check, download, restart into the
    /// new version — before anyone presses Publish.
    /// </summary>
    public const string LocalFeedVariable = "STEAMPIPESTUDIO_UPDATE_FEED";

    public VelopackUpdater()
    {
        // Stable releases only. The release workflow opens a draft and a person presses
        // Publish after reading the notes; that click, not the tag, is when users should
        // be offered the version. Drafts are invisible to an anonymous client anyway.
        IUpdateSource source = Environment.GetEnvironmentVariable(LocalFeedVariable) is { Length: > 0 } feed
            ? new SimpleFileSource(new DirectoryInfo(feed))
            : new GithubSource(RepositoryUrl, accessToken: null, prerelease: false);

        _manager = new UpdateManager(source);
    }

    public bool IsUpdatable => _manager.IsInstalled;

    public string CurrentVersion => _manager.IsInstalled && _manager.CurrentVersion is { } version
        ? version.ToString()
        : AssemblyVersion();

    /// <summary>
    /// Where a portable copy keeps its projects and settings: in a <c>data</c> folder
    /// inside its own, so the folder carries them wherever it goes. <c>null</c> for an
    /// installed copy. Velopack only has a portable layout on Windows; on macOS and Linux
    /// settings belong in the home folder, and this is always <c>null</c> there.
    /// </summary>
    public string? PortableDataDirectory =>
        _manager.IsInstalled && _manager.IsPortable &&
        VelopackLocator.IsCurrentSet && VelopackLocator.Current.RootAppDir is { Length: > 0 } root
            ? Path.Combine(root, "data")
            : null;

    public async Task<string?> CheckAsync(CancellationToken cancellation)
    {
        // CheckForUpdatesAsync takes no token; giving up on it is the best that can be done.
        _pending = await _manager.CheckForUpdatesAsync().WaitAsync(cancellation).ConfigureAwait(false);
        return _pending?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadAsync(Action<int> progress, CancellationToken cancellation) =>
        _pending is null
            ? throw new InvalidOperationException("Check for updates before downloading one.")
            : _manager.DownloadUpdatesAsync(_pending, progress, cancellation);

    public void ApplyAndRestart() =>
        _manager.ApplyUpdatesAndRestart(_pending?.TargetFullRelease);

    /// <summary>
    /// The version a development build reports: the one stamped by <c>-p:Version</c>,
    /// without the <c>+commit</c> suffix the SDK appends to the informational version.
    /// </summary>
    private static string AssemblyVersion()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return informational is { Length: > 0 }
            ? informational.Split('+')[0] + " (development build)"
            : "development build";
    }
}
