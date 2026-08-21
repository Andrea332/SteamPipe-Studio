using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamPipeStudio.Core.Build;
using SteamPipeStudio.Core.Model;

namespace SteamPipeStudio.Core.Steam;

public sealed record UploadOutcome(
    bool Succeeded,
    uint? BuildId,
    string? FailureDetail,
    string AppScriptPath,
    string? BuildLogPath);

/// <summary>
/// Whose depots <c>app_update</c> fetches. <see cref="Host"/> leaves the choice to
/// steamcmd, which installs for the machine it runs on; the others force it, which is
/// how a Windows box pulls the macOS build to look at it.
/// </summary>
public enum DownloadPlatform
{
    Host,
    Windows,
    MacOS,
    Linux
}

/// <summary>
/// What to download and where. The app and the account come from the profile: a
/// download logs in exactly like an upload does.
/// </summary>
public sealed record DownloadRequest(
    string Branch,
    string? BranchPassword,
    string InstallDirectory,
    DownloadPlatform Platform = DownloadPlatform.Host);

public sealed record DownloadOutcome(
    bool Succeeded,
    string? FailureDetail,
    string InstallDirectory);

/// <summary>
/// The upload workflow: generate scripts, validate, log in, run the build.
///
/// Credentials policy, and the main reason this is not a straight port of the 2018
/// tool: the password never appears on a command line and never goes into the profile
/// file. steamcmd is always invoked as <c>+login &lt;account&gt;</c>, which makes it either
/// reuse the refresh token it cached under its own config folder or prompt on stdin —
/// and the prompt is answered through <see cref="ISteamCmdPrompt"/>, in memory, once per
/// ask. Whether that answer is typed by the user or read from the platform secret store
/// (the optional per-account password the App layer keeps under DPAPI, the keychain or
/// the keyring) is the prompt implementation's business; this class only ever writes it
/// to stdin. A password on the command line would be visible to every other process on
/// the machine via the process list, and in the original tool it was also persisted in
/// clear text in user.config.
/// </summary>
public sealed class SteamCmdSession
{
    private readonly ISteamCmdPrompt _prompt;

    public SteamCmdSession(ISteamCmdPrompt prompt) => _prompt = prompt;

    public event Action<SteamCmdEvent>? Output;

    /// <summary>
    /// Verifies that the cached session still works, without touching a build.
    /// Runs <c>+login &lt;account&gt; +info +quit</c>, which is cheap and prints the
    /// account state.
    /// </summary>
    public async Task<bool> TestLoginAsync(string contentBuilderPath, string accountName,
                                           CancellationToken cancellation = default)
    {
        if (!SteamCmdLocator.TryLocate(contentBuilderPath, out var steamCmd, out var error))
            throw new FileNotFoundException(error);

        var runner = CreateRunner();
        var result = await runner
            .RunAsync(steamCmd, new[] { "+login", accountName, "+info", "+quit" }, cancellation)
            .ConfigureAwait(false);

        return result.ExitCode == 0 && result.FailureDetail is null;
    }

    public async Task<UploadOutcome> UploadAsync(
        BuildProfile profile,
        AppSettings settings,
        string scriptOutputDirectory,
        CancellationToken cancellation = default)
    {
        var contentBuilderPath = !string.IsNullOrWhiteSpace(profile.ContentBuilderPathOverride)
            ? profile.ContentBuilderPathOverride
            : settings.ContentBuilderPath;

        if (!SteamCmdLocator.TryLocate(contentBuilderPath, out var steamCmd, out var error))
            throw new FileNotFoundException(error);

        var issues = BuildValidator.Validate(profile, settings);
        if (BuildValidator.HasBlockingIssues(issues))
            throw new InvalidOperationException(
                "Fix these before uploading:\n  " +
                string.Join("\n  ", issues.Where(i => i.Severity == IssueSeverity.Error)
                                          .Select(i => $"{i.Field}: {i.Message}")));

        Directory.CreateDirectory(profile.BuildOutput);
        var appScriptPath = BuildScriptGenerator.WriteTo(profile, scriptOutputDirectory);

        Output?.Invoke(new SteamCmdEvent(SteamCmdEventKind.Bootstrap,
            $"Generated build scripts in {scriptOutputDirectory}"));

        await WarmUpAsync(steamCmd, cancellation).ConfigureAwait(false);

        var runner = CreateRunner();

        // steamcmd resolves +run_app_build relative to its own working directory, which
        // the runner sets to the builder folder. Passing an absolute path removes the
        // ambiguity entirely and lets scripts live wherever the project wants them.
        var arguments = new List<string>
        {
            "+login", profile.SteamAccountName,
            "+run_app_build", Path.GetFullPath(appScriptPath),
            "+quit"
        };

        var result = await runner.RunAsync(steamCmd, arguments, cancellation).ConfigureAwait(false);

        return new UploadOutcome(
            result.Succeeded,
            result.BuildId,
            result.FailureDetail ?? (result.Succeeded ? null : DescribeExit(result.ExitCode)),
            appScriptPath,
            FindLatestBuildLog(profile.BuildOutput));
    }

    /// <summary>
    /// Downloads the build that is live on a branch into a folder, with
    /// <c>app_update</c>: the same files, in the same layout, that a player installing
    /// from that branch receives. A build is not a file on Steam but a set of depot
    /// manifests, so "downloading a build" means asking steamcmd to install the branch it
    /// is live on; a build that is on no branch has to be promoted to one first — a
    /// private branch will do.
    ///
    /// Two things about the command line are not negotiable. <c>force_install_dir</c> has
    /// to come before <c>login</c>, or steamcmd prints a warning and installs under its
    /// own folder instead. And the branch password, when the branch has one, travels in
    /// a <c>+runscript</c> file rather than on the command line, for the same reason the
    /// account password never does: the process list is readable by every other program
    /// on the machine. The file lives for the duration of the run and is owner-only where
    /// the filesystem can express that.
    /// </summary>
    public async Task<DownloadOutcome> DownloadAsync(
        BuildProfile profile,
        AppSettings settings,
        DownloadRequest request,
        CancellationToken cancellation = default)
    {
        if (DownloadProblem(profile, request) is { } problem)
            throw new InvalidOperationException(problem);

        var contentBuilderPath = !string.IsNullOrWhiteSpace(profile.ContentBuilderPathOverride)
            ? profile.ContentBuilderPathOverride
            : settings.ContentBuilderPath;

        if (!SteamCmdLocator.TryLocate(contentBuilderPath, out var steamCmd, out var error))
            throw new FileNotFoundException(error);

        var installDirectory = Path.GetFullPath(request.InstallDirectory);

        // steamcmd would create it too, but creating it here turns a permissions problem
        // into an exception with the path in it, before a login has been asked for.
        Directory.CreateDirectory(installDirectory);

        await WarmUpAsync(steamCmd, cancellation).ConfigureAwait(false);

        string? passwordScript = null;
        try
        {
            if (!string.IsNullOrEmpty(request.BranchPassword))
                passwordScript = WritePasswordScript(profile.AppId, request.Branch, request.BranchPassword);

            var arguments = DownloadArguments(profile.AppId, profile.SteamAccountName,
                                              request with { InstallDirectory = installDirectory },
                                              passwordScript);

            Output?.Invoke(new SteamCmdEvent(SteamCmdEventKind.Bootstrap,
                $"Downloading AppID {profile.AppId} from branch '{request.Branch}' into {installDirectory}"));

            var runner = CreateRunner();
            var result = await runner.RunAsync(steamCmd, arguments, cancellation).ConfigureAwait(false);

            return new DownloadOutcome(
                result.Succeeded,
                result.FailureDetail ?? (result.Succeeded ? null : DescribeDownloadExit(result)),
                installDirectory);
        }
        finally
        {
            if (passwordScript is not null)
            {
                try { File.Delete(passwordScript); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// Why a download cannot start, or <c>null</c> when it can. Pure, so the tests can
    /// pin the checks without a steamcmd install.
    /// </summary>
    internal static string? DownloadProblem(BuildProfile profile, DownloadRequest request)
    {
        if (profile.AppId == 0)
            return "The project has no App ID.";

        if (string.IsNullOrWhiteSpace(profile.SteamAccountName))
            return "Enter the Steam account on the Project tab first: a download logs in with it, " +
                   "exactly like an upload.";

        if (string.IsNullOrWhiteSpace(request.Branch))
            return "Pick a branch the build is live on.";

        if (string.IsNullOrWhiteSpace(request.InstallDirectory))
            return "Choose a folder to download into.";

        // The same mistake the validator refuses for the build output, with the same
        // consequence: the next upload would ship the downloaded build inside the game.
        if (BuildValidator.IsInside(request.InstallDirectory, profile.ContentRoot))
            return "The download folder is inside the content folder, so the next upload would " +
                   "include the downloaded build in the game. Pick a folder outside it.";

        return null;
    }

    /// <summary>
    /// The steamcmd command line for a download. Internal so the tests can pin the order,
    /// which steamcmd is picky about: the platform override and <c>force_install_dir</c>
    /// both have to precede <c>login</c>. With a branch password the <c>app_update</c>
    /// moves into the script at <paramref name="passwordScriptPath"/>, so that neither
    /// the password nor anything else about the branch is on the command line.
    /// </summary>
    internal static List<string> DownloadArguments(uint appId, string accountName,
                                                   DownloadRequest request, string? passwordScriptPath)
    {
        var arguments = new List<string>();

        if (PlatformName(request.Platform) is { } platform)
            arguments.AddRange(new[] { "+@sSteamCmdForcePlatformType", platform });

        arguments.AddRange(new[]
        {
            "+force_install_dir", Path.GetFullPath(request.InstallDirectory),
            "+login", accountName
        });

        if (passwordScriptPath is null)
        {
            arguments.Add("+app_update");
            arguments.AddRange(AppUpdateArguments(appId, request.Branch, null));
        }
        else
        {
            arguments.AddRange(new[] { "+runscript", passwordScriptPath });
        }

        arguments.Add("+quit");
        return arguments;
    }

    /// <summary>The one-line script that carries the branch password.</summary>
    internal static string PasswordScript(uint appId, string branch, string password) =>
        "app_update " + string.Join(' ', AppUpdateArguments(appId, branch, password));

    private static IEnumerable<string> AppUpdateArguments(uint appId, string branch, string? password)
    {
        yield return appId.ToString(CultureInfo.InvariantCulture);

        // "-beta public" is not a thing steamcmd understands as "the default branch";
        // leaving the switch off is.
        if (!IsDefaultBranch(branch))
        {
            yield return "-beta";
            yield return branch;
        }

        if (!string.IsNullOrEmpty(password))
        {
            yield return "-betapassword";
            // steamcmd's script reader splits on spaces and honours double quotes.
            yield return password.Any(char.IsWhiteSpace) ? "\"" + password + "\"" : password;
        }

        // Re-verifies files already in the folder, which is what makes downloading a
        // second build into the same folder an incremental update rather than a mess.
        yield return "validate";
    }

    internal static bool IsDefaultBranch(string? branch) =>
        string.IsNullOrWhiteSpace(branch) || branch.Equals("public", StringComparison.OrdinalIgnoreCase);

    private static string? PlatformName(DownloadPlatform platform) => platform switch
    {
        DownloadPlatform.Windows => "windows",
        DownloadPlatform.MacOS => "macos",
        DownloadPlatform.Linux => "linux",
        _ => null
    };

    private static string WritePasswordScript(uint appId, string branch, string password)
    {
        var path = Path.Combine(Path.GetTempPath(), "steampipe-" + Guid.NewGuid().ToString("N") + ".txt");

        // CreateNew, so a file planted at a guessed name is an error rather than a target;
        // owner-only on Unix, where the temp folder is shared between users.
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(PasswordScript(appId, branch, password));
        writer.Write('\n');

        return path;
    }

    private static string DescribeDownloadExit(SteamCmdResult result) => result switch
    {
        { SawSuccess: true } =>
            $"steamcmd reported the app fully installed but exited with code {result.ExitCode}. " +
            "The files should be in place; check the log above.",
        { ExitCode: 0 } =>
            "steamcmd exited cleanly but never reported the app as fully installed. " +
            "Check the log above — the last lines before it quit usually say why.",
        { ExitCode: 5 } =>
            "steamcmd exited with code 5 (login failure): the account name, the password " +
            "or the Steam Guard code was rejected.",
        { ExitCode: 8 } =>
            "steamcmd exited with code 8: the download did not complete. " +
            "The error line above it says why.",
        _ => $"steamcmd exited with code {result.ExitCode}."
    };

    /// <summary>
    /// Runs <c>steamcmd +quit</c> and waits for it to finish, before the build.
    ///
    /// This exists because of one specific, reproducible hang. When steamcmd finds an
    /// update for itself it installs it and <em>re-executes</em> as a child process. The
    /// build run then loses its output stream part-way through the child's startup: the
    /// log stops at the version banner, and everything after it — including the
    /// "password:" prompt — never reaches the parser. Nothing answers the prompt, nothing
    /// times out, and both sides wait for each other until the user notices.
    ///
    /// Letting the update happen in a throwaway process costs a couple of seconds on a
    /// current install and removes the case entirely: by the time the build runs, there
    /// is nothing left for steamcmd to update and no relaunch.
    ///
    /// No prompt handler is attached on purpose. <c>+quit</c> alone never asks for
    /// credentials, and a dialog appearing during what the user sees as preparation would
    /// be worse than the hang it prevents.
    /// </summary>
    private async Task WarmUpAsync(string steamCmdPath, CancellationToken cancellation)
    {
        Output?.Invoke(new SteamCmdEvent(SteamCmdEventKind.Bootstrap,
            "Preparing steamcmd (letting it finish any self-update)…"));

        var runner = new SteamCmdRunner();
        runner.Output += evt => Output?.Invoke(evt);

        try
        {
            await runner.RunAsync(steamCmdPath, new[] { "+quit" }, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            // A warm-up that cannot start is not a reason to refuse the upload: the build
            // run below fails on its own, with an error about the actual build.
            Output?.Invoke(new SteamCmdEvent(SteamCmdEventKind.Raw,
                $"Could not pre-run steamcmd ({e.Message}); continuing."));
        }
    }

    private SteamCmdRunner CreateRunner()
    {
        var runner = new SteamCmdRunner(_prompt);
        runner.Output += evt => Output?.Invoke(evt);
        return runner;
    }

    private static string DescribeExit(int exitCode) => exitCode switch
    {
        0 => "steamcmd exited cleanly but never reported a finished build. " +
             "Check the build log — this usually means a depot failed to commit.",
        1 => "steamcmd exited with code 1. The most common causes are a rejected login " +
             "and a content root that no longer exists.",
        5 => "steamcmd exited with code 5 (login failure): the account name, the password " +
             "or the Steam Guard code was rejected. If a password is saved for this account " +
             "on the Project tab, check that it is still the current one.",
        _ => $"steamcmd exited with code {exitCode}."
    };

    /// <summary>
    /// steamcmd writes per-depot and per-app logs into the build output folder. Surfacing
    /// the newest one turns "it failed" into something the user can actually read; the
    /// original tool's habit of tailing these logs was its single best feature.
    /// </summary>
    public static string? FindLatestBuildLog(string buildOutput)
    {
        if (string.IsNullOrWhiteSpace(buildOutput) || !Directory.Exists(buildOutput)) return null;

        try
        {
            string? newest = null;
            var newestWrite = DateTime.MinValue;

            foreach (var file in Directory.EnumerateFiles(buildOutput, "*.log",
                         new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                var written = File.GetLastWriteTimeUtc(file);
                if (written <= newestWrite) continue;
                newestWrite = written;
                newest = file;
            }

            return newest;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
