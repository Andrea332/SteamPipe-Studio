using System;
using System.Text.RegularExpressions;

namespace SteamPipeStudio.Core.Steam;

public enum SteamCmdEventKind
{
    /// <summary>Uninteresting chatter; still shown in the log.</summary>
    Raw,
    Bootstrap,
    LoginPrompt,
    SteamGuardPrompt,
    LoginSucceeded,
    LoginFailed,
    BuildStarted,
    DepotScanning,
    DepotUploading,
    Progress,
    BuildSucceeded,
    BuildFailed,
    /// <summary>An <c>app_update</c> state line; <c>Detail</c> is the state name.</summary>
    Downloading,
    DownloadSucceeded,
    DownloadFailed,
    Error
}

public sealed record SteamCmdEvent(
    SteamCmdEventKind Kind,
    string Line,
    uint? DepotId = null,
    uint? BuildId = null,
    double? Percent = null,
    string? Detail = null);

/// <summary>
/// Classifies steamcmd's console output into structured events.
///
/// Every pattern here is a guess about a program whose output format Valve does not
/// document and does change between SDK releases, so the parser is deliberately
/// permissive: anything it does not recognise falls through as <see
/// cref="SteamCmdEventKind.Raw"/> and is still shown verbatim in the log. If a build
/// stops being detected as successful after an SDK update, this file — and only this
/// file — is what needs updating.
/// </summary>
public static class SteamCmdOutputParser
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // The build-finished line. Valve has reworded it between SDK releases and both
    // shapes are in the wild:
    //   "Successfully finished appID 480 - build 12345678"           (older builders)
    //   "Successfully finished AppID 480 build (BuildID 12345678)."  (SDK 1.63)
    // Only "successfully finished appID <n>" is required here, because getting this
    // wrong is expensive: an unrecognised line makes a perfectly good upload report
    // itself as "steamcmd exited cleanly but never reported a finished build".
    private static readonly Regex BuildSucceeded = new(
        @"success(?:fully)?\s+finished\s+app\s*id\s*(?<app>\d+)(?<tail>.*)", Options);

    // The build id off the tail of that line. Both wordings end with it — "- build
    // 12345678" and "(BuildID 12345678)." — so it is read from the end rather than
    // matched against a fixed label.
    private static readonly Regex TrailingBuildId = new(@"(?<build>\d{4,})\D*$", Options);

    // Some SDK builds print: "Uploading build ... BuildID 12345678"
    private static readonly Regex BuildIdOnly = new(
        @"\bbuild\s*id\b\D{0,4}(?<build>\d{4,})", Options);

    private static readonly Regex BuildFailed = new(
        @"(?:ERROR!\s*)?Failed\s+to\s+(?:build|upload|commit)|AppID\s+\d+\s+build\s+failed", Options);

    private static readonly Regex LoginFailed = new(
        @"FAILED\s*(?:login)?\s*(?:with\s+result\s+code)?\s*[:(]?\s*(?<reason>[A-Za-z ]+)", Options);

    // Deliberately does not accept a bare "OK". steamcmd's own console log splits
    // "Loading Steam API...OK" across two lines, and reading that lone OK as a completed
    // login would announce a session that does not exist yet — and, worse, tell the
    // runner to stop watching for the login prompt that is about to appear.
    private static readonly Regex LoginOk = new(
        @"^\s*(?:Logged\s+in\s+OK|Waiting\s+for\s+(?:client\s+config|user\s+info))", Options);

    // "Logging in user 'x' [U:1:0] to Steam Public...OK" — the verdict is appended to the
    // line that announces the attempt, so it has to be read before LoggingIn below.
    private static readonly Regex LoginResultOk = new(
        @"to\s+Steam\s+\S+\s*\.{2,}\s*OK\b", Options);

    /// <summary>
    /// The <c>[2026-08-21 16:12:10] </c> stamp steamcmd puts on every line of its own
    /// console log. The log file is the only place some output — the password prompt
    /// included — appears in time to be useful, so its lines go through this same parser;
    /// without stripping the stamp first every pattern anchored to the start of a line
    /// would silently stop matching.
    /// </summary>
    /// The negative lookahead is not decoration. steamcmd stamps its own build messages
    /// too, and those read <c>[2026-08-21 16:27:42]: Starting AppID …</c> — a colon after
    /// the bracket. In the log file such a line carries both stamps. Stripping the inner
    /// one as well leaves the file's copy and the pipe's copy of the same message looking
    /// different, and every one of them then prints twice.
    private static readonly Regex LogTimestamp = new(
        @"^\s*\[\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}\](?!:)\s?", Options);

    private static readonly Regex LoggingIn = new(
        @"Logging\s+in\s+user\s+'(?<user>[^']*)'", Options);

    // "Logging in user 'x' [U:1:0] to Steam Public...ERROR (Invalid Password)". The
    // result is appended to the same line that announces the attempt, so without this the
    // LoggingIn pattern above claims the line first and a rejected login is reported as
    // routine chatter — leaving the user with a bare exit code instead of the reason.
    private static readonly Regex LoginResultFailed = new(
        @"to\s+Steam\s+\S+\s*\.{2,}\s*(?:ERROR|FAILED)\s*\(?\s*(?<reason>[^)\r\n]*)", Options);

    private static readonly Regex SteamGuard = new(
        @"(?:Steam\s*Guard|Two-?factor)\s*code|please\s+check\s+your\s+email|mobile\s+authenticator", Options);

    private static readonly Regex PasswordPrompt = new(@"^\s*password\s*:", Options);

    private static readonly Regex DepotScanning = new(
        @"(?:Scanning|Building\s+file\s+mapping|Building)\s+depot\s+(?<depot>\d+)|Scanning\s+content", Options);

    private static readonly Regex DepotUploading = new(
        @"Uploading\s+(?:depot\s+(?<depot>\d+)\s+)?content", Options);

    private static readonly Regex DepotCommitted = new(
        @"Depot\s+Build\s+for\s+DepotID\s+(?<depot>\d+)|Successfully\s+committed\s+depot\s+(?<depot2>\d+)", Options);

    // steamcmd's own self-update banner: "[  0%] Checking for available updates..."
    private static readonly Regex BootstrapProgress = new(
        @"^\s*\[\s*(?<pct>\d{1,3})%\]\s*(?<text>.*)$", Options);

    // Generic "42.13%" or "42%" anywhere in the line.
    private static readonly Regex InlinePercent = new(
        @"(?<pct>\d{1,3}(?:\.\d+)?)\s*%", Options);

    private static readonly Regex GenericError = new(
        @"^\s*(?:ERROR!?|FATAL|Fatal\s+Error)\b", Options);

    // ---- app_update, i.e. downloading a build ----
    //
    // " Update state (0x61) downloading, progress: 45.17 (1234567 / 2734567)". There is
    // no percent sign, so the generic percent pattern below never sees these, and the
    // state name is worth showing: "verifying install" for minutes is not a hang.
    private static readonly Regex UpdateState = new(
        @"Update\s+state\s*\(0x[0-9A-F]+\)\s*(?<state>[^,(]*?)\s*(?:,\s*progress:\s*(?<pct>\d{1,3}(?:\.\d+)?))?\s*(?:\(|$)",
        Options);

    // "Success! App '480' fully installed." — the only line that means the download is
    // complete. As with the build-finished line, the exit code alone is not trusted.
    private static readonly Regex AppInstalled = new(
        @"Success!\s*App\s*'?(?<app>\d+)'?\s*fully\s+installed", Options);

    // "Error! App '480' state is 0x202 after update job." — the state is the only clue
    // steamcmd gives, and the common ones have well-known meanings.
    private static readonly Regex AppUpdateFailed = new(
        @"Error!\s*App\s*'?(?<app>\d+)'?\s*state\s+is\s+(?<state>0x[0-9A-F]+)\s+after\s+update\s+job",
        Options);

    // "ERROR! Failed to install app '480' (No subscription)" — refused before anything
    // was downloaded; the reason in brackets is Steam's own wording.
    private static readonly Regex AppInstallRefused = new(
        @"Failed\s+to\s+install\s+app\s*'?(?<app>\d+)'?\s*\((?<reason>[^)]*)\)", Options);

    /// <summary>
    /// The same line without steamcmd's log stamp, used to recognise a line that arrives
    /// on two channels — the console log writes it stamped, the pipe delivers it bare.
    /// </summary>
    public static string StripLogTimestamp(string line) => LogTimestamp.Replace(line.Trim(), string.Empty);

    public static SteamCmdEvent Parse(string line)
    {
        var trimmed = StripLogTimestamp(line);
        if (trimmed.Length == 0) return new SteamCmdEvent(SteamCmdEventKind.Raw, line);

        var success = BuildSucceeded.Match(trimmed);
        if (success.Success)
        {
            // A wording this parser cannot mine a build id out of is still a success;
            // the run is reported as finished, just without a number.
            var finished = TrailingBuildId.Match(success.Groups["tail"].Value);
            return new SteamCmdEvent(SteamCmdEventKind.BuildSucceeded, line,
                BuildId: finished.Success ? ParseUInt(finished.Groups["build"].Value) : null);
        }

        if (BuildFailed.IsMatch(trimmed))
            return new SteamCmdEvent(SteamCmdEventKind.BuildFailed, line, Detail: trimmed);

        // The app_update lines go before the login patterns on purpose: "Failed to
        // install app" would otherwise be claimed by the generic FAILED matcher below and
        // reported as a login failure with the reason "to install app".
        var installed = AppInstalled.Match(trimmed);
        if (installed.Success)
            return new SteamCmdEvent(SteamCmdEventKind.DownloadSucceeded, line,
                Detail: installed.Groups["app"].Value);

        var updateFailed = AppUpdateFailed.Match(trimmed);
        if (updateFailed.Success)
            return new SteamCmdEvent(SteamCmdEventKind.DownloadFailed, line,
                Detail: ExplainUpdateState(updateFailed.Groups["app"].Value,
                                           updateFailed.Groups["state"].Value));

        var refused = AppInstallRefused.Match(trimmed);
        if (refused.Success)
            return new SteamCmdEvent(SteamCmdEventKind.DownloadFailed, line,
                Detail: ExplainInstallRefusal(refused.Groups["app"].Value,
                                              refused.Groups["reason"].Value.Trim()));

        var updateState = UpdateState.Match(trimmed);
        if (updateState.Success)
            return new SteamCmdEvent(SteamCmdEventKind.Downloading, line,
                Percent: ParseDouble(updateState.Groups["pct"].Value),
                Detail: updateState.Groups["state"].Value.Trim());

        if (SteamGuard.IsMatch(trimmed))
            return new SteamCmdEvent(SteamCmdEventKind.SteamGuardPrompt, line, Detail: trimmed);

        if (PasswordPrompt.IsMatch(trimmed))
            return new SteamCmdEvent(SteamCmdEventKind.LoginPrompt, line);

        // Before LoggingIn, which would otherwise swallow the whole line including its
        // verdict.
        var loginResult = LoginResultFailed.Match(trimmed);
        if (loginResult.Success)
            return new SteamCmdEvent(SteamCmdEventKind.LoginFailed, line,
                Detail: loginResult.Groups["reason"].Value.Trim() is { Length: > 0 } reason
                    ? reason
                    : trimmed);

        if (LoginResultOk.IsMatch(trimmed))
            return new SteamCmdEvent(SteamCmdEventKind.LoginSucceeded, line);

        var loggingIn = LoggingIn.Match(trimmed);
        if (loggingIn.Success)
            return new SteamCmdEvent(SteamCmdEventKind.Bootstrap, line,
                Detail: loggingIn.Groups["user"].Value);

        // Check failure before success: "FAILED" lines can contain "OK" substrings.
        var loginFail = LoginFailed.Match(trimmed);
        if (loginFail.Success)
            return new SteamCmdEvent(SteamCmdEventKind.LoginFailed, line,
                Detail: loginFail.Groups["reason"].Value.Trim());

        if (LoginOk.IsMatch(trimmed))
            return new SteamCmdEvent(SteamCmdEventKind.LoginSucceeded, line);

        var bootstrap = BootstrapProgress.Match(trimmed);
        if (bootstrap.Success)
            return new SteamCmdEvent(SteamCmdEventKind.Bootstrap, line,
                Percent: ParseDouble(bootstrap.Groups["pct"].Value),
                Detail: bootstrap.Groups["text"].Value.Trim());

        var uploading = DepotUploading.Match(trimmed);
        if (uploading.Success)
            return new SteamCmdEvent(SteamCmdEventKind.DepotUploading, line,
                DepotId: ParseUInt(uploading.Groups["depot"].Value),
                Percent: TryInlinePercent(trimmed));

        var scanning = DepotScanning.Match(trimmed);
        if (scanning.Success)
            return new SteamCmdEvent(SteamCmdEventKind.DepotScanning, line,
                DepotId: ParseUInt(scanning.Groups["depot"].Value),
                Percent: TryInlinePercent(trimmed));

        var committed = DepotCommitted.Match(trimmed);
        if (committed.Success)
            return new SteamCmdEvent(SteamCmdEventKind.BuildStarted, line,
                DepotId: ParseUInt(committed.Groups["depot"].Value)
                         ?? ParseUInt(committed.Groups["depot2"].Value));

        if (GenericError.IsMatch(trimmed))
            return new SteamCmdEvent(SteamCmdEventKind.Error, line, Detail: trimmed);

        var buildId = BuildIdOnly.Match(trimmed);
        if (buildId.Success)
            return new SteamCmdEvent(SteamCmdEventKind.Progress, line,
                BuildId: ParseUInt(buildId.Groups["build"].Value));

        var percent = TryInlinePercent(trimmed);
        if (percent is not null)
            return new SteamCmdEvent(SteamCmdEventKind.Progress, line, Percent: percent);

        return new SteamCmdEvent(SteamCmdEventKind.Raw, line);
    }

    /// <summary>
    /// The states steamcmd leaves an app in when <c>app_update</c> fails. Valve does not
    /// document them; these are the ones that come up in practice, with the meanings the
    /// server-hosting community has pinned down (LinuxGSM keeps the list). Anything else
    /// gets the generic advice plus the raw state, which is still more than "exit code 8".
    /// </summary>
    private static string ExplainUpdateState(string app, string state)
    {
        var why = state.ToUpperInvariant() switch
        {
            "0X202" or "0X206" =>
                "Steam reports there is not enough free disk space in the destination.",
            "0X602" or "0X606" =>
                "steamcmd could not write to the destination folder — check its permissions.",
            "0X402" =>
                "The connection to Steam's content servers dropped; try again.",
            _ =>
                "Check the branch name and its password, that the account owns the app, " +
                "and that the destination folder is writable."
        };

        return $"steamcmd could not finish installing AppID {app} (state {state}). {why}";
    }

    private static string ExplainInstallRefusal(string app, string reason)
    {
        if (reason.Contains("No subscription", StringComparison.OrdinalIgnoreCase))
            return $"Steam says this account does not own AppID {app}, so it cannot download it. " +
                   "Downloads need an account with the app in its library — the publisher's own " +
                   "account, or one that redeemed a developer comp key.";

        if (reason.Contains("Missing configuration", StringComparison.OrdinalIgnoreCase))
            return $"Steam has no depot configuration for AppID {app} on this branch and platform. " +
                   "Check the branch name, and whether the build has depots for the platform " +
                   "being downloaded.";

        if (reason.Contains("Invalid platform", StringComparison.OrdinalIgnoreCase))
            return $"AppID {app} has nothing to install for the platform being downloaded.";

        return $"steamcmd refused to install AppID {app}: {reason}.";
    }

    private static double? TryInlinePercent(string line)
    {
        var match = InlinePercent.Match(line);
        if (!match.Success) return null;
        var value = ParseDouble(match.Groups["pct"].Value);
        return value is >= 0 and <= 100 ? value : null;
    }

    private static uint? ParseUInt(string? value) =>
        uint.TryParse(value, out var parsed) ? parsed : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
