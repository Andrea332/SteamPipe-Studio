using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SteamPipeStudio.Core.Model;

namespace SteamPipeStudio.App.ViewModels;

/// <summary>
/// What the app needs from an update mechanism. Kept this small so the view model does
/// not care which library does the work, and so a build that cannot update itself —
/// <c>dotnet run</c>, an IDE, a plain copied folder — is just one that says so.
/// </summary>
public interface IAppUpdater
{
    /// <summary>
    /// False when this copy was not installed by the installer or unpacked from the
    /// portable archive, so there is nothing that could be replaced in place.
    /// </summary>
    bool IsUpdatable { get; }

    /// <summary>The running version, as the user should read it.</summary>
    string CurrentVersion { get; }

    /// <summary>The newer version available, or <c>null</c> when this one is current.</summary>
    Task<string?> CheckAsync(CancellationToken cancellation);

    /// <summary>Downloads the version the last check found; progress is 0–100.</summary>
    Task DownloadAsync(Action<int> progress, CancellationToken cancellation);

    /// <summary>Exits, swaps in the downloaded version and starts it. Does not return.</summary>
    void ApplyAndRestart();
}

/// <summary>
/// Drives the Updates card in Settings and the notice in the status bar.
///
/// The app only ever offers an update. Installing one restarts the process, which is
/// never acceptable in the middle of an upload — steamcmd would be killed with a depot
/// half committed — so the install button is off while steamcmd is running, and
/// everything typed is saved before the restart.
/// </summary>
public sealed class UpdatesViewModel : ViewModelBase
{
    private const string ReleasesUrl = "https://github.com/Andrea332/SteamPipe-Studio/releases";

    private readonly IAppUpdater _updater;
    private readonly AppSettings _settings;
    private readonly ProfileStore _store;
    private readonly Func<bool> _isSteamCmdBusy;
    private readonly Action _beforeRestart;

    private string _status;
    private string? _availableVersion;
    private bool _isBusy;
    private bool _isDownloading;
    private double _downloadProgress;

    public UpdatesViewModel(IAppUpdater updater, AppSettings settings, ProfileStore store,
                            Func<bool> isSteamCmdBusy, Action beforeRestart)
    {
        _updater = updater;
        _settings = settings;
        _store = store;
        _isSteamCmdBusy = isSteamCmdBusy;
        _beforeRestart = beforeRestart;

        _status = updater.IsUpdatable
            ? string.Empty
            : "This copy was not installed with the installer or the portable archive, so it " +
              "cannot update itself. Download new versions from the releases page.";

        CheckCommand = new AsyncRelayCommand(() => CheckAsync(quiet: false),
                                             () => _updater.IsUpdatable && !IsBusy);
        InstallCommand = new AsyncRelayCommand(InstallAsync,
                                               () => IsUpdateAvailable && !IsBusy && !_isSteamCmdBusy());
        OpenReleasesCommand = new RelayCommand(OpenReleases);

        CheckCommand.Faulted += e => Status = $"Could not check for updates: {e.Message}";
        InstallCommand.Faulted += e =>
        {
            IsDownloading = false;
            Status = $"The update could not be installed: {e.Message}";
        };
        OpenReleasesCommand.Faulted += e => Status = e.Message;
    }

    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public RelayCommand OpenReleasesCommand { get; }

    public string CurrentVersion => $"Version {_updater.CurrentVersion}";

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public string? AvailableVersion
    {
        get => _availableVersion;
        private set
        {
            if (!SetProperty(ref _availableVersion, value)) return;
            RaiseAll(nameof(IsUpdateAvailable), nameof(InstallLabel));
            RefreshCommandStates();
        }
    }

    public bool IsUpdateAvailable => AvailableVersion is not null;

    public string InstallLabel => $"Install {AvailableVersion} and restart";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RefreshCommandStates();
        }
    }

    public bool IsDownloading { get => _isDownloading; private set => SetProperty(ref _isDownloading, value); }

    public double DownloadProgress { get => _downloadProgress; private set => SetProperty(ref _downloadProgress, value); }

    public bool CheckAtStartup
    {
        get => _settings.CheckForUpdatesAtStartup;
        set
        {
            if (SetModel(_settings.CheckForUpdatesAtStartup, value, v => _settings.CheckForUpdatesAtStartup = v))
                _store.SaveSettings(_settings);
        }
    }

    /// <summary>Called when steamcmd starts or stops: the install button depends on it.</summary>
    public void RefreshCommandStates()
    {
        CheckCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// The check at startup. Quiet on purpose: no network, a GitHub outage or a rate
    /// limit are not worth a message to someone who opened the app to upload a build.
    /// </summary>
    public async Task CheckAtStartupAsync()
    {
        if (!_settings.CheckForUpdatesAtStartup || !_updater.IsUpdatable) return;

        try { await CheckAsync(quiet: true).ConfigureAwait(true); }
        catch (Exception) { Status = string.Empty; }
    }

    private async Task CheckAsync(bool quiet)
    {
        IsBusy = true;
        if (!quiet) Status = "Checking for updates…";

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            AvailableVersion = await _updater.CheckAsync(timeout.Token).ConfigureAwait(true);

            Status = AvailableVersion is not null
                ? $"Version {AvailableVersion} is available."
                : quiet ? string.Empty : $"You have the latest version ({_updater.CurrentVersion}).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallAsync()
    {
        if (_isSteamCmdBusy())
        {
            Status = "Wait for steamcmd to finish: installing the update restarts the app.";
            return;
        }

        IsBusy = true;
        IsDownloading = true;
        DownloadProgress = 0;
        Status = $"Downloading version {AvailableVersion}…";

        try
        {
            await _updater.DownloadAsync(
                    percent => Avalonia.Threading.Dispatcher.UIThread.Post(() => DownloadProgress = percent),
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        // Checked again: an upload may have been started while the download ran.
        if (_isSteamCmdBusy())
        {
            IsDownloading = false;
            Status = $"Version {AvailableVersion} is downloaded. Install it once steamcmd has finished.";
            return;
        }

        Status = "Restarting…";

        // The restart ends the process without going through the window's Closing
        // handler, so the autosave that normally runs there has to run now.
        _beforeRestart();
        _updater.ApplyAndRestart();
    }

    private void OpenReleases() =>
        Process.Start(new ProcessStartInfo { FileName = ReleasesUrl, UseShellExecute = true });
}
