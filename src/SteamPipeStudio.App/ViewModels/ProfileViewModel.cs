using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using SteamPipeStudio.Core.Model;
using SteamPipeStudio.Core.Security;
using SteamPipeStudio.Core.Steam;

namespace SteamPipeStudio.App.ViewModels;

/// <summary>A single editable string in a list (file exclusions).</summary>
public sealed class TextItemViewModel : ViewModelBase
{
    private string _value;

    public TextItemViewModel(string value = "") => _value = value;

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}

public sealed class FileMappingViewModel : ViewModelBase
{
    private readonly FileMappingRule _model;

    public FileMappingViewModel(FileMappingRule model) => _model = model;

    public FileMappingRule Model => _model;

    public string LocalPath
    {
        get => _model.LocalPath;
        set => SetModel(_model.LocalPath, value, v => _model.LocalPath = v);
    }

    public string DepotPath
    {
        get => _model.DepotPath;
        set => SetModel(_model.DepotPath, value, v => _model.DepotPath = v);
    }

    public bool Recursive
    {
        get => _model.Recursive;
        set => SetModel(_model.Recursive, value, v => _model.Recursive = v);
    }
}

public sealed class DepotViewModel : ViewModelBase
{
    private readonly DepotDefinition _model;

    public DepotViewModel(DepotDefinition model)
    {
        _model = model;

        Mappings = new ObservableCollection<FileMappingViewModel>(
            model.FileMappings.Select(m => new FileMappingViewModel(m)));
        Exclusions = new ObservableCollection<TextItemViewModel>(
            model.FileExclusions.Select(e => new TextItemViewModel(e)));

        // Every edit inside a depot — its own fields, a mapping's paths, an exclusion's
        // text, a row added or removed — has to reach the profile, or "edit the file
        // mappings, close the window" loses the work: the shell only persists profiles
        // it believes are dirty.
        PropertyChanged += (_, _) => Changed?.Invoke();
        Track(Mappings);
        Track(Exclusions);

        AddMappingCommand = new RelayCommand(() =>
            Mappings.Add(new FileMappingViewModel(new FileMappingRule())));

        RemoveMappingCommand = new RelayCommand(parameter =>
        {
            if (parameter is FileMappingViewModel mapping) Mappings.Remove(mapping);
        });

        AddExclusionCommand = new RelayCommand(() => Exclusions.Add(new TextItemViewModel()));

        RemoveExclusionCommand = new RelayCommand(parameter =>
        {
            if (parameter is TextItemViewModel item) Exclusions.Remove(item);
        });
    }

    /// <summary>Raised for any edit anywhere inside this depot.</summary>
    public event Action? Changed;

    public DepotDefinition Model => _model;

    public ObservableCollection<FileMappingViewModel> Mappings { get; }
    public ObservableCollection<TextItemViewModel> Exclusions { get; }

    /// <summary>
    /// Re-syncs subscriptions against the whole collection on every change rather than
    /// diffing OldItems/NewItems. A Reset — which <see cref="ObservableCollection{T}.Clear"/>
    /// raises — carries neither list, so a diffing implementation silently leaks a
    /// handler on every cleared row. These collections hold a handful of items, so
    /// re-subscribing is free and cannot drift.
    /// </summary>
    private void Track<T>(ObservableCollection<T> collection) where T : ViewModelBase
    {
        var subscribed = new List<ViewModelBase>();

        void Resync()
        {
            foreach (var item in subscribed) item.PropertyChanged -= OnChildChanged;
            subscribed.Clear();

            foreach (var item in collection)
            {
                item.PropertyChanged += OnChildChanged;
                subscribed.Add(item);
            }
        }

        Resync();

        collection.CollectionChanged += (_, _) =>
        {
            Resync();
            Changed?.Invoke();
        };
    }

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e) => Changed?.Invoke();

    public RelayCommand AddMappingCommand { get; }
    public RelayCommand RemoveMappingCommand { get; }
    public RelayCommand AddExclusionCommand { get; }
    public RelayCommand RemoveExclusionCommand { get; }

    public string DepotIdText
    {
        get => _model.DepotId == 0 ? string.Empty : _model.DepotId.ToString();
        set
        {
            // Accept an empty box while typing rather than snapping back to 0.
            var parsed = uint.TryParse(value?.Trim(), out var id) ? id : 0u;
            if (SetModel(_model.DepotId, parsed, v => _model.DepotId = v))
                OnPropertyChanged(nameof(Header));
        }
    }

    public string Label
    {
        get => _model.Label;
        set
        {
            if (SetModel(_model.Label, value, v => _model.Label = v))
                OnPropertyChanged(nameof(Header));
        }
    }

    public string ContentRootOverride
    {
        get => _model.ContentRootOverride;
        set => SetModel(_model.ContentRootOverride, value, v => _model.ContentRootOverride = v);
    }

    public string InstallScript
    {
        get => _model.InstallScript;
        set => SetModel(_model.InstallScript, value, v => _model.InstallScript = v);
    }

    public bool Enabled
    {
        get => _model.Enabled;
        set => SetModel(_model.Enabled, value, v => _model.Enabled = v);
    }

    public string Header => string.IsNullOrWhiteSpace(Label)
        ? $"Depot {DepotIdText}"
        : $"Depot {DepotIdText} — {Label}";

    /// <summary>Pushes the editable collections back into the model before saving.</summary>
    public void Flush()
    {
        _model.FileMappings = Mappings.Select(m => m.Model).ToList();
        _model.FileExclusions = Exclusions
            .Select(e => e.Value.Trim())
            .Where(e => e.Length > 0)
            .ToList();
    }
}

/// <summary>
/// Editable wrapper around a <see cref="BuildProfile"/>.
///
/// Every setter marks the profile dirty so the shell can autosave and warn on exit;
/// losing a depot layout because a window was closed is exactly the kind of small
/// betrayal that stops people trusting a tool.
/// </summary>
public sealed class ProfileViewModel : ViewModelBase
{
    /// <summary>How long the account field has to stay still before the secret store is asked about it.</summary>
    private static readonly TimeSpan PasswordLookupDelay = TimeSpan.FromMilliseconds(400);

    private readonly BuildProfile _model;
    private readonly ISecretStore _secrets;
    private readonly List<DepotViewModel> _subscribedDepots = new();
    private bool _isDirty;
    private string _passwordInput = string.Empty;
    private string _passwordStatus = string.Empty;
    private bool? _hasStoredPassword;
    private IDisposable? _pendingPasswordLookup;
    private string _apiKeyInput = string.Empty;
    private string _apiKeyStatus = string.Empty;
    private bool? _hasOwnApiKey;
    private bool? _hasGlobalApiKey;

    public ProfileViewModel(BuildProfile model, ISecretStore secrets)
    {
        _model = model;
        _secrets = secrets;
        Depots = new ObservableCollection<DepotViewModel>(model.Depots.Select(d => new DepotViewModel(d)));
        ResyncDepotSubscriptions();
        Depots.CollectionChanged += OnDepotsChanged;

        SavePasswordCommand = new RelayCommand(SavePassword);
        ClearPasswordCommand = new RelayCommand(ClearPassword, () => HasStoredPassword);
        SavePasswordCommand.Faulted += e => PasswordStatus = e.Message;
        ClearPasswordCommand.Faulted += e => PasswordStatus = e.Message;

        SaveApiKeyCommand = new RelayCommand(SaveApiKey);
        ClearApiKeyCommand = new RelayCommand(ClearApiKey, () => HasOwnApiKey);
        SaveApiKeyCommand.Faulted += e => ApiKeyStatus = e.Message;
        ClearApiKeyCommand.Faulted += e => ApiKeyStatus = e.Message;

        AddDepotCommand = new RelayCommand(() =>
        {
            // Steam allocates depot IDs just above the App ID, so guessing the next one
            // saves a trip to the admin panel in the common case.
            var suggested = Depots.Count == 0
                ? _model.AppId + 1
                : Depots.Max(d => d.Model.DepotId) + 1;

            Depots.Add(new DepotViewModel(DepotDefinition.Create(suggested)));
        });

        RemoveDepotCommand = new RelayCommand(parameter =>
        {
            if (parameter is DepotViewModel depot) Depots.Remove(depot);
        });
    }

    public BuildProfile Model => _model;

    public ObservableCollection<DepotViewModel> Depots { get; }

    public RelayCommand AddDepotCommand { get; }
    public RelayCommand RemoveDepotCommand { get; }

    public bool IsDirty
    {
        get => _isDirty;
        set => SetProperty(ref _isDirty, value);
    }

    public string Name
    {
        get => _model.Name;
        set => Set(_model.Name, value, v => _model.Name = v);
    }

    public string AppIdText
    {
        get => _model.AppId == 0 ? string.Empty : _model.AppId.ToString();
        set => Set(_model.AppId, uint.TryParse(value?.Trim(), out var id) ? id : 0u, v => _model.AppId = v);
    }

    public string Description
    {
        get => _model.Description;
        set => Set(_model.Description, value, v => _model.Description = v);
    }

    public string ContentRoot
    {
        get => _model.ContentRoot;
        set => Set(_model.ContentRoot, value, v => _model.ContentRoot = v);
    }

    public string BuildOutput
    {
        get => _model.BuildOutput;
        set => Set(_model.BuildOutput, value, v => _model.BuildOutput = v);
    }

    public string SteamAccountName
    {
        get => _model.SteamAccountName;
        set
        {
            if (!Set(_model.SteamAccountName, value, v => _model.SteamAccountName = v)) return;

            // A check, or a complaint about the last Save, was about the previous account.
            PasswordStatus = string.Empty;

            // The password is filed under the account name, so a different account needs
            // a fresh answer to "is one saved?" — otherwise the card claims a password is
            // stored for an account that has none. Asked once the typing stops rather than
            // on every keystroke: on macOS and Linux each question to the secret store
            // starts a process, on the UI thread.
            _pendingPasswordLookup?.Dispose();
            _pendingPasswordLookup = DispatcherTimer.RunOnce(() => SetStoredPassword(null), PasswordLookupDelay);
        }
    }

    // ------------------------------------------------------------------
    // Steam password
    //
    // Kept out of the profile JSON entirely: it goes to the platform secret store —
    // DPAPI, the macOS keychain, or the Linux keyring — and only ever comes back out to
    // be written to steamcmd's stdin. It is never put on a command line, where the
    // process list would hand it to every other program on the machine.
    // ------------------------------------------------------------------

    public RelayCommand SavePasswordCommand { get; }
    public RelayCommand ClearPasswordCommand { get; }

    /// <summary>What the user is typing. Cleared the moment it reaches the store.</summary>
    public string PasswordInput
    {
        get => _passwordInput;
        set => SetProperty(ref _passwordInput, value);
    }

    /// <summary>
    /// The note next to Saved / Not saved: a problem with the last Save or Remove, or the
    /// outcome of Check. Setting it plainly makes it a warning.
    /// </summary>
    public string PasswordStatus
    {
        get => _passwordStatus;
        private set
        {
            _passwordStatusVerdict = null;
            SetProperty(ref _passwordStatus, value);
            RaiseAll(nameof(PasswordStatusIsGood), nameof(PasswordStatusIsBad), nameof(PasswordStatusIsWarning));
        }
    }

    private PasswordCheckVerdict? _passwordStatusVerdict;

    // Green for a password Steam accepted, red for one it rejected or a sign-in that
    // failed, yellow for everything in between.
    public bool PasswordStatusIsGood => _passwordStatusVerdict == PasswordCheckVerdict.Correct;
    public bool PasswordStatusIsBad => _passwordStatusVerdict is PasswordCheckVerdict.Wrong or PasswordCheckVerdict.Failed;
    public bool PasswordStatusIsWarning => !PasswordStatusIsGood && !PasswordStatusIsBad;

    /// <summary>Shows a check in progress (<c>null</c>) or its outcome.</summary>
    public void ShowPasswordCheck(PasswordCheck? check)
    {
        PasswordStatus = check?.Message ?? "Checking with Steam…";
        _passwordStatusVerdict = check?.Verdict;
        RaiseAll(nameof(PasswordStatusIsGood), nameof(PasswordStatusIsBad), nameof(PasswordStatusIsWarning));
    }

    /// <summary>
    /// Whether a password is saved for the account. Remembered rather than recomputed on
    /// every read: a binding and a command read it, and asking the secret store costs a
    /// DPAPI call on Windows and a process launch on macOS and Linux.
    /// </summary>
    public bool HasStoredPassword => _hasStoredPassword ??=
        !string.IsNullOrWhiteSpace(_model.SteamAccountName) &&
        !string.IsNullOrEmpty(_secrets.Read(SecretStoreFactory.SteamPassword(_model.SteamAccountName)));

    // The field never shows a saved password, so both of these say whether there is one:
    // the placeholder of the empty field, and the coloured word under it.
    public string PasswordWatermark => HasStoredPassword
        ? "Type it here to change it"
        : "Type it here to set it, or leave empty to be asked when needed";

    public string PasswordState => HasStoredPassword ? "Saved" : "Not saved";

    /// <summary>Records the answer, or forgets it with <c>null</c> so the next read asks the store.</summary>
    private void SetStoredPassword(bool? known)
    {
        _hasStoredPassword = known;
        RaiseAll(nameof(HasStoredPassword), nameof(PasswordWatermark), nameof(PasswordState));
        ClearPasswordCommand.RaiseCanExecuteChanged();
    }

    private void SavePassword()
    {
        var account = _model.SteamAccountName.Trim();
        if (account.Length == 0) { PasswordStatus = "Fill in the Steam account first."; return; }
        if (PasswordInput.Length == 0) { PasswordStatus = "Type the password to save it."; return; }

        _secrets.Write(SecretStoreFactory.SteamPassword(account), PasswordInput);
        PasswordInput = string.Empty;
        PasswordStatus = string.Empty;
        SetStoredPassword(true);
    }

    private void ClearPassword()
    {
        var account = _model.SteamAccountName.Trim();
        if (account.Length == 0) return;

        _secrets.Delete(SecretStoreFactory.SteamPassword(account));
        PasswordInput = string.Empty;
        PasswordStatus = string.Empty;
        SetStoredPassword(false);
    }

    // ------------------------------------------------------------------
    // The project's own publisher Web API key
    //
    // Optional, and used instead of the global one in Settings: a key belongs to one
    // Steamworks partner, so an app published by another partner needs its own. Kept in
    // the secret store like the password, never in the project file.
    // ------------------------------------------------------------------

    public RelayCommand SaveApiKeyCommand { get; }
    public RelayCommand ClearApiKeyCommand { get; }

    /// <summary>What the user is pasting. Cleared the moment it reaches the store.</summary>
    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set => SetProperty(ref _apiKeyInput, value);
    }

    /// <summary>A problem with the last Save or Remove of the key.</summary>
    public string ApiKeyStatus
    {
        get => _apiKeyStatus;
        private set => SetProperty(ref _apiKeyStatus, value);
    }

    /// <summary>Remembered for the same reason as <see cref="HasStoredPassword"/>.</summary>
    public bool HasOwnApiKey => _hasOwnApiKey ??=
        !string.IsNullOrWhiteSpace(_secrets.Read(SecretStoreFactory.ProjectApiKey(_model.Id)));

    private bool HasGlobalApiKey => _hasGlobalApiKey ??=
        !string.IsNullOrWhiteSpace(_secrets.Read(SecretStoreFactory.PublisherApiKey));

    public string ApiKeyWatermark => HasOwnApiKey
        ? "Paste a key here to replace this project's key"
        : "Optional — paste a key for this project only";

    // Green when the project has its own key, plain when it borrows the one in Settings,
    // red when the Builds tab has no key at all to work with.
    public string ApiKeyState => HasOwnApiKey ? "Saved for this project"
        : HasGlobalApiKey ? "Not saved: uses the key in Settings"
        : "Not saved, and none in Settings either: the Builds tab needs one";

    public bool ApiKeyStateIsGood => HasOwnApiKey;
    public bool ApiKeyStateIsBad => !HasOwnApiKey && !HasGlobalApiKey;

    /// <summary>Forgets what is known about both keys, after the one in Settings changed.</summary>
    public void RefreshApiKeyState()
    {
        _hasOwnApiKey = null;
        _hasGlobalApiKey = null;
        RaiseAll(nameof(HasOwnApiKey), nameof(ApiKeyWatermark), nameof(ApiKeyState),
                 nameof(ApiKeyStateIsGood), nameof(ApiKeyStateIsBad));
        ClearApiKeyCommand.RaiseCanExecuteChanged();
    }

    private void SaveApiKey()
    {
        var key = ApiKeyInput.Trim();
        if (key.Length == 0) { ApiKeyStatus = "Paste the key to save it."; return; }

        _secrets.Write(SecretStoreFactory.ProjectApiKey(_model.Id), key);
        ApiKeyInput = string.Empty;
        ApiKeyStatus = string.Empty;
        RefreshApiKeyState();
    }

    private void ClearApiKey()
    {
        _secrets.Delete(SecretStoreFactory.ProjectApiKey(_model.Id));
        ApiKeyInput = string.Empty;
        ApiKeyStatus = string.Empty;
        RefreshApiKeyState();
    }

    public string SetLiveBranch
    {
        get => _model.SetLiveBranch;
        set => Set(_model.SetLiveBranch, value, v => _model.SetLiveBranch = v);
    }

    public string LocalContentServerPath
    {
        get => _model.LocalContentServerPath;
        set => Set(_model.LocalContentServerPath, value, v => _model.LocalContentServerPath = v);
    }

    public string ContentBuilderPathOverride
    {
        get => _model.ContentBuilderPathOverride;
        set => Set(_model.ContentBuilderPathOverride, value, v => _model.ContentBuilderPathOverride = v);
    }

    public bool Preview
    {
        get => _model.Preview;
        set => Set(_model.Preview, value, v => _model.Preview = v);
    }

    public bool Verbose
    {
        get => _model.Verbose;
        set => Set(_model.Verbose, value, v => _model.Verbose = v);
    }

    public string LastBuildSummary => _model.LastBuildId is null
        ? "No build uploaded from this machine yet."
        : $"Last build {_model.LastBuildId} on {_model.LastUploadedUtc?.ToLocalTime():yyyy-MM-dd HH:mm}";

    /// <summary><see cref="ViewModelBase.SetModel{T}"/> that also marks the profile dirty.</summary>
    private bool Set<T>(T current, T value, Action<T> assign, [CallerMemberName] string? propertyName = null)
    {
        if (!SetModel(current, value, assign, propertyName)) return false;
        IsDirty = true;
        return true;
    }

    // A cleared text box can hand back null; the model never holds one.
    private bool Set(string current, string? value, Action<string> assign,
                     [CallerMemberName] string? propertyName = null) =>
        Set<string>(current, value ?? string.Empty, assign, propertyName);

    private void OnDepotsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ResyncDepotSubscriptions();
        IsDirty = true;
        OnPropertyChanged(nameof(Depots));
    }

    /// <summary>Same reasoning as <c>DepotViewModel.Track</c>: a Reset carries no item lists.</summary>
    private void ResyncDepotSubscriptions()
    {
        foreach (var depot in _subscribedDepots) depot.Changed -= MarkDirty;
        _subscribedDepots.Clear();

        foreach (var depot in Depots)
        {
            depot.Changed += MarkDirty;
            _subscribedDepots.Add(depot);
        }
    }

    private void MarkDirty() => IsDirty = true;

    /// <summary>Copies UI state back into the model. Call before persisting or building.</summary>
    public BuildProfile Flush()
    {
        foreach (var depot in Depots) depot.Flush();
        _model.Depots = Depots.Select(d => d.Model).ToList();
        return _model;
    }

    public void MarkSaved()
    {
        IsDirty = false;
        OnPropertyChanged(nameof(LastBuildSummary));
    }
}
