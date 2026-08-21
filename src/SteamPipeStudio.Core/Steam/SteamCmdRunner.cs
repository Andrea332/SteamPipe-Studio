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

        var stdout = PumpAsync(process.StandardOutput, process, state, cancellation);
        var stderr = PumpAsync(process.StandardError, process, state, cancellation);

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

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
                                 CancellationToken cancellation)
    {
        var evt = SteamCmdOutputParser.Parse(line);

        switch (evt.Kind)
        {
            case SteamCmdEventKind.BuildSucceeded:
                state.SawSuccess = true;
                state.BuildId = evt.BuildId ?? state.BuildId;
                break;
            case SteamCmdEventKind.BuildFailed:
            case SteamCmdEventKind.LoginFailed:
                state.FailureDetail ??= evt.Detail ?? line.Trim();
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
