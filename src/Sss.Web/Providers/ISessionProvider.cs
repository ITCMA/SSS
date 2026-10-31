using Sss.Web.Models;

namespace Sss.Web.Providers;

/// <summary>Thrown when the backend (Citrix) cannot be reached or answers with an error.
/// The message is for logs only and is never shown to users.</summary>
public sealed class ProviderUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public interface ISessionProvider
{
    /// <summary>Returns ONLY sessions owned by <paramref name="user"/>: all live ones plus ended ones
    /// within <paramref name="historyWindow"/>. A zero window means "live sessions only" and must be cheap.</summary>
    Task<SessionQueryResult> GetSessionsAsync(UserIdentity user, TimeSpan historyWindow, CancellationToken ct);

    /// <summary>Logs off the given sessions (already verified as owned by the user) and
    /// returns the uids for which the log-off was accepted.</summary>
    Task<IReadOnlyList<string>> LogoffAsync(UserIdentity user, IReadOnlyCollection<string> uids, CancellationToken ct);
}
