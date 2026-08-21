using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
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
            var store = new ProfileStore();
            var settings = store.LoadSettings();

            RequestedThemeVariant = settings.DarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;

            var window = new MainWindow();
            window.DataContext = new MainWindowViewModel(store, settings, window);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
