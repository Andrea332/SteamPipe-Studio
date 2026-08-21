using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamPipeStudio.Core.Model;

/// <summary>Global, non-secret application settings.</summary>
public sealed class AppSettings
{
    /// <summary>Path to <c>sdk/tools/ContentBuilder</c> of the Steamworks SDK in use.</summary>
    public string ContentBuilderPath { get; set; } = string.Empty;

    /// <summary>Last Steam account name used, so the login screen can prefill it.</summary>
    public string LastSteamAccountName { get; set; } = string.Empty;

    public bool DarkTheme { get; set; } = true;

    /// <summary>Ask for confirmation before setting a build live on the default branch.</summary>
    public bool ConfirmSetLive { get; set; } = true;

    /// <summary>How many build-history rows to request from the partner Web API.</summary>
    public int BuildHistoryCount { get; set; } = 20;
}

/// <summary>
/// JSON persistence for <see cref="AppSettings"/> and <see cref="BuildProfile"/>.
///
/// Nothing secret is ever written here: the Steam password is never persisted at all,
/// and the publisher Web API key goes to <see cref="Security.ISecretStore"/> instead.
/// This is the main behavioural difference from the original SteamPipeGUI, which
/// offered a "save password" checkbox and stored it in plain text in user.config.
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _root;

    public ProfileStore(string? rootOverride = null)
    {
        _root = rootOverride ?? DefaultRoot();
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(ProfilesDirectory);
    }

    public string RootDirectory => _root;
    public string ProfilesDirectory => Path.Combine(_root, "profiles");
    public string SettingsPath => Path.Combine(_root, "settings.json");

    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create),
        "SteamPipeStudio");

    public AppSettings LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json)
                   ?? new AppSettings();
        }
        catch (JsonException)
        {
            // A corrupt settings file must not stop the app from starting.
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings) =>
        WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, Json));

    public List<BuildProfile> LoadProfiles()
    {
        var result = new List<BuildProfile>();
        if (!Directory.Exists(ProfilesDirectory)) return result;

        foreach (var file in Directory.EnumerateFiles(ProfilesDirectory, "*.json"))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<BuildProfile>(File.ReadAllText(file), Json);
                if (profile is not null) result.Add(profile);
            }
            catch (JsonException)
            {
                // Skip unreadable profiles rather than losing the whole list.
            }
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public void SaveProfile(BuildProfile profile) =>
        WriteAtomic(PathFor(profile), JsonSerializer.Serialize(profile, Json));

    public void DeleteProfile(BuildProfile profile)
    {
        var path = PathFor(profile);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(BuildProfile profile) =>
        Path.Combine(ProfilesDirectory, profile.Id.ToString("N") + ".json");

    /// <summary>Write via a temporary file so a crash mid-save cannot truncate the original.</summary>
    private static void WriteAtomic(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);

        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
}
