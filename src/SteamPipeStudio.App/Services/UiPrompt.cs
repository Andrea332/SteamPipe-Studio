using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SteamPipeStudio.App.Views;
using SteamPipeStudio.Core.Steam;

namespace SteamPipeStudio.App.Services;

/// <summary>
/// Bridges steamcmd's interactive prompts to the UI.
///
/// Both methods are called from the runner's output pump, which is a background thread,
/// so every one of them hops to the UI thread before touching a window.
/// </summary>
public sealed class UiPrompt : ISteamCmdPrompt
{
    private readonly Window _owner;

    public UiPrompt(Window owner) => _owner = owner;

    public Task<string?> RequestSteamGuardCodeAsync(string message, CancellationToken cancellation) =>
        OnUiThread(() => PromptWindow.AskAsync(
            _owner,
            "Steam Guard",
            string.IsNullOrWhiteSpace(message)
                ? "Enter the Steam Guard code for this account."
                : message,
            "e.g. K4T9X"));

    public Task<string?> RequestPasswordAsync(string accountName, CancellationToken cancellation) =>
        OnUiThread(() => PromptWindow.AskAsync(
            _owner,
            "Steam password",
            "steamcmd has no valid cached session for this account. The password is used " +
            "once, in memory, and is never saved.",
            "Password",
            masked: true));

    public Task<bool> ConfirmAsync(string title, string message) =>
        OnUiThread(() => PromptWindow.ConfirmAsync(_owner, title, message));

    /// <summary>
    /// Puts text on the system clipboard. Returns false when there is no clipboard to
    /// write to — a headless run, or an X11 session with no selection owner — which is
    /// worth a status line and not an exception.
    /// </summary>
    public Task<bool> CopyToClipboardAsync(string text) =>
        OnUiThread(async () =>
        {
            var clipboard = TopLevel.GetTopLevel(_owner)?.Clipboard;
            if (clipboard is null) return false;

            await clipboard.SetTextAsync(text);
            return true;
        });

    /// <summary>
    /// Marshals an async UI operation onto the UI thread and hands the caller a task it
    /// can await from wherever it is. Written against <c>Post</c> plus a completion
    /// source rather than an <c>InvokeAsync</c> overload, because the overload that
    /// unwraps an async delegate has moved between Avalonia versions and this does not.
    /// </summary>
    private static Task<T> OnUiThread<T>(Func<Task<T>> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(async () =>
        {
            try { completion.TrySetResult(await work()); }
            catch (Exception e) { completion.TrySetException(e); }
        });

        return completion.Task;
    }

    public async Task<string?> PickFolderAsync(string title, string? startAt = null)
    {
        var storage = TopLevel.GetTopLevel(_owner)?.StorageProvider;
        if (storage is null) return null;

        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(startAt))
        {
            // Best effort only: a stale or malformed path may throw anything from
            // FormatException to a platform-specific IO error, and none of it should
            // stop the picker from opening at its default location.
            try { start = await storage.TryGetFolderFromPathAsync(startAt); }
            catch (Exception) { start = null; }
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title, string extension, string extensionLabel)
    {
        var storage = TopLevel.GetTopLevel(_owner)?.StorageProvider;
        if (storage is null) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new(extensionLabel) { Patterns = new[] { "*." + extension.TrimStart('.') } },
                new("All files") { Patterns = new[] { "*" } }
            }
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string extension)
    {
        var storage = TopLevel.GetTopLevel(_owner)?.StorageProvider;
        if (storage is null) return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.')
        });

        return file?.TryGetLocalPath();
    }
}
