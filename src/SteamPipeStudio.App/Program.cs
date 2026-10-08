// SteamPipe Studio — a build uploader for Steam.
// Copyright (C) 2026  Andrea Galet
//
// This program is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software Foundation,
// either version 3 of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY
// WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
// PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with this
// program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using Avalonia;
using Velopack;

namespace SteamPipeStudio.App;

internal static class Program
{
    // Avalonia needs this to run before any control is created, and it must not use
    // any Avalonia type itself.
    [STAThread]
    public static void Main(string[] args)
    {
        // First thing, before a window exists: the installer, the uninstaller and the
        // updater start the app with arguments of their own, and this handles those runs
        // and exits. It is also where an update downloaded earlier gets applied. On an
        // ordinary start, including `dotnet run`, it returns straight away.
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
