using System;
using System.Collections.ObjectModel;
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
    private string _status = "Ready.";

    public MainWindowViewModel(ProfileStore store, AppSettings settings, Window owner)
    {
        _store = store;
        _settings = settings;
        _prompt = new UiPrompt(owner);
        _secrets = SecretStoreFactory.Create(store.RootDirectory);

        Profiles = new ObservableCollection<ProfileViewModel>(
            store.LoadProfiles().Select(p => new ProfileViewModel(p, _secrets)));

        Upload = new UploadViewModel(() => SelectedProfile, () => _settings, _prompt, OnUploadSucceeded,
                                     _prompt.CopyToClipboardAsync, _secrets);
        Builds = new BuildsViewModel(() => SelectedProfile, () => _settings, _secrets, _prompt.ConfirmAsync);
        Settings = new SettingsViewModel(_settings, store, _secrets,
            title => _prompt.PickFolderAsync(title, _settings.ContentBuilderPath));

        NewProfileCommand = new RelayCommand(NewProfile);
        DuplicateProfileCommand = new RelayCommand(DuplicateProfile, () => SelectedProfile is not null);
        DeleteProfileCommand = new AsyncRelayCommand(DeleteProfileAsync, () => SelectedProfile is not null);
        SaveProfileCommand = new RelayCommand(SaveProfile, () => SelectedProfile is not null);
        ImportScriptCommand = new AsyncRelayCommand(ImportScriptAsync);
        ExportScriptsCommand = new AsyncRelayCommand(ExportScriptsAsync, () => SelectedProfile is not null);
        ExportWorkflowCommand = new AsyncRelayCommand(ExportWorkflowAsync, () => SelectedProfile is not null);

        BrowseContentRootCommand = new AsyncRelayCommand(async () =>
        {
            var picked = await _prompt.PickFolderAsync("Select the folder to upload",
                                                       SelectedProfile?.ContentRoot);
            if (picked is not null && SelectedProfile is not null) SelectedProfile.ContentRoot = picked;
        });

        BrowseBuildOutputCommand = new AsyncRelayCommand(async () =>
        {
            var picked = await _prompt.PickFolderAsync("Select a folder for logs and the build cache",
                                                       SelectedProfile?.BuildOutput);
            if (picked is not null && SelectedProfile is not null) SelectedProfile.BuildOutput = picked;
        });

        foreach (var command in new[]
                 {
                     DeleteProfileCommand, ImportScriptCommand, ExportScriptsCommand,
                     ExportWorkflowCommand, BrowseContentRootCommand, BrowseBuildOutputCommand
                 })
            command.Faulted += e => Status = e.Message;

        foreach (var command in new[]
                 {
                     NewProfileCommand, DuplicateProfileCommand, SaveProfileCommand
                 })
            command.Faulted += e => Status = e.Message;

        SelectedProfile = Profiles.FirstOrDefault();
    }

    public ObservableCollection<ProfileViewModel> Profiles { get; }

    public UploadViewModel Upload { get; }
    public BuildsViewModel Builds { get; }
    public SettingsViewModel Settings { get; }

    public RelayCommand NewProfileCommand { get; }
    public RelayCommand DuplicateProfileCommand { get; }
    public AsyncRelayCommand DeleteProfileCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public AsyncRelayCommand ImportScriptCommand { get; }
    public AsyncRelayCommand ExportScriptsCommand { get; }
    public AsyncRelayCommand ExportWorkflowCommand { get; }
    public AsyncRelayCommand BrowseContentRootCommand { get; }
    public AsyncRelayCommand BrowseBuildOutputCommand { get; }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public bool HasProfile => SelectedProfile is not null;

    public ProfileViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            // Persist whatever the user typed into the previous project before swapping;
            // switching projects should never be a way to lose edits.
            if (_selectedProfile is { IsDirty: true }) Persist(_selectedProfile);

            if (!SetProperty(ref _selectedProfile, value)) return;

            OnPropertyChanged(nameof(HasProfile));
            DuplicateProfileCommand.RaiseCanExecuteChanged();
            DeleteProfileCommand.RaiseCanExecuteChanged();
            SaveProfileCommand.RaiseCanExecuteChanged();
            ExportScriptsCommand.RaiseCanExecuteChanged();
            ExportWorkflowCommand.RaiseCanExecuteChanged();
        }
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
            "The project's settings are removed from this machine. Nothing on Steam changes " +
            "and no files in your content folder are touched.");

        if (!confirmed) return;

        // Clear the dirty flag first. Removing the item makes the ListBox write back a new
        // selection, and the SelectedProfile setter autosaves whatever was dirty — which
        // would rewrite the JSON file we are about to delete and resurrect the project.
        SelectedProfile.MarkSaved();

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

        // Look the view model up by identity: an upload takes minutes and the user may
        // have switched projects meanwhile. Marking the *selected* one clean would throw
        // away edits made to a different project while this one was uploading.
        //
        // Persist rather than SaveProfile: depot rows only reach the model through
        // Flush(), which last ran when the upload started, so a mapping added during the
        // upload would be marked clean and then dropped.
        var uploaded = Profiles.FirstOrDefault(p => p.Model.Id == profile.Id);
        if (uploaded is not null) Persist(uploaded);
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
