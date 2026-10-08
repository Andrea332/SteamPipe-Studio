using System;
using System.Runtime.CompilerServices;
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
    private bool? _hasApiKey;

    public SettingsViewModel(AppSettings settings, ProfileStore store, ISecretStore secrets,
                             Func<string, Task<string?>> pickFolder)
    {
        _settings = settings;
        _store = store;
        _secrets = secrets;
        _pickFolder = pickFolder;

        BrowseContentBuilderCommand = new AsyncRelayCommand(async () =>
        {
            var picked = await _pickFolder("Select the SDK's tools/ContentBuilder folder");
            if (picked is not null) ContentBuilderPath = picked;
        });

        SaveApiKeyCommand = new RelayCommand(SaveApiKey);
        ClearApiKeyCommand = new RelayCommand(ClearApiKey, () => HasApiKey);

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
            if (SetSetting(_settings.ContentBuilderPath, value ?? string.Empty, v => _settings.ContentBuilderPath = v))
                ValidateContentBuilder();
        }
    }

    public bool DarkTheme
    {
        get => _settings.DarkTheme;
        set
        {
            if (SetSetting(_settings.DarkTheme, value, v => _settings.DarkTheme = v) &&
                Application.Current is { } app)
                app.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    public bool ConfirmSetLive
    {
        get => _settings.ConfirmSetLive;
        set => SetSetting(_settings.ConfirmSetLive, value, v => _settings.ConfirmSetLive = v);
    }

    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set => SetProperty(ref _apiKeyInput, value);
    }

    /// <summary>Raised when the key is saved or removed: projects without their own key use it.</summary>
    public event Action? ApiKeyChanged;

    /// <summary>Remembered: asking the secret store costs a DPAPI call or a process launch.</summary>
    public bool HasApiKey => _hasApiKey ??=
        !string.IsNullOrWhiteSpace(_secrets.Read(SecretStoreFactory.PublisherApiKey));

    public string ApiKeyState => HasApiKey ? "Saved" : "Not saved";

    public string ApiKeyWatermark => HasApiKey
        ? "Paste a key here to replace the saved one"
        : "Paste the key here to save it";

    /// <summary>A problem with the last Save or Remove of the key.</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public string SteamCmdStatus { get => _steamCmdStatus; private set => SetProperty(ref _steamCmdStatus, value); }

    /// <summary>
    /// The tooltip of the API key field: what the key is for, where to get one, and where it
    /// is kept — in the platform's own terms, since that differs on every system.
    /// </summary>
    public string ApiKeyHelp =>
        "Needed only for the Builds tab: reading build history, promoting a build to a branch " +
        "and downloading builds. Create one in Steamworks under Users & Permissions → Manage " +
        "Groups. It serves every project that has no key of its own: a key belongs to one " +
        "Steamworks partner, so a project whose app another partner publishes gets its own " +
        "on the Project tab. " +
        (OperatingSystem.IsWindows()
            ? "Stored with Windows DPAPI, readable only by your Windows account on this machine."
            : OperatingSystem.IsMacOS()
                ? "Stored in your macOS login keychain."
                : "Stored in your keyring when one is available; otherwise in an encrypted file " +
                  "readable only by your user account.");

    private void SaveApiKey()
    {
        var key = ApiKeyInput.Trim();
        if (key.Length == 0) { Status = "Enter a key first."; return; }

        _secrets.Write(SecretStoreFactory.PublisherApiKey, key);
        ApiKeyInput = string.Empty;
        Status = string.Empty;
        SetHasApiKey(true);
    }

    private void ClearApiKey()
    {
        _secrets.Delete(SecretStoreFactory.PublisherApiKey);
        ApiKeyInput = string.Empty;
        Status = string.Empty;
        SetHasApiKey(false);
    }

    private void SetHasApiKey(bool saved)
    {
        _hasApiKey = saved;
        RaiseAll(nameof(HasApiKey), nameof(ApiKeyState), nameof(ApiKeyWatermark));
        ClearApiKeyCommand.RaiseCanExecuteChanged();
        ApiKeyChanged?.Invoke();
    }

    /// <summary>Settings are saved the moment they change: there is no Save button to forget.</summary>
    private bool SetSetting<T>(T current, T value, Action<T> assign, [CallerMemberName] string? propertyName = null)
    {
        if (!SetModel(current, value, assign, propertyName)) return false;
        _store.SaveSettings(_settings);
        return true;
    }

    private void ValidateContentBuilder()
    {
        SteamCmdStatus = SteamCmdLocator.TryLocate(ContentBuilderPath, out var path, out var error)
            ? $"Found steamcmd: {path}"
            : error;
    }
}
