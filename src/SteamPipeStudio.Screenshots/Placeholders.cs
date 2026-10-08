using System;
using SteamPipeStudio.Core.Model;

namespace SteamPipeStudio.Screenshots;

/// <summary>
/// The made-up studio the screenshot shows. App and depot IDs are the ones Valve's own
/// SDK samples use (1000, 1001…), so nobody mistakes them for a real game's, and the
/// paths are Windows paths because that is where most people will run the app.
/// </summary>
internal static class Placeholders
{
    public static ProfileStore CreateStore(string root)
    {
        var store = new ProfileStore(root);

        store.SaveSettings(new AppSettings
        {
            ContentBuilderPath = @"C:\Steamworks\sdk\tools\ContentBuilder",
            LastSteamAccountName = "studio_builder",
            DarkTheme = true
        });

        store.SaveProfile(new BuildProfile
        {
            Id = new Guid("00000000-0000-0000-0000-000000001000"),
            Name = "My Game",
            AppId = 1000,
            Description = "Release candidate 2",
            ContentRoot = @"C:\Projects\MyGame\Build\Windows",
            BuildOutput = @"C:\Projects\MyGame\SteamPipe Output",
            SteamAccountName = "studio_builder",
            SetLiveBranch = "beta",
            Depots = { DepotDefinition.Create(1001, "Windows content") },
            LastBuildId = 12345678,
            LastUploadedUtc = new DateTimeOffset(2026, 1, 15, 16, 42, 0, TimeSpan.Zero)
        });

        store.SaveProfile(new BuildProfile
        {
            Id = new Guid("00000000-0000-0000-0000-000000002000"),
            Name = "My Game Demo",
            AppId = 2000,
            Description = "Demo for the festival",
            ContentRoot = @"C:\Projects\MyGame\Build\Demo",
            BuildOutput = @"C:\Projects\MyGame\SteamPipe Output (Demo)",
            SteamAccountName = "studio_builder",
            Depots = { DepotDefinition.Create(2001, "Demo content") }
        });

        return store;
    }
}
