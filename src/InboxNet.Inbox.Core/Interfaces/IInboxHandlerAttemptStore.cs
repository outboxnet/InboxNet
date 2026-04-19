using InboxNet.Inbox.Models;

namespace InboxNet.Inbox.Interfaces;

/// <summary>
/// Summary of prior handler attempts for a single inbox message.
/// Retrieved in one batch query via <see cref="IInboxHandlerAttemptStore.GetHandlerStatesAsync"/>.
/// </summary>
public sealed record HandlerAttemptState(int AttemptCount, bool HasSuccess);

public interface IInboxHandlerAttemptStore
{
    Task SaveAttemptAsync(InboxHandlerAttempt attempt, CancellationToken ct = default);

    Task SaveAttemptsAsync(IReadOnlyList<InboxHandlerAttempt> attempts, CancellationToken ct = default)
    {
        if (attempts.Count == 0) return Task.CompletedTask;
        if (attempts.Count == 1) return SaveAttemptAsync(attempts[0], ct);
        return Task.WhenAll(attempts.Select(a => SaveAttemptAsync(a, ct)));
    }

    Task<IReadOnlyList<InboxHandlerAttempt>> GetByMessageIdAsync(Guid messageId, CancellationToken ct = default);

    /// <summary>
    /// Returns the attempt count and success status for every handler name in
    /// <paramref name="handlerNames"/> in a single round-trip.
    /// Handlers with no prior attempts are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<string, HandlerAttemptState>> GetHandlerStatesAsync(
        Guid messageId,
        IReadOnlyList<string> handlerNames,
        CancellationToken ct = default);

    Task<int> PurgeOldAttemptsAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
