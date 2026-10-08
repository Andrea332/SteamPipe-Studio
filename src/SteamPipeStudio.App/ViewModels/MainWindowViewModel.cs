using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using SteamPipeStudio.App.Services;
using SteamPipeStudio.Core.Build;
using SteamPipeStudio.Core.Ci;
using SteamPipeStudio.Core.Model;
using SteamPipeStudio.Core.Security;

namespace SteamPipeStudio.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly ProfileStore _store;
    private readonly AppSettings _settings;
    private readonly UiPrompt _prompt;
    private readonly ISecretStore _secrets;

    private ProfileViewModel? _selectedProfile;
    private bool _isSettingsOpen;
    private string _status = "Ready.";

    /// <param name="secrets">
    /// Where passwords and keys are kept; the platform's own store when omitted. On macOS
    /// and Linux that is the user's keychain or keyring, shared by every copy of the app, so
    /// a tool that draws the window with made-up data passes its own.
    /// </param>
    public MainWindowViewModel(ProfileStore store, AppSettings settings, Window owner, IAppUpdater updater,
                               ISecretStore? secrets = null)
    {
        _store = store;
        _settings = settings;
        _prompt = new UiPrompt(owner);
        _secrets = secrets ?? SecretStoreFactory.Create(store.RootDirectory);

        Profiles = new ObservableCollection<ProfileViewModel>(
            store.LoadProfiles().Select(p => new ProfileViewModel(p, _secrets)));
        Profiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyHint));
        };

        Upload = new UploadViewModel(() => SelectedProfile, () => _settings, _prompt, OnUploadSucceeded,
                                     _prompt.CopyToClipboardAsync, _secrets);
        Builds = new BuildsViewModel(() => SelectedProfile, () => _settings, _secrets, _prompt.ConfirmAsync,
            new DownloadServices(
                PickFolder: (title, startAt) => _prompt.PickFolderAsync(title, startAt ?? SelectedProfile?.BuildOutput),
                AskBranchPassword: _prompt.RequestBranchPasswordAsync,
                Download: Upload.DownloadAsync,
                IsSteamCmdBusy: () => Upload.IsRunning,
                Persist: PersistModel));
        Settings = new SettingsViewModel(_settings, store, _secrets,
            title => _prompt.PickFolderAsync(title, _settings.ContentBuilderPath));

        // Every project without a key of its own falls back on the one in Settings, so its
        // Saved / Not saved line changes with it.
        Settings.ApiKeyChanged += () =>
        {
            foreach (var profile in Profiles) profile.RefreshApiKeyState();
        };
        Updates = new UpdatesViewModel(updater, _settings, store, () => Upload.IsRunning, PersistAll);

        NewProfileCommand = new RelayCommand(NewProfile);
        DuplicateProfileCommand = new RelayCommand(DuplicateProfile, () => SelectedProfile is not null);
        DeleteProfileCommand = new AsyncRelayCommand(DeleteProfileAsync, () => SelectedProfile is not null);
        SaveProfileCommand = new RelayCommand(SaveProfile, () => SelectedProfile is not null);
        ImportScriptCommand = new AsyncRelayCommand(ImportScriptAsync);
        ExportScriptsCommand = new AsyncRelayCommand(ExportScriptsAsync, () => SelectedProfile is not null);
        ExportWorkflowCommand = new AsyncRelayCommand(ExportWorkflowAsync, () => SelectedProfile is not null);
        // Only for a password typed in the field: the saved one is what every upload uses
        // anyway, and checking it would mostly end in "not checked" while Steam remembers
        // the last login.
        CheckPasswordCommand = new AsyncRelayCommand(CheckPasswordAsync,
            () => SelectedProfile is { PasswordInput.Length: > 0 } && !Upload.IsRunning);
        CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);

        BrowseContentRootCommand = new AsyncRelayCommand(async () =>
        {
            var picked = await _prompt.PickFolderAsync("Select the folder to upload",
                                                       SelectedProfile?.ContentRoot);
            if (picked is not null && SelectedProfile is not null) SelectedProfile.ContentRoot = picked;
        });

        BrowseBuildOutputCommand = new AsyncRelayCommand(async () =>
        {
            var picked = await _prompt.PickFolderAsync("Select a folder for upload logs, .vdf scripts and the chunk cache",
                                                       SelectedProfile?.BuildOutput);
            if (picked is not null && SelectedProfile is not null) SelectedProfile.BuildOutput = picked;
        });

        foreach (var command in new[]
                 {
                     DeleteProfileCommand, ImportScriptCommand, ExportScriptsCommand,
                     ExportWorkflowCommand, BrowseContentRootCommand, BrowseBuildOutputCommand,
                     CheckPasswordCommand
                 })
            command.Faulted += e => Status = e.Message;

        foreach (var command in new[]
                 {
                     NewProfileCommand, DuplicateProfileCommand, SaveProfileCommand
                 })
            command.Faulted += e => Status = e.Message;

        // The Download buttons on the Builds tab, the update's Install button and the
        // password Check button are disabled while steamcmd is busy on the Upload tab, and
        // nothing else tells them when that changes.
        Upload.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(UploadViewModel.IsRunning)) return;
            Builds.RefreshCommandStates();
            Updates.RefreshCommandStates();
            CheckPasswordCommand.RaiseCanExecuteChanged();
        };

        SelectedProfile = Profiles.FirstOrDefault();
    }

    public ObservableCollection<ProfileViewModel> Profiles { get; }

    public UploadViewModel Upload { get; }
    public BuildsViewModel Builds { get; }
    public SettingsViewModel Settings { get; }
    public UpdatesViewModel Updates { get; }

    public RelayCommand NewProfileCommand { get; }
    public RelayCommand DuplicateProfileCommand { get; }
    public AsyncRelayCommand DeleteProfileCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public AsyncRelayCommand ImportScriptCommand { get; }
    public AsyncRelayCommand ExportScriptsCommand { get; }
    public AsyncRelayCommand ExportWorkflowCommand { get; }
    public AsyncRelayCommand BrowseContentRootCommand { get; }
    public AsyncRelayCommand BrowseBuildOutputCommand { get; }
    public AsyncRelayCommand CheckPasswordCommand { get; }
    public RelayCommand CloseSettingsCommand { get; }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public bool HasProfile => SelectedProfile is not null;

    /// <summary>
    /// Settings are the app's, not a project's, so they open from the sidebar in place of
    /// the project tabs — also when there is no project — and close when a project is
    /// picked.
    /// </summary>
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (SetProperty(ref _isSettingsOpen, value))
                RaiseAll(nameof(ShowProjectTabs), nameof(ShowEmptyState));
        }
    }

    public bool ShowProjectTabs => HasProfile && !IsSettingsOpen;
    public bool ShowEmptyState => !HasProfile && !IsSettingsOpen;

    // What the content area says instead of the tabs: either there is no project at all,
    // or the one in the list was deselected.
    public string EmptyTitle => Profiles.Count == 0 ? "No projects yet" : "No project selected";

    public string EmptyHint => Profiles.Count == 0
        ? "Create one with New project, top left — or turn a SteamPipe build script you already have into a project with Import app_build.vdf…"
        : "Select a project in the list on the left, or create a new one with New project.";

    public ProfileViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            // Persist whatever the user typed into the previous project before swapping;
            // switching projects should never be a way to lose edits.
            if (_selectedProfile is { IsDirty: true }) Persist(_selectedProfile);

            var previous = _selectedProfile;
            if (!SetProperty(ref _selectedProfile, value)) return;

            if (previous is not null) previous.PropertyChanged -= OnSelectedProfileChanged;
            if (value is not null) value.PropertyChanged += OnSelectedProfileChanged;

            OnPropertyChanged(nameof(HasProfile));
            RaiseAll(nameof(ShowProjectTabs), nameof(ShowEmptyState));
            if (value is not null) IsSettingsOpen = false;
            DuplicateProfileCommand.RaiseCanExecuteChanged();
            DeleteProfileCommand.RaiseCanExecuteChanged();
            SaveProfileCommand.RaiseCanExecuteChanged();
            ExportScriptsCommand.RaiseCanExecuteChanged();
            ExportWorkflowCommand.RaiseCanExecuteChanged();
            CheckPasswordCommand.RaiseCanExecuteChanged();
        }
    }

    // Typing in the password field, or the field being cleared by Save, turns Check on and off.
    private void OnSelectedProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileViewModel.PasswordInput))
            CheckPasswordCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------

    private void NewProfile()
    {
        var profile = new BuildProfile
        {
            Name = "New project",
            SteamAccountName = _settings.LastSteamAccountName
        };

        var viewModel = new ProfileViewModel(profile, _secrets);
        Profiles.Add(viewModel);
        SelectedProfile = viewModel;
        Persist(viewModel);
        Status = "Created a new project.";
    }

    private void DuplicateProfile()
    {
        if (SelectedProfile is null) return;

        var originalName = SelectedProfile.Name;
        var copy = SelectedProfile.Flush().Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = SelectedProfile.Name + " (copy)";
        copy.LastBuildId = null;
        copy.LastUploadedUtc = null;

        // The copy usually uploads to an app of the same Steamworks partner, so it keeps
        // the original's own Web API key; the password is filed by account and needs nothing.
        var originalKey = _secrets.Read(SecretStoreFactory.ProjectApiKey(SelectedProfile.Model.Id));
        if (!string.IsNullOrWhiteSpace(originalKey))
            _secrets.Write(SecretStoreFactory.ProjectApiKey(copy.Id), originalKey);

        var viewModel = new ProfileViewModel(copy, _secrets);
        Profiles.Add(viewModel);
        SelectedProfile = viewModel;
        Persist(viewModel);
        Status = $"Duplicated '{originalName}'.";
    }

    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is null) return;

        var name = SelectedProfile.Name;
        var confirmed = await _prompt.ConfirmAsync(
            $"Delete '{name}'?",
            "The project's settings, and its own Web API key if it has one, are removed from " +
            "this machine. Nothing on Steam changes " +
            "and no files in your app/game build folder are touched.");

        if (!confirmed) return;

        // Clear the dirty flag first. Removing the item makes the ListBox write back a new
        // selection, and the SelectedProfile setter autosaves whatever was dirty — which
        // would rewrite the JSON file we are about to delete and resurrect the project.
        SelectedProfile.MarkSaved();

        // The key first: if the keychain refuses, nothing is gone yet and the error says why.
        _secrets.Delete(SecretStoreFactory.ProjectApiKey(SelectedProfile.Model.Id));
        _store.DeleteProfile(SelectedProfile.Model);
        var index = Profiles.IndexOf(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.Count == 0 ? null : Profiles[Math.Min(index, Profiles.Count - 1)];
        Status = $"Deleted '{name}'.";
    }

    private void SaveProfile()
    {
        if (SelectedProfile is null) return;
        Persist(SelectedProfile);
        Status = $"Saved '{SelectedProfile.Name}'.";
    }

    /// <summary>
    /// Checks the password typed in the Password field by signing in to Steam with it, so a
    /// new password can be tried before it is saved. The verdict shows next to Saved / Not
    /// saved; the sign-in is logged on the Upload tab.
    /// </summary>
    private async Task CheckPasswordAsync()
    {
        var profileVm = SelectedProfile;
        if (profileVm is not { PasswordInput.Length: > 0 }) return;

        var profile = profileVm.Flush();
        profileVm.ShowPasswordCheck(null);
        profileVm.ShowPasswordCheck(await Upload.CheckPasswordAsync(profile, profileVm.PasswordInput));
    }

    private async Task ImportScriptAsync()
    {
        var path = await _prompt.PickFileAsync("Open an existing app_build script", "vdf", "SteamPipe build script");
        if (path is null) return;

        var profile = BuildScriptGenerator.ImportAppScript(path);
        profile.SteamAccountName = _settings.LastSteamAccountName;

        var viewModel = new ProfileViewModel(profile, _secrets);
        Profiles.Add(viewModel);
        SelectedProfile = viewModel;
        Persist(viewModel);

        Status = $"Imported {Path.GetFileName(path)} — {profile.Depots.Count} depot(s).";
    }

    private async Task ExportScriptsAsync()
    {
        if (SelectedProfile is null) return;

        var directory = await _prompt.PickFolderAsync("Where should the .vdf scripts go?",
                                                      SelectedProfile.BuildOutput);
        if (directory is null) return;

        var appScript = BuildScriptGenerator.WriteTo(SelectedProfile.Flush(), directory);
        Status = $"Wrote {Path.GetFileName(appScript)} and its depot scripts to {directory}.";
    }

    private async Task ExportWorkflowAsync()
    {
        if (SelectedProfile is null) return;

        var path = await _prompt.SaveFileAsync("Save the GitHub Actions workflow",
                                               "steam-deploy.yml", "yml");
        if (path is null) return;

        await File.WriteAllTextAsync(path, GitHubActionsExporter.Export(SelectedProfile.Flush()));
        Status = $"Wrote {Path.GetFileName(path)}. Add the two repository secrets it lists at the top.";
    }

    private void OnUploadSucceeded(BuildProfile profile)
    {
        _settings.LastSteamAccountName = profile.SteamAccountName;
        _store.SaveSettings(_settings);
        PersistModel(profile);
    }

    /// <summary>
    /// Saves a profile that was changed through its model rather than its view model —
    /// by an upload stamping the build id, or a download remembering its folder.
    ///
    /// Looked up by identity: an upload takes minutes and the user may have switched
    /// projects meanwhile. Marking the <em>selected</em> one clean would throw away edits
    /// made to a different project while this one was uploading.
    ///
    /// Persist rather than SaveProfile: depot rows only reach the model through Flush(),
    /// which last ran when the upload started, so a mapping added during the upload would
    /// be marked clean and then dropped.
    /// </summary>
    private void PersistModel(BuildProfile profile)
    {
        var owner = Profiles.FirstOrDefault(p => p.Model.Id == profile.Id);
        if (owner is not null) Persist(owner);
        else _store.SaveProfile(profile);
    }

    private void Persist(ProfileViewModel viewModel)
    {
        _store.SaveProfile(viewModel.Flush());
        viewModel.MarkSaved();
    }

    /// <summary>Called when the window is closing so nothing typed is lost.</summary>
    public void PersistAll()
    {
        // Swallows per-profile IO failures on purpose: this runs from Window.Closing, and
        // an exception there takes the process down during shutdown, which looks to the
        // user exactly like a crash.
        foreach (var profile in Profiles.Where(p => p.IsDirty))
        {
            try { Persist(profile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        try { _store.SaveSettings(_settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
