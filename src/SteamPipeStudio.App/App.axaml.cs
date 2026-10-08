using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using SteamPipeStudio.App.Services;
using SteamPipeStudio.App.ViewModels;
using SteamPipeStudio.App.Views;
using SteamPipeStudio.Core.Model;

namespace SteamPipeStudio.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var updater = new VelopackUpdater();
            var store = updater.PortableDataDirectory is { } portable
                ? ProfileStore.OpenPortable(portable)
                : new ProfileStore();
            var settings = store.LoadSettings();

            RequestedThemeVariant = settings.DarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;

            var window = new MainWindow();
            var viewModel = new MainWindowViewModel(store, settings, window, updater);
            window.DataContext = viewModel;

            // After the window is up, not before: the check goes to the network, and a slow
            // answer must not delay the first frame.
            window.Opened += async (_, _) => await viewModel.Updates.CheckAtStartupAsync();

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
