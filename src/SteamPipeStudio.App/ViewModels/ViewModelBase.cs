using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SteamPipeStudio.App.ViewModels;

/// <summary>
/// Hand-rolled MVVM base. CommunityToolkit.Mvvm would do the same job with less typing,
/// but this keeps the app's dependency list to Avalonia alone, which matters for a tool
/// meant to be dropped into a studio's build machine without a package audit.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected void RaiseAll(params string[] propertyNames)
    {
        foreach (var name in propertyNames) OnPropertyChanged(name);
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// Button.OnClick does not catch, so an exception escaping a command handler ends the
    /// process. Saving a profile writes files and storing a key calls DPAPI; both can
    /// throw for ordinary reasons (a locked file, a roamed profile) that must not be
    /// fatal.
    /// </summary>
    public event Action<Exception>? Faulted;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        try
        {
            _execute(parameter);
        }
        catch (Exception e)
        {
            Faulted?.Invoke(e);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Async command that refuses to run twice concurrently. Without the guard, a
/// double-clicked Upload button starts two steamcmd processes against the same
/// build output folder, which corrupts the chunk cache.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// ICommand.Execute is void, so an exception from the awaited task would otherwise
    /// reach the synchronisation context unobserved and take the process down. Anything
    /// that escapes the handler is surfaced here instead.
    /// </summary>
    public event Action<Exception>? Faulted;

    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (_running == value) return;
            _running = value;
            RaiseCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        IsRunning = true;
        try
        {
            await _execute(parameter);
        }
        catch (OperationCanceledException)
        {
            // Cancelling an upload is a normal outcome, not a fault.
        }
        catch (Exception e)
        {
            Faulted?.Invoke(e);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
