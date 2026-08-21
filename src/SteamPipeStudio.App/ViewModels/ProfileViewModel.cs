using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using SteamPipeStudio.Core.Model;

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
        set { if (_model.LocalPath != value) { _model.LocalPath = value; OnPropertyChanged(); } }
    }

    public string DepotPath
    {
        get => _model.DepotPath;
        set { if (_model.DepotPath != value) { _model.DepotPath = value; OnPropertyChanged(); } }
    }

    public bool Recursive
    {
        get => _model.Recursive;
        set { if (_model.Recursive != value) { _model.Recursive = value; OnPropertyChanged(); } }
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
            if (_model.DepotId == parsed) return;
            _model.DepotId = parsed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Header));
        }
    }

    public string Label
    {
        get => _model.Label;
        set
        {
            if (_model.Label == value) return;
            _model.Label = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Header));
        }
    }

    public string ContentRootOverride
    {
        get => _model.ContentRootOverride;
        set { if (_model.ContentRootOverride != value) { _model.ContentRootOverride = value; OnPropertyChanged(); } }
    }

    public string InstallScript
    {
        get => _model.InstallScript;
        set { if (_model.InstallScript != value) { _model.InstallScript = value; OnPropertyChanged(); } }
    }

    public bool Enabled
    {
        get => _model.Enabled;
        set { if (_model.Enabled != value) { _model.Enabled = value; OnPropertyChanged(); } }
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
    private readonly BuildProfile _model;
    private readonly List<DepotViewModel> _subscribedDepots = new();
    private bool _isDirty;

    public ProfileViewModel(BuildProfile model)
    {
        _model = model;
        Depots = new ObservableCollection<DepotViewModel>(model.Depots.Select(d => new DepotViewModel(d)));
        ResyncDepotSubscriptions();
        Depots.CollectionChanged += OnDepotsChanged;

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
        set => Set(v => _model.Name = v, _model.Name, value);
    }

    public string AppIdText
    {
        get => _model.AppId == 0 ? string.Empty : _model.AppId.ToString();
        set
        {
            var parsed = uint.TryParse(value?.Trim(), out var id) ? id : 0u;
            if (_model.AppId == parsed) return;
            _model.AppId = parsed;
            IsDirty = true;
            OnPropertyChanged();
        }
    }

    public string Description
    {
        get => _model.Description;
        set => Set(v => _model.Description = v, _model.Description, value);
    }

    public string ContentRoot
    {
        get => _model.ContentRoot;
        set => Set(v => _model.ContentRoot = v, _model.ContentRoot, value);
    }

    public string BuildOutput
    {
        get => _model.BuildOutput;
        set => Set(v => _model.BuildOutput = v, _model.BuildOutput, value);
    }

    public string SteamAccountName
    {
        get => _model.SteamAccountName;
        set => Set(v => _model.SteamAccountName = v, _model.SteamAccountName, value);
    }

    public string SetLiveBranch
    {
        get => _model.SetLiveBranch;
        set => Set(v => _model.SetLiveBranch = v, _model.SetLiveBranch, value);
    }

    public string LocalContentServerPath
    {
        get => _model.LocalContentServerPath;
        set => Set(v => _model.LocalContentServerPath = v, _model.LocalContentServerPath, value);
    }

    public string ContentBuilderPathOverride
    {
        get => _model.ContentBuilderPathOverride;
        set => Set(v => _model.ContentBuilderPathOverride = v, _model.ContentBuilderPathOverride, value);
    }

    public bool Preview
    {
        get => _model.Preview;
        set
        {
            if (_model.Preview == value) return;
            _model.Preview = value;
            IsDirty = true;
            OnPropertyChanged();
        }
    }

    public bool Verbose
    {
        get => _model.Verbose;
        set
        {
            if (_model.Verbose == value) return;
            _model.Verbose = value;
            IsDirty = true;
            OnPropertyChanged();
        }
    }

    public string LastBuildSummary => _model.LastBuildId is null
        ? "No build uploaded from this machine yet."
        : $"Last build {_model.LastBuildId} on {_model.LastUploadedUtc?.ToLocalTime():yyyy-MM-dd HH:mm}";

    private void Set(Action<string> assign, string current, string? value,
                     [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        var next = value ?? string.Empty;
        if (current == next) return;
        assign(next);
        IsDirty = true;
        OnPropertyChanged(propertyName);
    }

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
