using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamPipeStudio.Core.Build;
using SteamPipeStudio.Core.Model;
using SteamPipeStudio.Core.Steam;

namespace SteamPipeStudio.App.ViewModels;

public sealed class LogLineViewModel
{
    public LogLineViewModel(string text, SteamCmdEventKind kind)
    {
        Text = text.TrimEnd();
        Kind = kind;
    }

    public string Text { get; }
    public SteamCmdEventKind Kind { get; }

    public bool IsError => Kind is SteamCmdEventKind.Error or SteamCmdEventKind.BuildFailed
                                or SteamCmdEventKind.LoginFailed;
    public bool IsWarning => Kind is SteamCmdEventKind.SteamGuardPrompt or SteamCmdEventKind.LoginPrompt;
    public bool IsSuccess => Kind is SteamCmdEventKind.BuildSucceeded or SteamCmdEventKind.LoginSucceeded;
    public bool IsMuted => Kind is SteamCmdEventKind.Raw or SteamCmdEventKind.Bootstrap;
}

public sealed class ValidationIssueViewModel
{
    public ValidationIssueViewModel(ValidationIssue issue)
    {
        Severity = issue.Severity.ToString();
        Field = issue.Field;
        Message = issue.Message;
    }

    public string Severity { get; }
    public string Field { get; }
    public string Message { get; }

    // Avalonia styles a control from boolean class bindings (Classes.foo="{Binding Bar}"),
    // so severity is exposed as three flags rather than one string.
    public bool IsError => Severity == nameof(IssueSeverity.Error);
    public bool IsWarning => Severity == nameof(IssueSeverity.Warning);
    public bool IsInfo => Severity == nameof(IssueSeverity.Info);
}

/// <summary>Drives the Upload tab: validate, preview, run, cancel.</summary>
public sealed class UploadViewModel : ViewModelBase
{
    private const int MaxLogLines = 5000;

    private readonly Func<ProfileViewModel?> _currentProfile;
    private readonly Func<AppSettings> _settings;
    private readonly ISteamCmdPrompt _prompt;
    private readonly Action<BuildProfile> _onUploadSucceeded;
    private readonly Func<string, Task<bool>> _copyToClipboard;

    private CancellationTokenSource? _cancellation;
    private string _status = "Idle.";
    private string _phase = string.Empty;
    private double _progress;
    private bool _isIndeterminate;
    private bool _isRunning;
    private string _preflightSummary = string.Empty;
    private string? _lastLogPath;

    public UploadViewModel(
        Func<ProfileViewModel?> currentProfile,
        Func<AppSettings> settings,
        ISteamCmdPrompt prompt,
        Action<BuildProfile> onUploadSucceeded,
        Func<string, Task<bool>> copyToClipboard)
    {
        _currentProfile = currentProfile;
        _settings = settings;
        _prompt = prompt;
        _onUploadSucceeded = onUploadSucceeded;
        _copyToClipboard = copyToClipboard;

        UploadCommand = new AsyncRelayCommand(UploadAsync, () => !IsRunning);
        PreflightCommand = new AsyncRelayCommand(PreflightAsync, () => !IsRunning);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        ClearLogCommand = new RelayCommand(() => Log.Clear());
        OpenLogCommand = new RelayCommand(OpenLatestLog, () => _lastLogPath is not null);
        CopyLogCommand = new AsyncRelayCommand(() => CopyToClipboardAsync(null));

        UploadCommand.Faulted += e => Fail(e.Message);
        PreflightCommand.Faulted += e => Fail(e.Message);
        CopyLogCommand.Faulted += e => Status = e.Message;
    }

    public ObservableCollection<LogLineViewModel> Log { get; } = new();
    public ObservableCollection<ValidationIssueViewModel> Issues { get; } = new();
    public ObservableCollection<DepotPreflightViewModel> PreflightDepots { get; } = new();

    public AsyncRelayCommand UploadCommand { get; }
    public AsyncRelayCommand PreflightCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public AsyncRelayCommand CopyLogCommand { get; }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool IsIndeterminate { get => _isIndeterminate; private set => SetProperty(ref _isIndeterminate, value); }
    public string PreflightSummary { get => _preflightSummary; private set => SetProperty(ref _preflightSummary, value); }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            UploadCommand.RaiseCanExecuteChanged();
            PreflightCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    // ------------------------------------------------------------------

    private async Task PreflightAsync()
    {
        var profileVm = _currentProfile();
        if (profileVm is null) { Status = "Select a project first."; return; }

        var profile = profileVm.Flush();
        RefreshIssues(profile);

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        IsRunning = true;
        IsIndeterminate = true;
        Phase = "Scanning content…";

        try
        {
            // Scanning a large content root takes real time, so it runs off the UI thread
            // and honours the same Cancel button as an upload.
            var result = await Task.Run(() => ContentPreflight.Run(profile, token), token)
                                   .ConfigureAwait(true);

            PreflightDepots.Clear();
            foreach (var depot in result.Depots)
                PreflightDepots.Add(new DepotPreflightViewModel(depot));

            PreflightSummary =
                $"{result.FileCount} files · {ContentPreflight.FormatBytes(result.TotalBytes)} across " +
                $"{result.Depots.Count} depot(s)";

            Status = result.Depots.SelectMany(d => d.Notes).Any()
                ? "Preview finished with warnings — check the depot notes."
                : "Preview finished.";
        }
        catch (OperationCanceledException)
        {
            Status = "Preview cancelled.";
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            IsRunning = false;
            IsIndeterminate = false;
            Phase = string.Empty;
        }
    }

    private async Task UploadAsync()
    {
        var profileVm = _currentProfile();
        if (profileVm is null) { Status = "Select a project first."; return; }

        var profile = profileVm.Flush();
        var settings = _settings();

        RefreshIssues(profile);
        if (Issues.Any(i => i.Severity == nameof(IssueSeverity.Error)))
        {
            Status = "Upload blocked — fix the errors listed above.";
            return;
        }

        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        Progress = 0;
        IsIndeterminate = true;
        Phase = "Starting steamcmd…";
        Status = profile.Preview ? "Running preview build (nothing is uploaded)…" : "Uploading…";
        Append($"--- {DateTime.Now:HH:mm:ss} starting build for AppID {profile.AppId} ---",
               SteamCmdEventKind.Bootstrap);

        var session = new SteamCmdSession(_prompt);
        session.Output += OnSteamCmdEvent;

        try
        {
            var scriptDirectory = Path.Combine(profile.BuildOutput, "scripts");

            var outcome = await session
                .UploadAsync(profile, settings, scriptDirectory, _cancellation.Token)
                .ConfigureAwait(true);

            _lastLogPath = outcome.BuildLogPath;
            OpenLogCommand.RaiseCanExecuteChanged();

            if (outcome.Succeeded)
            {
                profile.LastBuildId = outcome.BuildId;
                profile.LastUploadedUtc = DateTimeOffset.UtcNow;
                _onUploadSucceeded(profile);

                Progress = 100;
                IsIndeterminate = false;
                Status = outcome.BuildId is null
                    ? "Build finished."
                    : $"Build {outcome.BuildId} uploaded successfully.";
                Append(Status, SteamCmdEventKind.BuildSucceeded);
            }
            else
            {
                Fail(outcome.FailureDetail ?? "The build did not finish.");
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
            Append("--- cancelled by user ---", SteamCmdEventKind.Error);
        }
        finally
        {
            session.Output -= OnSteamCmdEvent;
            _cancellation?.Dispose();
            _cancellation = null;
            IsRunning = false;
            IsIndeterminate = false;
            Phase = string.Empty;
        }
    }

    private void Cancel()
    {
        Status = "Cancelling…";
        _cancellation?.Cancel();
    }

    private void RefreshIssues(BuildProfile profile)
    {
        Issues.Clear();
        foreach (var issue in BuildValidator.Validate(profile, _settings()))
            Issues.Add(new ValidationIssueViewModel(issue));
    }

    private void OnSteamCmdEvent(SteamCmdEvent evt)
    {
        // steamcmd's output arrives on a background thread; every collection and
        // property touched here is bound to the UI.
        Dispatcher.UIThread.Post(() =>
        {
            Append(evt.Line, evt.Kind);

            switch (evt.Kind)
            {
                case SteamCmdEventKind.Bootstrap when evt.Percent is not null:
                    Phase = evt.Detail ?? "Updating steamcmd…";
                    break;
                case SteamCmdEventKind.LoginSucceeded:
                    Phase = "Signed in.";
                    break;
                case SteamCmdEventKind.DepotScanning:
                    Phase = evt.DepotId is null ? "Scanning content…" : $"Scanning depot {evt.DepotId}…";
                    IsIndeterminate = evt.Percent is null;
                    break;
                case SteamCmdEventKind.DepotUploading:
                    Phase = evt.DepotId is null ? "Uploading…" : $"Uploading depot {evt.DepotId}…";
                    break;
            }

            if (evt.Percent is { } percent &&
                evt.Kind is SteamCmdEventKind.DepotUploading or SteamCmdEventKind.DepotScanning
                         or SteamCmdEventKind.Progress)
            {
                IsIndeterminate = false;
                Progress = percent;
            }
        });
    }

    private void Append(string text, SteamCmdEventKind kind)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Log.Add(new LogLineViewModel(text, kind));

        // A long upload can emit tens of thousands of progress lines; keeping them all
        // turns the log into the app's largest allocation for no benefit.
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    }

    private void Fail(string message)
    {
        Status = message;
        Append(message, SteamCmdEventKind.Error);
        IsIndeterminate = false;
    }

    private void OpenLatestLog()
    {
        if (_lastLogPath is null || !File.Exists(_lastLogPath)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _lastLogPath,
                UseShellExecute = true
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = $"Could not open {_lastLogPath}.";
        }
    }

    /// <summary>
    /// Copies <paramref name="lines"/>, or the whole log when nothing is selected, as
    /// plain text. The panel is the only place a failure explanation exists — nothing
    /// writes it to disk — so getting it out of the window and into a bug report has to
    /// be one click.
    /// </summary>
    public async Task CopyToClipboardAsync(IEnumerable<LogLineViewModel>? lines)
    {
        List<LogLineViewModel> chosen;

        if (lines is null)
        {
            chosen = Log.ToList();
        }
        else
        {
            // Selection order is the order the user clicked in, which is not the order
            // the lines were logged in; filtering the log itself restores it. Reference
            // identity is the right comparison here — two runs can emit the same text.
            var selected = new HashSet<LogLineViewModel>(lines);
            chosen = Log.Where(selected.Contains).ToList();
        }

        if (chosen.Count == 0) { Status = "There is nothing in the log to copy."; return; }

        var text = string.Join(Environment.NewLine, chosen.Select(l => l.Text));

        Status = await _copyToClipboard(text).ConfigureAwait(true)
            ? $"Copied {chosen.Count} log line{(chosen.Count == 1 ? string.Empty : "s")} to the clipboard."
            : "No clipboard is available on this system.";
    }
}

public sealed class DepotPreflightViewModel
{
    public DepotPreflightViewModel(DepotPreflight depot)
    {
        DepotId = depot.DepotId;
        Summary = $"{depot.FileCount} files · {ContentPreflight.FormatBytes(depot.TotalBytes)}";
        Notes = depot.Notes.ToList();

        // Showing every file in a 40 000-file build helps nobody; the largest ones are
        // what people actually scan for when a build is unexpectedly big.
        LargestFiles = depot.Files
            .OrderByDescending(f => f.Length)
            .Take(15)
            .Select(f => $"{ContentPreflight.FormatBytes(f.Length),10}  {f.DepotPath}")
            .ToList();
    }

    public uint DepotId { get; }
    public string Summary { get; }
    public IReadOnlyList<string> Notes { get; }
    public IReadOnlyList<string> LargestFiles { get; }
    public bool HasNotes => Notes.Count > 0;
}
