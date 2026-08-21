using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SteamPipeStudio.Core.Model;
using SteamPipeStudio.Core.Security;
using SteamPipeStudio.Core.Steam;

namespace SteamPipeStudio.App.ViewModels;

public sealed class BuildRowViewModel
{
    public BuildRowViewModel(SteamBuild build, IEnumerable<SteamBranch> branches)
    {
        Build = build;
        BuildId = build.BuildId.ToString();
        Description = string.IsNullOrWhiteSpace(build.Description) ? "(no description)" : build.Description;
        Created = build.CreatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

        // Which branches carry this build, from two sources that should agree and may
        // not: GetAppBetas says which build each branch points at, and whether the
        // branch has a password; GetAppBuilds may list branch names on the build itself.
        // The union is what is shown; the branch objects are what a download needs.
        Branches = branches.Where(b => b.BuildId == build.BuildId).ToList();
        LiveBranchNames = Branches.Select(b => b.Name)
            .Concat(build.LiveBranches)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        LiveOn = LiveBranchNames.Count == 0 ? "—" : string.Join(", ", LiveBranchNames);
    }

    public SteamBuild Build { get; }
    public string BuildId { get; }
    public string Description { get; }
    public string Created { get; }
    public string LiveOn { get; }
    public IReadOnlyList<SteamBranch> Branches { get; }
    public IReadOnlyList<string> LiveBranchNames { get; }

    /// <summary>
    /// A build is a set of depot manifests, not a file: steamcmd can only install what a
    /// branch points at, so a build is downloadable exactly while some branch carries it.
    /// </summary>
    public bool CanDownload => LiveBranchNames.Count > 0;

    public string DownloadHint => CanDownload
        ? "Download this build with steamcmd into a folder you choose — the same files a player gets."
        : "Not live on any branch, so there is nothing for steamcmd to install. Set it live on a " +
          "branch — a private one will do — and it becomes downloadable.";

    /// <summary>
    /// The branch to download from. The build is the same on every branch that carries
    /// it, so the only preference is for one without a password, which saves a prompt.
    /// </summary>
    public (string Name, bool PasswordRequired) PickDownloadBranch()
    {
        var open = Branches.FirstOrDefault(b => !b.PasswordRequired);
        if (open is not null) return (open.Name, false);
        if (Branches.Count > 0) return (Branches[0].Name, true);

        // Named only on the build record, so nothing is known about a password: try
        // without one, and let steamcmd say so if it wanted one.
        return (LiveBranchNames[0], false);
    }
}

/// <summary>An entry of the "download for" choice.</summary>
public sealed record PlatformChoice(string Label, DownloadPlatform Platform);

/// <summary>
/// What a download needs from outside this view model: the window picks a folder and
/// asks for a branch password, the Upload tab runs steamcmd and owns the log, and the
/// main view model knows whether steamcmd is busy and how to save the profile.
/// </summary>
public sealed record DownloadServices(
    Func<string, string?, Task<string?>> PickFolder,
    Func<string, Task<string?>> AskBranchPassword,
    Func<BuildProfile, DownloadRequest, Action<RunProgress>?, Task<DownloadOutcome>> Download,
    Func<bool> IsSteamCmdBusy,
    Action<BuildProfile> Persist);

/// <summary>
/// Build history, branch promotion and build download via the partner Web API and
/// steamcmd.
///
/// This is the half the original tool never had: it could push a build but not tell you
/// what was already up there, so "which build is on the beta branch right now" always
/// meant opening the Steamworks site — and getting that build back onto a machine meant
/// a Steam client logged into the right account.
/// </summary>
public sealed class BuildsViewModel : ViewModelBase
{
    private readonly Func<ProfileViewModel?> _currentProfile;
    private readonly Func<AppSettings> _settings;
    private readonly ISecretStore _secrets;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly DownloadServices _downloads;

    private string _status = "Enter a publisher Web API key in Settings to see build history.";
    private BuildRowViewModel? _selectedBuild;
    private SteamBranch? _selectedBranch;
    private string _setLiveDescription = string.Empty;
    private bool _isBusy;
    private PlatformChoice _selectedPlatform;
    private bool _isDownloading;
    private double _downloadProgress;
    private bool _isDownloadIndeterminate;
    private string _downloadPhase = string.Empty;

    public BuildsViewModel(
        Func<ProfileViewModel?> currentProfile,
        Func<AppSettings> settings,
        ISecretStore secrets,
        Func<string, string, Task<bool>> confirm,
        DownloadServices downloads)
    {
        _currentProfile = currentProfile;
        _settings = settings;
        _secrets = secrets;
        _confirm = confirm;
        _downloads = downloads;

        Platforms = new[]
        {
            new PlatformChoice("this machine", DownloadPlatform.Host),
            new PlatformChoice("Windows", DownloadPlatform.Windows),
            new PlatformChoice("macOS", DownloadPlatform.MacOS),
            new PlatformChoice("Linux", DownloadPlatform.Linux)
        };
        _selectedPlatform = Platforms[0];

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        SetLiveCommand = new AsyncRelayCommand(SetLiveAsync, () => !IsBusy && SelectedBuild is not null);

        // One command for every row, told which row by its parameter, so that a button
        // is enabled exactly when its own build can be downloaded.
        DownloadCommand = new AsyncRelayCommand(DownloadAsync,
            parameter => !IsBusy && !_downloads.IsSteamCmdBusy() &&
                         parameter is BuildRowViewModel { CanDownload: true });

        RefreshCommand.Faulted += e => Status = e.Message;
        SetLiveCommand.Faulted += e => Status = e.Message;
        DownloadCommand.Faulted += e => Status = e.Message;
    }

    public ObservableCollection<BuildRowViewModel> Builds { get; } = new();
    public ObservableCollection<SteamBranch> Branches { get; } = new();
    public IReadOnlyList<PlatformChoice> Platforms { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SetLiveCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public string SetLiveDescription
    {
        get => _setLiveDescription;
        set => SetProperty(ref _setLiveDescription, value);
    }

    public BuildRowViewModel? SelectedBuild
    {
        get => _selectedBuild;
        set
        {
            if (!SetProperty(ref _selectedBuild, value)) return;
            SetLiveCommand.RaiseCanExecuteChanged();
        }
    }

    public SteamBranch? SelectedBranch
    {
        get => _selectedBranch;
        set => SetProperty(ref _selectedBranch, value);
    }

    public PlatformChoice SelectedPlatform
    {
        get => _selectedPlatform;
        set => SetProperty(ref _selectedPlatform, value);
    }

    // A progress bar of this tab's own, for the download the user started here. The
    // numbers come from the Upload tab's run, which owns the log; mirroring them keeps
    // the user from having to switch tabs to see whether anything is moving.
    public bool IsDownloading { get => _isDownloading; private set => SetProperty(ref _isDownloading, value); }
    public double DownloadProgress { get => _downloadProgress; private set => SetProperty(ref _downloadProgress, value); }
    public bool IsDownloadIndeterminate { get => _isDownloadIndeterminate; private set => SetProperty(ref _isDownloadIndeterminate, value); }
    public string DownloadPhase { get => _downloadPhase; private set => SetProperty(ref _downloadPhase, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RefreshCommandStates();
        }
    }

    /// <summary>
    /// Re-evaluates every command. Also called from outside when steamcmd starts or stops
    /// on the Upload tab, because the Download buttons depend on it being idle.
    /// </summary>
    public void RefreshCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        SetLiveCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
    }

    private async Task RefreshAsync()
    {
        var profile = _currentProfile()?.Model;
        if (profile is null || profile.AppId == 0)
        {
            Status = "Select a project with an App ID first.";
            return;
        }

        var key = _secrets.Read(SecretStoreFactory.PublisherApiKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            Status = "No publisher Web API key stored. Add one in Settings.";
            return;
        }

        IsBusy = true;
        Status = "Loading build history…";

        try
        {
            using var client = new PartnerApiClient();

            var buildsTask = client.GetAppBuildsAsync(key, profile.AppId, _settings().BuildHistoryCount);
            var branchesTask = client.GetAppBetasAsync(key, profile.AppId);
            await Task.WhenAll(buildsTask, branchesTask).ConfigureAwait(true);

            Branches.Clear();
            foreach (var branch in branchesTask.Result) Branches.Add(branch);

            Builds.Clear();
            foreach (var build in buildsTask.Result) Builds.Add(new BuildRowViewModel(build, branchesTask.Result));

            SelectedBranch ??= Branches.FirstOrDefault(b =>
                string.Equals(b.Name, profile.SetLiveBranch, StringComparison.OrdinalIgnoreCase))
                ?? Branches.FirstOrDefault();

            var downloadable = Builds.Count(b => b.CanDownload);
            Status = Builds.Count == 0
                ? "Steam returned no builds for this App ID."
                : $"{Builds.Count} builds · {Branches.Count} branches · " +
                  $"{downloadable} on a branch and downloadable";
        }
        catch (PartnerApiException e)
        {
            Status = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SetLiveAsync()
    {
        var profile = _currentProfile()?.Model;
        var build = SelectedBuild;
        var branch = SelectedBranch;

        if (profile is null || build is null) return;

        if (branch is null)
        {
            Status = "Pick a branch to promote the build to.";
            return;
        }

        var key = _secrets.Read(SecretStoreFactory.PublisherApiKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            Status = "No publisher Web API key stored.";
            return;
        }

        // Promoting to the default branch is the one irreversible-feeling action in the
        // app: it changes what every customer downloads. It always confirms, regardless
        // of the "confirm before set live" preference.
        var isDefault = branch.Name.Equals("public", StringComparison.OrdinalIgnoreCase);
        if (isDefault || _settings().ConfirmSetLive)
        {
            var confirmed = await _confirm(
                isDefault ? "Publish to everyone?" : $"Set build live on '{branch.Name}'?",
                isDefault
                    ? $"Build {build.BuildId} will become the default branch for AppID {profile.AppId}. " +
                      "Every player will download it."
                    : $"Build {build.BuildId} will go live on the '{branch.Name}' branch of AppID {profile.AppId}.")
                .ConfigureAwait(true);

            if (!confirmed) { Status = "Cancelled."; return; }
        }

        IsBusy = true;
        Status = $"Setting build {build.BuildId} live on {branch.Name}…";

        try
        {
            using var client = new PartnerApiClient();
            await client.SetAppBuildLiveAsync(key, profile.AppId, build.Build.BuildId, branch.Name,
                    string.IsNullOrWhiteSpace(SetLiveDescription) ? null : SetLiveDescription)
                .ConfigureAwait(true);

            var success = $"Build {build.BuildId} is live on {branch.Name}.";
            await RefreshAsync().ConfigureAwait(true);

            // RefreshAsync overwrites Status with its own summary; the outcome the user
            // just asked for is the more useful thing to leave on screen.
            Status = success;
        }
        catch (PartnerApiException e)
        {
            Status = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Downloads the build in the row, by installing the branch that carries it with
    /// steamcmd. The run itself happens on the Upload tab, which owns the log and the
    /// prompts; this side picks the folder, asks for a branch password when the branch
    /// has one, and keeps its own status line up to date.
    /// </summary>
    private async Task DownloadAsync(object? parameter)
    {
        if (parameter is not BuildRowViewModel row) return;

        var profile = _currentProfile()?.Model;
        if (profile is null || profile.AppId == 0)
        {
            Status = "Select a project with an App ID first.";
            return;
        }

        if (!row.CanDownload)
        {
            Status = row.DownloadHint;
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.SteamAccountName))
        {
            Status = "Enter the Steam account on the Project tab first: a download logs in with it, " +
                     "exactly like an upload.";
            return;
        }

        var (branch, passwordRequired) = row.PickDownloadBranch();

        var folder = await _downloads.PickFolder(
                $"Where should build {row.BuildId} be downloaded to?",
                string.IsNullOrWhiteSpace(profile.DownloadDirectory) ? null : profile.DownloadDirectory)
            .ConfigureAwait(true);

        if (folder is null) { Status = "Cancelled."; return; }

        // A folder that already holds a steamcmd install of this app is the normal case —
        // the download becomes an incremental update. Anything else with files in it
        // deserves a question, because steamcmd overwrites whatever shares a name.
        if (HasFiles(folder) && !IsSteamInstallOf(folder, profile.AppId))
        {
            var proceed = await _confirm(
                    "Download into a folder that is not empty?",
                    $"{folder} already has files in it. steamcmd writes the build into it and " +
                    "overwrites any file with the same name; other files are left alone.")
                .ConfigureAwait(true);

            if (!proceed) { Status = "Cancelled."; return; }
        }

        string? branchPassword = null;
        if (passwordRequired)
        {
            branchPassword = await _downloads.AskBranchPassword(branch).ConfigureAwait(true);
            if (branchPassword is null) { Status = "Cancelled."; return; }
        }

        profile.DownloadDirectory = folder;
        _downloads.Persist(profile);

        IsBusy = true;
        IsDownloading = true;
        DownloadProgress = 0;
        IsDownloadIndeterminate = true;
        DownloadPhase = "Starting steamcmd…";
        Status = $"Downloading build {row.BuildId} from '{branch}' into {folder}… " +
                 "The full log is on the Upload tab.";

        try
        {
            var request = new DownloadRequest(branch, branchPassword, folder, SelectedPlatform.Platform);

            var outcome = await _downloads
                .Download(profile, request, progress =>
                {
                    DownloadPhase = progress.Phase;
                    IsDownloadIndeterminate = progress.Percent is null;
                    if (progress.Percent is { } percent) DownloadProgress = percent;
                })
                .ConfigureAwait(true);

            Status = outcome.Succeeded
                ? $"Build {row.BuildId} downloaded to {outcome.InstallDirectory}."
                : outcome.FailureDetail ?? "The download did not finish.";
        }
        finally
        {
            IsDownloading = false;
            IsBusy = false;
        }
    }

    private static bool HasFiles(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as empty, but steamcmd will say so itself, with
            // a better message than a failed directory listing.
            return false;
        }
    }

    /// <summary>
    /// steamcmd leaves <c>steamapps/appmanifest_&lt;appid&gt;.acf</c> in every folder it
    /// installs into; its presence is what makes the next download incremental.
    /// </summary>
    private static bool IsSteamInstallOf(string folder, uint appId)
    {
        try
        {
            return File.Exists(Path.Combine(folder, "steamapps", $"appmanifest_{appId}.acf"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
