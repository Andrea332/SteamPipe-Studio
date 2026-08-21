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

    // "Successfully finished appID 480 - build 12345678"
    private static readonly Regex BuildSucceeded = new(
        @"success(?:fully)?\s+finished\s+app\s*id\s*(?<app>\d+)\s*-\s*build\s*(?<build>\d+)", Options);

    // Some SDK builds print: "Uploading build ... BuildID 12345678"
    private static readonly Regex BuildIdOnly = new(
        @"\bbuild\s*id\b\D{0,4}(?<build>\d{4,})", Options);

    private static readonly Regex BuildFailed = new(
        @"(?:ERROR!\s*)?Failed\s+to\s+(?:build|upload|commit)|AppID\s+\d+\s+build\s+failed", Options);

    private static readonly Regex LoginFailed = new(
        @"FAILED\s*(?:login)?\s*(?:with\s+result\s+code)?\s*[:(]?\s*(?<reason>[A-Za-z ]+)", Options);

    private static readonly Regex LoginOk = new(
        @"^\s*(?:Logged\s+in\s+OK|Waiting\s+for\s+(?:client\s+config|user\s+info)|OK\b)", Options);

    private static readonly Regex LoggingIn = new(
        @"Logging\s+in\s+user\s+'(?<user>[^']*)'", Options);

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

    public static SteamCmdEvent Parse(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return new SteamCmdEvent(SteamCmdEventKind.Raw, line);

        var success = BuildSucceeded.Match(trimmed);
        if (success.Success)
            return new SteamCmdEvent(SteamCmdEventKind.BuildSucceeded, line,
                BuildId: ParseUInt(success.Groups["build"].Value));

        if (BuildFailed.IsMatch(trimmed))
            return new SteamCmdEvent(SteamCmdEventKind.BuildFailed, line, Detail: trimmed);

        if (SteamGuard.IsMatch(trimmed))
            return new SteamCmdEvent(SteamCmdEventKind.SteamGuardPrompt, line, Detail: trimmed);

        if (PasswordPrompt.IsMatch(trimmed))
            return new SteamCmdEvent(SteamCmdEventKind.LoginPrompt, line);

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
