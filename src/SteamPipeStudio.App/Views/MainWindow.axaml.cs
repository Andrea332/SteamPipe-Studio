using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using SteamPipeStudio.App.ViewModels;

namespace SteamPipeStudio.App.Views;

public partial class MainWindow : Window
{
    private INotifyCollectionChanged? _observedLog;
    private bool _scrollQueued;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => AttachLogAutoScroll();
        Closing += (_, _) =>
        {
            // PersistAll swallows its own IO failures; this guard is for anything else,
            // because an exception from a Closing handler ends the process and reads to
            // the user as a crash on exit.
            try { (DataContext as MainWindowViewModel)?.PersistAll(); }
            catch (Exception) { }
        };
    }

    /// <summary>
    /// Keeps the build log pinned to the newest line.
    ///
    /// This lives in code-behind on purpose: "scroll to the end when a line arrives" is a
    /// property of the view, and expressing it as a bindable view-model flag would put
    /// scroll state into the model for no gain.
    /// </summary>
    private void AttachLogAutoScroll()
    {
        if (_observedLog is not null)
            _observedLog.CollectionChanged -= OnLogChanged;

        _observedLog = (DataContext as MainWindowViewModel)?.Upload.Log;

        if (_observedLog is not null)
            _observedLog.CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _scrollQueued) return;

        // Deferred and coalesced on purpose. Scrolling inline would re-enter the
        // virtualising panel while it is still handling this same collection change, and
        // would read ItemCount before the ListBox's own handler has updated it. steamcmd
        // also emits log lines in bursts of hundreds, and one scroll per burst is enough.
        _scrollQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;

            // The control does not exist until the Upload tab has been shown at least once.
            var list = this.FindControl<ListBox>("LogList");
            if (list is { ItemCount: > 0 }) list.ScrollIntoView(list.ItemCount - 1);
        }, DispatcherPriority.Background);
    }
}
