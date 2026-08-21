using System;
using System.Collections.Generic;
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
/// The upload workflow: generate scripts, validate, log in, run the build.
///
/// Credentials policy, and the main reason this is not a straight port of the 2018
/// tool: the password is never written to disk and never appears on a command line.
/// steamcmd is always invoked as <c>+login &lt;account&gt;</c>, which makes it either
/// reuse the refresh token it cached under its own config folder or prompt on stdin —
/// and the prompt is answered through <see cref="ISteamCmdPrompt"/>, in memory, once.
/// A password on the command line would be visible to every other process on the
/// machine via the process list, and in the original tool it was also persisted in
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
        5 => "steamcmd exited with code 5 (login failure). If the account has Steam Guard, " +
             "sign in once from the Account screen so a session token gets cached.",
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
