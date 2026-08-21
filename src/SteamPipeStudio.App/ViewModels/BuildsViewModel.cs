using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SteamPipeStudio.Core.Model;
using SteamPipeStudio.Core.Security;
using SteamPipeStudio.Core.Steam;

namespace SteamPipeStudio.App.ViewModels;

public sealed class BuildRowViewModel
{
    public BuildRowViewModel(SteamBuild build)
    {
        Build = build;
        BuildId = build.BuildId.ToString();
        Description = string.IsNullOrWhiteSpace(build.Description) ? "(no description)" : build.Description;
        Created = build.CreatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
        LiveOn = build.LiveBranches.Count == 0 ? "—" : string.Join(", ", build.LiveBranches);
    }

    public SteamBuild Build { get; }
    public string BuildId { get; }
    public string Description { get; }
    public string Created { get; }
    public string LiveOn { get; }
}

/// <summary>
/// Build history and branch promotion via the partner Web API.
///
/// This is the half the original tool never had: it could push a build but not tell you
/// what was already up there, so "which build is on the beta branch right now" always
/// meant opening the Steamworks site.
/// </summary>
public sealed class BuildsViewModel : ViewModelBase
{
    private readonly Func<ProfileViewModel?> _currentProfile;
    private readonly Func<AppSettings> _settings;
    private readonly ISecretStore _secrets;
    private readonly Func<string, string, Task<bool>> _confirm;

    private string _status = "Enter a publisher Web API key in Settings to see build history.";
    private BuildRowViewModel? _selectedBuild;
    private SteamBranch? _selectedBranch;
    private string _setLiveDescription = string.Empty;
    private bool _isBusy;

    public BuildsViewModel(
        Func<ProfileViewModel?> currentProfile,
        Func<AppSettings> settings,
        ISecretStore secrets,
        Func<string, string, Task<bool>> confirm)
    {
        _currentProfile = currentProfile;
        _settings = settings;
        _secrets = secrets;
        _confirm = confirm;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        SetLiveCommand = new AsyncRelayCommand(SetLiveAsync, () => !IsBusy && SelectedBuild is not null);

        RefreshCommand.Faulted += e => Status = e.Message;
        SetLiveCommand.Faulted += e => Status = e.Message;
    }

    public ObservableCollection<BuildRowViewModel> Builds { get; } = new();
    public ObservableCollection<SteamBranch> Branches { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SetLiveCommand { get; }

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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
            SetLiveCommand.RaiseCanExecuteChanged();
        }
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

            Builds.Clear();
            foreach (var build in buildsTask.Result) Builds.Add(new BuildRowViewModel(build));

            Branches.Clear();
            foreach (var branch in branchesTask.Result) Branches.Add(branch);

            SelectedBranch ??= Branches.FirstOrDefault(b =>
                string.Equals(b.Name, profile.SetLiveBranch, StringComparison.OrdinalIgnoreCase))
                ?? Branches.FirstOrDefault();

            Status = Builds.Count == 0
                ? "Steam returned no builds for this App ID."
                : $"{Builds.Count} builds · {Branches.Count} branches";
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
}
