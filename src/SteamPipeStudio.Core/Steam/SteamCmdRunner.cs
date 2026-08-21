using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamPipeStudio.Core.Steam;

/// <summary>Asked for by the runner when steamcmd blocks on an interactive prompt.</summary>
public interface ISteamCmdPrompt
{
    /// <summary>Returns the Steam Guard code, or <c>null</c> to abort the run.</summary>
    Task<string?> RequestSteamGuardCodeAsync(string message, CancellationToken cancellation);

    /// <summary>Returns the account password, or <c>null</c> to abort. Never persisted.</summary>
    Task<string?> RequestPasswordAsync(string accountName, CancellationToken cancellation);
}

public sealed record SteamCmdResult(
    int ExitCode,
    bool SawSuccess,
    uint? BuildId,
    string? FailureDetail)
{
    /// <summary>
    /// steamcmd's exit code is not a reliable success signal on its own — it has
    /// historically returned 0 after a failed depot commit — so a run only counts as
    /// successful when the "Successfully finished appID … - build …" line was seen.
    /// </summary>
    public bool Succeeded => SawSuccess && ExitCode == 0;
}

/// <summary>
/// Launches steamcmd and turns its console output into a live event stream.
///
/// The output pump reads characters rather than lines. steamcmd rewrites progress in
/// place using a bare carriage return and does not terminate those updates with a
/// newline, so <c>ReadLineAsync</c> appears to hang for minutes during a large upload
/// and then dumps everything at once. Reading into a buffer and splitting on both
/// '\n' and '\r' is what makes the progress bar move in real time.
/// </summary>
public sealed class SteamCmdRunner
{
    private readonly ISteamCmdPrompt? _prompt;

    public SteamCmdRunner(ISteamCmdPrompt? prompt = null) => _prompt = prompt;

    /// <summary>Raised for every line steamcmd produces, already classified.</summary>
    public event Action<SteamCmdEvent>? Output;

    public async Task<SteamCmdResult> RunAsync(
        string steamCmdPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellation = default)
    {
        if (!File.Exists(steamCmdPath))
            throw new FileNotFoundException($"steamcmd not found at {steamCmdPath}", steamCmdPath);

        SteamCmdLocator.EnsureExecutable(steamCmdPath);

        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(steamCmdPath))
                               ?? Directory.GetCurrentDirectory();

        var startInfo = new ProcessStartInfo
        {
            FileName = steamCmdPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        // Where the console log stands *before* the run, so the tail below reports this
        // run's output and not the previous one's: steamcmd appends rather than truncates.
        var consoleLog = ConsoleLogPath(steamCmdPath);
        var consoleLogOffset = FileLengthOrZero(consoleLog);

        if (!process.Start())
            throw new InvalidOperationException($"Could not start {steamCmdPath}.");

        var state = new RunState();

        // Cancellation kills the whole tree: steamcmd spawns a child on some platforms
        // and killing only the parent leaves an orphan holding the content lock.
        await using var cancellationRegistration = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { }
        });

        state.LastOutputTicks = Environment.TickCount64;

        var stdout = PumpAsync(process.StandardOutput, process, state, cancellation);
        var stderr = PumpAsync(process.StandardError, process, state, cancellation);
        var consoleTail = TailConsoleLogAsync(consoleLog, consoleLogOffset, process, state, cancellation);
        var watchdog = WatchForSilentPromptAsync(process, state, cancellation);

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr, consoleTail, watchdog).ConfigureAwait(false);

        cancellation.ThrowIfCancellationRequested();

        return new SteamCmdResult(process.ExitCode, state.SawSuccess, state.BuildId, state.FailureDetail);
    }

    private sealed class RunState
    {
        public bool SawSuccess;
        public uint? BuildId;
        public string? FailureDetail;

        /// <summary>
        /// 0 = idle, 1 = a prompt dialog is open. Interlocked because the stdout and
        /// stderr pumps run concurrently and steamcmd echoes prompts on both; a plain
        /// bool lets two dialogs open for the same question.
        /// </summary>
        public int PromptGate;

        /// <summary>
        /// The exact text already answered, cleared as soon as any non-prompt line
        /// arrives. A prompt reaches the pump with no line terminator, so it is inspected
        /// once as an unterminated tail and again when the newline finally lands; without
        /// this the user is asked for the same Steam Guard code twice. It must NOT
        /// survive other output, though — after a wrong code steamcmd prints a failure
        /// and re-prompts with byte-identical text, and swallowing that leaves the
        /// process blocked on stdin forever.
        /// </summary>
        public string? AnsweredPrompt;

        /// <summary><see cref="Environment.TickCount64"/> when output was last seen.</summary>
        public long LastOutputTicks;

        /// <summary>
        /// Lines already shown from steamcmd's console log, waiting for the pipe to
        /// deliver its copy of them so it can be dropped.
        ///
        /// A multiset rather than a set: progress output repeats the same text many
        /// times, and each occurrence has to cancel exactly one occurrence.
        /// </summary>
        public readonly DuplicateFilter Duplicates = new();

        /// <summary>1 once a password has gone to stdin, by either route.</summary>
        public int PasswordAnswered;

        /// <summary>
        /// Set once output is clearly flowing again — a login result, or any depot work.
        /// From then on silence means steamcmd is busy, not waiting, and the watchdog
        /// must stay out of the way: a dialog opening in the middle of a depot commit
        /// would push a stray line into an input nobody is reading.
        /// </summary>
        public volatile bool LoginSettled;
    }

    /// <summary>steamcmd's own console log, next to the executable.</summary>
    internal static string ConsoleLogPath(string steamCmdPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(steamCmdPath)) ?? ".",
                     "logs", "console_log.txt");

    private static long FileLengthOrZero(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Follows steamcmd's console log for the duration of the run.
    ///
    /// The pipe is not a complete picture of what steamcmd is doing, and on Windows it is
    /// not even a timely one — it is block buffered, so a run that stops to ask for a
    /// password shows nothing at all until something answers. Measured side by side on
    /// one run: the log file had "Cached credentials not found." and "password:" 1.3
    /// seconds in; the pipe delivered the same two lines 23 seconds later, and only
    /// because a reply had been sent blind.
    ///
    /// So the file is the live channel and the pipe is the slow one. Reading both and
    /// dropping the second copy gives the user the whole run as it happens, and lets the
    /// password prompt be answered the moment it is asked rather than inferred from
    /// silence.
    /// </summary>
    private async Task TailConsoleLogAsync(string path, long offset, Process process,
                                           RunState state, CancellationToken cancellation)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var line = new StringBuilder();
        var chars = new char[8192];

        // One last pass after the process exits: the final lines are usually written
        // between the last poll and the exit.
        var draining = false;

        while (true)
        {
            var exited = process.HasExited;

            try
            {
                offset = await ReadNewLinesAsync(path, offset, decoder, chars, line, process, state,
                                                 cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The log is steamcmd's, not ours. Losing it costs detail, not the run.
            }

            if (draining) break;
            if (exited) { draining = true; continue; }

            try { await Task.Delay(200, cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { draining = true; }
        }

        if (line.Length > 0)
            await EmitAsync(line.ToString(), process, state, cancellation, fromConsoleLog: true)
                .ConfigureAwait(false);
    }

    private async Task<long> ReadNewLinesAsync(string path, long offset, Decoder decoder,
                                               char[] chars, StringBuilder line, Process process,
                                               RunState state, CancellationToken cancellation)
    {
        if (!File.Exists(path)) return offset;

        // steamcmd keeps the file open for writing for the whole run, so every share flag
        // has to be granted or the open fails outright.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);

        // A rotated or recreated log is shorter than where we were reading.
        if (stream.Length < offset) offset = 0;
        if (stream.Length == offset) return offset;

        stream.Seek(offset, SeekOrigin.Begin);

        var bytes = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(), cancellation).ConfigureAwait(false);
            if (read == 0) break;

            offset += read;
            state.LastOutputTicks = Environment.TickCount64;

            // Decoded incrementally: a UTF-8 sequence split across two reads would
            // otherwise turn into replacement characters.
            var decoded = decoder.GetChars(bytes, 0, read, chars, 0);

            for (var i = 0; i < decoded; i++)
            {
                var c = chars[i];

                if (c is '\n' or '\r')
                {
                    if (c == '\r' && i + 1 < decoded && chars[i + 1] == '\n') i++;

                    if (line.Length > 0)
                    {
                        await EmitAsync(line.ToString(), process, state, cancellation,
                                        fromConsoleLog: true).ConfigureAwait(false);
                        line.Clear();
                    }
                    continue;
                }

                line.Append(c);
            }

            // "password: " is written without a terminator, so the unfinished tail has to
            // be offered to the prompt handler as well — that line is the whole reason
            // this tail exists.
            if (line.Length > 0)
                await MaybeAnswerPromptAsync(line.ToString(), process, state, cancellation)
                    .ConfigureAwait(false);
        }

        return offset;
    }

    /// <summary>
    /// Remembers lines shown from the console log so the pipe's late copy of the same
    /// line can be dropped instead of printing everything twice.
    /// </summary>
    internal sealed class DuplicateFilter
    {
        // Bounded: a long upload emits tens of thousands of progress lines, and keeping
        // all of them would make this the largest allocation in the process.
        private const int Capacity = 3000;

        // How far back a match is looked for. Once output is flowing the pipe trails the
        // log by a line or two, so this is generous; it exists to stop a pathological
        // scan, not to be reached.
        private const int Window = 300;

        private readonly object _gate = new();

        // Shown from the console log, waiting for the pipe's copy. null once claimed.
        private readonly List<string?> _fromLog = new();

        // Shown from the pipe, waiting for the log's copy — which the log may deliver in
        // several pieces, so each entry tracks how much of it has been matched so far.
        private readonly List<PartiallyMatched> _fromPipe = new();

        private sealed class PartiallyMatched
        {
            public PartiallyMatched(string text) => Remaining = text;
            public string Remaining;
        }

        /// <summary>
        /// True when this line was already shown from the console log; consumes it.
        ///
        /// Also accepts a line that is several consecutive log lines run together.
        /// steamcmd's log writer flushes every partial write on its own line while the
        /// pipe delivers the finished one, so "Loading Steam API..." followed by "OK" in
        /// the file arrives here as a single "Loading Steam API...OK", and three lines of
        /// ".", "." and " 23.1MB (23%)" arrive as ".. 23.1MB (23%)".
        ///
        /// This is not confined to the login. The pipe hands over its whole withheld
        /// block when steamcmd finally unblocks — which on a successful upload is at the
        /// very end — so the joined form can turn up long after the run has settled.
        /// </summary>
        public bool ShouldSuppressPipeLine(string normalised)
        {
            lock (_gate)
            {
                var from = Math.Max(0, _fromLog.Count - Window);

                // Newest first: the pipe's copy of a line is normally the most recent
                // thing the log produced.
                for (var i = _fromLog.Count - 1; i >= from; i--)
                {
                    if (!string.Equals(_fromLog[i], normalised, StringComparison.Ordinal)) continue;
                    _fromLog[i] = null;
                    return true;
                }

                // Oldest first, so a joined line consumes the earliest run of parts that
                // fits it rather than a later coincidence.
                for (var i = from; i < _fromLog.Count; i++)
                {
                    if (_fromLog[i] is not { Length: > 0 } head) continue;
                    if (!normalised.StartsWith(head, StringComparison.Ordinal)) continue;

                    var matched = head.Length;
                    var last = i;

                    for (var j = i + 1; j < _fromLog.Count && matched < normalised.Length; j++)
                    {
                        if (_fromLog[j] is not { Length: > 0 } next) continue;
                        if (!normalised.AsSpan(matched).StartsWith(next, StringComparison.Ordinal)) break;
                        matched += next.Length;
                        last = j;
                    }

                    if (matched == normalised.Length)
                    {
                        for (var k = i; k <= last; k++) _fromLog[k] = null;
                        return true;
                    }

                    // Matched as far as the log has got, and the log has not got any
                    // further yet. This is the real shape at shutdown: the log writes
                    // "Unloading Steam API...", the pipe flushes the finished
                    // "Unloading Steam API...OK" as the process exits, and the log's "OK"
                    // lands a poll later. Drop the joined line — its first half is already
                    // on screen and its second half is about to be — rather than showing
                    // the message a second time in one piece.
                    if (last == _fromLog.Count - 1)
                    {
                        for (var k = i; k <= last; k++) _fromLog[k] = null;
                        return true;
                    }
                }

                // Not a duplicate. Remember it, because the log may yet produce the same
                // message as several lines — at shutdown the pipe wins the race outright.
                _fromPipe.Add(new PartiallyMatched(normalised));
                if (_fromPipe.Count > Capacity) _fromPipe.RemoveRange(0, _fromPipe.Count - Capacity);
                return false;
            }
        }

        /// <summary>
        /// True when this console-log line is a piece of something the pipe already
        /// showed.
        ///
        /// The mirror of the case above, and it is not hypothetical: at shutdown the pipe
        /// flushes "Unloading Steam API...OK" as the process exits, while the log tail is
        /// still up to one poll behind and delivers "Unloading Steam API..." and "OK"
        /// afterwards. Without this the last line of every successful upload prints twice.
        /// </summary>
        public bool ShouldSuppressLogLine(string normalised)
        {
            lock (_gate)
            {
                // Oldest first: pieces arrive in order, so the earliest unfinished entry
                // is the one they belong to.
                for (var i = 0; i < _fromPipe.Count; i++)
                {
                    var pending = _fromPipe[i];
                    if (!pending.Remaining.StartsWith(normalised, StringComparison.Ordinal)) continue;

                    pending.Remaining = pending.Remaining[normalised.Length..];
                    if (pending.Remaining.Length == 0) _fromPipe.RemoveAt(i);
                    return true;
                }

                RecordLogLineCore(normalised);
                return false;
            }
        }

        /// <summary>Remembers a log line without ever suppressing it.</summary>
        public void RecordLogLine(string normalised)
        {
            lock (_gate) RecordLogLineCore(normalised);
        }

        private void RecordLogLineCore(string normalised)
        {
            _fromLog.Add(normalised);
            if (_fromLog.Count > Capacity * 2) Compact();
        }

        private void Compact()
        {
            _fromLog.RemoveAll(entry => entry is null);
            if (_fromLog.Count > Capacity) _fromLog.RemoveRange(0, _fromLog.Count - Capacity);
        }
    }

    /// <summary>
    /// How long steamcmd may say nothing before it is assumed to be blocked on a prompt
    /// nobody can see. Measured against reality: from the version banner it reaches the
    /// password prompt in about two seconds, and the slowest legitimate gap before login
    /// — the bootstrap update check — runs about five. Twenty leaves generous room and
    /// still turns a permanent hang into a dialog while the user is still watching.
    /// </summary>
    private const int SilenceBeforePromptMs = 20_000;

    /// <summary>
    /// Opens the prompt when steamcmd goes quiet before logging in.
    ///
    /// This exists because the obvious approach — watch stdout for "password:" and answer
    /// it — cannot work on Windows. steamcmd's stdout is block buffered as soon as it is
    /// a pipe rather than a console, so the prompt sits unflushed in its buffer: measured
    /// on a real run, the banner arrives immediately and then nothing at all, and the
    /// whole withheld block — "Loading Steam API...OK", "Cached credentials not found.",
    /// "password:" — only appears once something is written to stdin. The program waits
    /// for text that the text waits for the program to unblock.
    ///
    /// Silence is therefore the only usable signal. stdin, unlike stdout, is connected
    /// and read normally, so an answer sent into the quiet does arrive.
    /// </summary>
    private async Task WatchForSilentPromptAsync(Process process, RunState state,
                                                 CancellationToken cancellation)
    {
        if (_prompt is null) return;

        while (!process.HasExited && !state.LoginSettled)
        {
            try
            {
                await Task.Delay(1000, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (process.HasExited || state.LoginSettled) return;

            var silentFor = Environment.TickCount64 - Interlocked.Read(ref state.LastOutputTicks);
            if (silentFor < SilenceBeforePromptMs) continue;

            await AnswerSilentPromptAsync(process, state, cancellation).ConfigureAwait(false);
        }
    }

    private async Task AnswerSilentPromptAsync(Process process, RunState state,
                                               CancellationToken cancellation)
    {
        // Same gate the text-driven path uses, so the two can never open two dialogs for
        // one question.
        if (Interlocked.CompareExchange(ref state.PromptGate, 1, 0) != 0) return;

        try
        {
            // Say so in the log. Without this the panel simply stops, which is the exact
            // symptom this whole mechanism exists to explain.
            Output?.Invoke(new SteamCmdEvent(SteamCmdEventKind.Bootstrap,
                $"steamcmd has said nothing for {SilenceBeforePromptMs / 1000}s — it is waiting " +
                "for credentials it did not manage to print. Answering it."));

            // steamcmd asks for the password first and the Steam Guard code after it, so
            // the first silence is the password and any later one is the code.
            var answer = Interlocked.Exchange(ref state.PasswordAnswered, 1) == 0
                ? await _prompt!.RequestPasswordAsync(string.Empty, cancellation).ConfigureAwait(false)
                : await _prompt!.RequestSteamGuardCodeAsync(
                        "steamcmd is waiting for input and is not saying what for. If it already " +
                        "has the password, this is the Steam Guard code.", cancellation)
                    .ConfigureAwait(false);

            if (answer is null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { }
                return;
            }

            await process.StandardInput.WriteLineAsync(answer).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);

            // Restart the clock: steamcmd now has something to do, and the reply that
            // follows is what tells us whether it worked.
            Interlocked.Exchange(ref state.LastOutputTicks, Environment.TickCount64);
        }
        finally
        {
            Interlocked.Exchange(ref state.PromptGate, 0);
        }
    }

    private async Task PumpAsync(StreamReader reader, Process process, RunState state,
                                 CancellationToken cancellation)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; } // process died mid-read

            if (read == 0) break;

            // Every byte counts as a sign of life, including bytes inside a line that is
            // not finished yet — the watchdog is measuring whether steamcmd is doing
            // anything at all, not whether it produced a whole line.
            Interlocked.Exchange(ref state.LastOutputTicks, Environment.TickCount64);

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];

                if (c is '\n' or '\r')
                {
                    // Collapse CRLF into a single line break.
                    if (c == '\r' && i + 1 < read && buffer[i + 1] == '\n') i++;

                    await EmitAsync(line.ToString(), process, state, cancellation).ConfigureAwait(false);
                    line.Clear();
                    continue;
                }

                line.Append(c);
            }

            // A prompt such as "Steam Guard code:" arrives with no line terminator at
            // all, so an unterminated tail has to be inspected as well or the run
            // deadlocks waiting for input nobody knows is needed.
            if (line.Length > 0)
                await MaybeAnswerPromptAsync(line.ToString(), process, state, cancellation)
                    .ConfigureAwait(false);
        }

        if (line.Length > 0)
            await EmitAsync(line.ToString(), process, state, cancellation).ConfigureAwait(false);
    }

    private async Task EmitAsync(string line, Process process, RunState state,
                                 CancellationToken cancellation, bool fromConsoleLog = false)
    {
        var normalised = SteamCmdOutputParser.StripLogTimestamp(line);

        // The console log stamps blank lines too. Shown verbatim they are bare timestamps
        // down the panel, which reads as the tool malfunctioning.
        if (fromConsoleLog && normalised.Length == 0) return;

        var evt = SteamCmdOutputParser.Parse(line);

        if (normalised.Length > 0)
        {
            var isPrompt = evt.Kind is SteamCmdEventKind.LoginPrompt
                                    or SteamCmdEventKind.SteamGuardPrompt;

            if (fromConsoleLog)
            {
                // A prompt read from the log is never dropped — it is the one line that
                // has to reach the handler, and a wrong match here would hang the run
                // rather than print something twice. It is still recorded, so the pipe's
                // late copy of it can be dropped.
                if (isPrompt) state.Duplicates.RecordLogLine(normalised);
                else if (state.Duplicates.ShouldSuppressLogLine(normalised)) return;
            }
            else if (state.Duplicates.ShouldSuppressPipeLine(normalised))
            {
                // The console log already showed this, in most cases many seconds ago.
                // Everything it implies — success flags, prompts — was acted on then.
                return;
            }
        }

        switch (evt.Kind)
        {
            case SteamCmdEventKind.BuildSucceeded:
                state.SawSuccess = true;
                state.BuildId = evt.BuildId ?? state.BuildId;
                break;
            // "Success! App 'x' fully installed." is to a download what the
            // build-finished line is to an upload: the one signal worth trusting.
            case SteamCmdEventKind.DownloadSucceeded:
                state.SawSuccess = true;
                break;
            case SteamCmdEventKind.BuildFailed:
            case SteamCmdEventKind.LoginFailed:
            case SteamCmdEventKind.DownloadFailed:
                state.FailureDetail ??= evt.Detail ?? line.Trim();
                break;
            // The console log splits a verdict off its own announcement — "Logging in
            // user 'x' to Steam Public..." on one line and "ERROR (Invalid Password)" on
            // the next — so the standalone error line is the only place the reason
            // survives. Without this the run reports a bare exit code.
            case SteamCmdEventKind.Error:
                state.FailureDetail ??= evt.Detail ?? normalised;
                break;
            // Anything past the login means steamcmd is working rather than waiting, and
            // the silence watchdog has to stand down for the rest of the run: a depot
            // commit can go minutes without a word.
            case SteamCmdEventKind.LoginSucceeded:
            case SteamCmdEventKind.BuildStarted:
            case SteamCmdEventKind.DepotScanning:
            case SteamCmdEventKind.DepotUploading:
            case SteamCmdEventKind.Downloading:
                state.LoginSettled = true;
                break;
            case SteamCmdEventKind.Progress when evt.BuildId is not null:
                state.BuildId ??= evt.BuildId;
                break;
        }

        Output?.Invoke(evt);

        // Any other output means the next identical prompt is a new question.
        if (evt.Kind is not (SteamCmdEventKind.SteamGuardPrompt or SteamCmdEventKind.LoginPrompt))
            state.AnsweredPrompt = null;

        await MaybeAnswerPromptAsync(line, process, state, cancellation).ConfigureAwait(false);
    }

    private async Task MaybeAnswerPromptAsync(string line, Process process, RunState state,
                                              CancellationToken cancellation)
    {
        if (_prompt is null) return;

        var evt = SteamCmdOutputParser.Parse(line);
        if (evt.Kind is not (SteamCmdEventKind.SteamGuardPrompt or SteamCmdEventKind.LoginPrompt)) return;

        // A password prompt that arrives as text after the watchdog already answered one
        // is the same question echoed late: the buffered block that finally flushes when
        // steamcmd unblocks contains the "password:" it printed before we replied.
        // Asking again here would put a second dialog in front of a login already
        // in progress.
        if (evt.Kind == SteamCmdEventKind.LoginPrompt &&
            Interlocked.Exchange(ref state.PasswordAnswered, 1) != 0) return;

        var normalised = line.Trim();
        if (string.Equals(state.AnsweredPrompt, normalised, StringComparison.Ordinal)) return;

        if (Interlocked.CompareExchange(ref state.PromptGate, 1, 0) != 0) return;

        try
        {
            state.AnsweredPrompt = normalised;

            var answer = evt.Kind == SteamCmdEventKind.SteamGuardPrompt
                ? await _prompt.RequestSteamGuardCodeAsync(evt.Detail ?? line.Trim(), cancellation)
                                 .ConfigureAwait(false)
                : await _prompt.RequestPasswordAsync(string.Empty, cancellation).ConfigureAwait(false);

            if (answer is null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { }
                return;
            }

            await process.StandardInput.WriteLineAsync(answer).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref state.PromptGate, 0);
        }
    }
}
