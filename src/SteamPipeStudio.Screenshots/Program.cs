using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SteamPipeStudio.App.ViewModels;
using SteamPipeStudio.App.Views;

namespace SteamPipeStudio.Screenshots;

/// <summary>
/// Renders the README screenshot: the real main window, built from the real XAML and
/// view models, drawn by Skia without a screen and filled with placeholder projects.
///
/// Placeholders rather than anyone's data, because the picture is public and is taken
/// on a CI runner that has no projects anyway; and fixed values, dates included, so a
/// release that does not change the UI produces the same image byte for byte and the
/// workflow has nothing to commit.
///
/// Usage: <c>SteamPipeStudio.Screenshots &lt;output.png&gt; [version]</c>
/// </summary>
internal static class Program
{
    private const double Scale = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "showcase.png");
        var version = args.Length > 1 ? args[1] : "dev";

        AppBuilder.Configure<SteamPipeStudio.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        var root = Path.Combine(Path.GetTempPath(), "steampipe-showcase-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = Placeholders.CreateStore(root);

            var window = new MainWindow();
            window.DataContext = new MainWindowViewModel(store, store.LoadSettings(), window,
                                                         new PlaceholderUpdater(version));
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Drawn again at twice the resolution rather than captured from the headless
            // screen, which renders at 1x: the README is read on high-density displays
            // too, where a 1x picture of text looks blurred.
            var size = new PixelSize((int)(window.Bounds.Width * Scale), (int)(window.Bounds.Height * Scale));
            using var frame = new RenderTargetBitmap(size, new Vector(96 * Scale, 96 * Scale));
            frame.Render(window);

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            frame.Save(output);
            window.Close();

            Console.WriteLine($"Wrote {output} ({size.Width}x{size.Height})");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>An updater that is never asked anything: the screenshot is of the Project tab.</summary>
    private sealed class PlaceholderUpdater(string version) : IAppUpdater
    {
        public bool IsUpdatable => true;
        public string CurrentVersion => version;
        public Task<string?> CheckAsync(CancellationToken cancellation) => Task.FromResult<string?>(null);
        public Task DownloadAsync(Action<int> progress, CancellationToken cancellation) => Task.CompletedTask;
        public void ApplyAndRestart() { }
    }
}
