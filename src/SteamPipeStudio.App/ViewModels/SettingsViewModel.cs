using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using SteamPipeStudio.Core.Model;
using SteamPipeStudio.Core.Security;
using SteamPipeStudio.Core.Steam;

namespace SteamPipeStudio.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly ProfileStore _store;
    private readonly ISecretStore _secrets;
    private readonly Func<string, Task<string?>> _pickFolder;

    private string _apiKeyInput = string.Empty;
    private string _status = string.Empty;
    private string _steamCmdStatus = string.Empty;

    public SettingsViewModel(AppSettings settings, ProfileStore store, ISecretStore secrets,
                             Func<string, Task<string?>> pickFolder)
    {
        _settings = settings;
        _store = store;
        _secrets = secrets;
        _pickFolder = pickFolder;

        HasStoredApiKey = !string.IsNullOrEmpty(secrets.Read(SecretStoreFactory.PublisherApiKey));

        BrowseContentBuilderCommand = new AsyncRelayCommand(async () =>
        {
            var picked = await _pickFolder("Select the SDK's tools/ContentBuilder folder");
            if (picked is not null) ContentBuilderPath = picked;
        });

        SaveApiKeyCommand = new RelayCommand(SaveApiKey);
        ClearApiKeyCommand = new RelayCommand(ClearApiKey);

        // Without these, a DPAPI or IO failure in Write/Delete leaves the user staring at
        // a Save button that did nothing and no message explaining why.
        SaveApiKeyCommand.Faulted += e => Status = e.Message;
        ClearApiKeyCommand.Faulted += e => Status = e.Message;
        BrowseContentBuilderCommand.Faulted += e => Status = e.Message;

        ValidateContentBuilder();
    }

    public AsyncRelayCommand BrowseContentBuilderCommand { get; }
    public RelayCommand SaveApiKeyCommand { get; }
    public RelayCommand ClearApiKeyCommand { get; }

    public string ContentBuilderPath
    {
        get => _settings.ContentBuilderPath;
        set
        {
            if (_settings.ContentBuilderPath == value) return;
            _settings.ContentBuilderPath = value ?? string.Empty;
            _store.SaveSettings(_settings);
            OnPropertyChanged();
            ValidateContentBuilder();
        }
    }

    public bool DarkTheme
    {
        get => _settings.DarkTheme;
        set
        {
            if (_settings.DarkTheme == value) return;
            _settings.DarkTheme = value;
            _store.SaveSettings(_settings);
            OnPropertyChanged();

            if (Application.Current is { } app)
                app.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    public bool ConfirmSetLive
    {
        get => _settings.ConfirmSetLive;
        set
        {
            if (_settings.ConfirmSetLive == value) return;
            _settings.ConfirmSetLive = value;
            _store.SaveSettings(_settings);
            OnPropertyChanged();
        }
    }

    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set => SetProperty(ref _apiKeyInput, value);
    }

    public bool HasStoredApiKey { get; private set; }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public string SteamCmdStatus { get => _steamCmdStatus; private set => SetProperty(ref _steamCmdStatus, value); }

    /// <summary>Explains where secrets live, in the platform's own terms.</summary>
    public string SecretStorageDescription => OperatingSystem.IsWindows()
        ? "Stored with Windows DPAPI, readable only by your Windows account on this machine."
        : OperatingSystem.IsMacOS()
            ? "Stored in your macOS login keychain."
            : "Stored in your keyring when one is available; otherwise in an encrypted file " +
              "readable only by your user account.";

    public string PasswordPolicyDescription =>
        "Your Steam password is never stored and never appears on a command line. steamcmd is " +
        "started as '+login <account>', so it reuses the session token it caches itself, and " +
        "asks only when that token has expired.";

    private void SaveApiKey()
    {
        var key = ApiKeyInput.Trim();
        if (key.Length == 0) { Status = "Enter a key first."; return; }

        _secrets.Write(SecretStoreFactory.PublisherApiKey, key);
        ApiKeyInput = string.Empty;
        HasStoredApiKey = true;
        OnPropertyChanged(nameof(HasStoredApiKey));
        Status = "Publisher key saved.";
    }

    private void ClearApiKey()
    {
        _secrets.Delete(SecretStoreFactory.PublisherApiKey);
        HasStoredApiKey = false;
        OnPropertyChanged(nameof(HasStoredApiKey));
        Status = "Publisher key removed.";
    }

    private void ValidateContentBuilder()
    {
        SteamCmdStatus = SteamCmdLocator.TryLocate(ContentBuilderPath, out var path, out var error)
            ? $"Found steamcmd: {path}"
            : error;
    }
}
