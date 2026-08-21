using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SteamPipeStudio.App.Views;

/// <summary>
/// One small modal doing three jobs: asking for a Steam Guard code, asking for a
/// password, and confirming a destructive action. Keeping it to a single window means
/// the app needs no dialog framework at all.
///
/// Controls are resolved with <c>FindControl</c> rather than through the fields
/// Avalonia's name generator produces, so the file compiles the same either way.
/// </summary>
public partial class PromptWindow : Window
{
    public PromptWindow()
    {
        InitializeComponent();
    }

    private TextBlock Title1 => this.FindControl<TextBlock>("TitleText")!;
    private TextBlock Message1 => this.FindControl<TextBlock>("MessageText")!;
    private TextBox Input => this.FindControl<TextBox>("InputBox")!;
    private Button Ok => this.FindControl<Button>("OkButton")!;

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        // An empty box is an abort, not an answer: writing a blank line to steamcmd's
        // stdin just burns one of the Steam Guard attempts.
        if (!Input.IsVisible) { Close(string.Empty); return; }
        Close(string.IsNullOrEmpty(Input.Text) ? null : Input.Text);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    /// <summary>Returns the typed text, or <c>null</c> if the user cancelled.</summary>
    public static Task<string?> AskAsync(Window owner, string title, string message,
                                         string watermark = "", bool masked = false)
    {
        var window = new PromptWindow();
        window.Title1.Text = title;
        window.Message1.Text = message;
        window.Message1.IsVisible = !string.IsNullOrWhiteSpace(message);
        window.Input.Watermark = watermark;
        if (masked) window.Input.PasswordChar = '•';

        // Focus the field immediately: when this appears mid-upload the user is watching
        // the log, not hunting for the mouse.
        window.Opened += (_, _) => window.Input.Focus();

        return window.ShowDialog<string?>(owner);
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        var window = new PromptWindow();
        window.Title1.Text = title;
        window.Message1.Text = message;
        window.Input.IsVisible = false;
        window.Ok.Content = "Confirm";

        return await window.ShowDialog<string?>(owner) is not null;
    }
}
