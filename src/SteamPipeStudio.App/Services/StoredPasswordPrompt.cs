using System;
using System.Threading;
using System.Threading.Tasks;
using SteamPipeStudio.Core.Security;
using SteamPipeStudio.Core.Steam;

namespace SteamPipeStudio.App.Services;

/// <summary>
/// Answers steamcmd's password prompt from the secret store, and falls back to asking
/// the user when the account has nothing saved.
///
/// This sits in front of the UI prompt rather than inside the runner because the runner
/// is a generic process pump: it sees the word "password:" on a stream and has no idea
/// which account is being logged in. The account comes from the profile, so the lookup
/// belongs here.
///
/// A saved password is offered <em>once</em> per run. steamcmd re-prompts after a
/// rejected login with byte-identical text, so feeding the same stored value again would
/// loop forever against a password that has been changed on the account; the second ask
/// goes to the user, who can then correct it.
/// </summary>
public sealed class StoredPasswordPrompt : ISteamCmdPrompt
{
    private readonly ISteamCmdPrompt _inner;
    private readonly ISecretStore _secrets;
    private readonly string _accountName;
    private int _storedOffered;
    private bool _storedUsed;

    public StoredPasswordPrompt(ISteamCmdPrompt inner, ISecretStore secrets, string accountName)
    {
        _inner = inner;
        _secrets = secrets;
        _accountName = accountName;
    }

    /// <summary>Raised when the saved password was rejected and the user had to be asked.</summary>
    public event Action? StoredPasswordRejected;

    public Task<string?> RequestSteamGuardCodeAsync(string message, CancellationToken cancellation) =>
        _inner.RequestSteamGuardCodeAsync(message, cancellation);

    public Task<string?> RequestPasswordAsync(string accountName, CancellationToken cancellation)
    {
        // The prompt carries no account name — steamcmd just prints "password:" — so the
        // account the upload is running as is the one to look up.
        if (string.IsNullOrWhiteSpace(_accountName))
            return _inner.RequestPasswordAsync(accountName, cancellation);

        if (Interlocked.Exchange(ref _storedOffered, 1) == 0)
        {
            var stored = _secrets.Read(SecretStoreFactory.SteamPassword(_accountName));
            if (!string.IsNullOrEmpty(stored))
            {
                _storedUsed = true;
                return Task.FromResult<string?>(stored);
            }
        }
        else if (_storedUsed)
        {
            // Only a second prompt after the saved password went out means it was wrong;
            // a second prompt after the user typed one is just the user mistyping.
            StoredPasswordRejected?.Invoke();
        }

        return _inner.RequestPasswordAsync(_accountName, cancellation);
    }
}
