using System.Threading;
using System.Threading.Tasks;
using SteamPipeStudio.Core.Steam;

namespace SteamPipeStudio.App.Services;

/// <summary>
/// The prompt behind the password Check button: answers steamcmd's password prompt once
/// with the password being checked, and counts how often it was asked.
///
/// The count is the whole point. A sign-in that never asked proves nothing about the
/// password, and a second ask is steamcmd saying the first answer was wrong — which is
/// aborted rather than shown to the user as a dialog, since the question being asked
/// was "is this password right", and the answer is no. Steam Guard codes still go to the
/// user: a real sign-in needs one whenever Steam asks.
/// </summary>
public sealed class PasswordCheckPrompt : ISteamCmdPrompt
{
    private readonly ISteamCmdPrompt _inner;
    private readonly string? _password;
    private int _asks;

    public PasswordCheckPrompt(ISteamCmdPrompt inner, string? password)
    {
        _inner = inner;
        _password = string.IsNullOrEmpty(password) ? null : password;
    }

    public int PasswordAsks => _asks;

    public bool HasPassword => _password is not null;

    public Task<string?> RequestSteamGuardCodeAsync(string message, CancellationToken cancellation) =>
        _inner.RequestSteamGuardCodeAsync(message, cancellation);

    public Task<string?> RequestPasswordAsync(string accountName, CancellationToken cancellation) =>
        Task.FromResult(Interlocked.Increment(ref _asks) == 1 ? _password : null);
}
