using InboxNet.Models;

namespace InboxNet.Interfaces;

public interface IInboxStore
{
    /// <summary>
    /// Inserts <paramref name="message"/>. Returns the ID of the persisted row; when a
    /// duplicate is detected on <c>(ProviderKey, DedupKey)</c> the implementation returns the
    /// existing row's ID and sets <paramref name="isDuplicate"/> to <c>true</c>.
    /// </summary>
    Task<(Guid Id, bool IsDuplicate)> InsertOrGetDuplicateAsync(
        InboxMessage message,
        CancellationToken ct = default);

    /// <summary>
    /// Locks the next batch of eligible messages for dispatch.
    /// <paramref name="skipIds"/> excludes specific IDs — used by the cold path to avoid
    /// racing against the hot path for rows currently in flight on this instance.
    /// </summary>
    Task<IReadOnlyList<InboxMessage>> LockNextBatchAsync(
        int batchSize,
        TimeSpan visibilityTimeout,
        string lockedBy,
        IReadOnlySet<Guid>? skipIds = null,
        CancellationToken ct = default);

    /// <summary>
    /// PK-seek UPDATE that locks a single message by ID. Returns null if the row does not
    /// exist, is already locked, is already processed, or is scheduled for a future retry.
    /// </summary>
    Task<InboxMessage?> TryLockByIdAsync(
        Guid messageId,
        TimeSpan visibilityTimeout,
        string lockedBy,
        CancellationToken ct = default);

    Task<bool> MarkAsProcessedAsync(Guid messageId, string lockedBy, CancellationToken ct = default);

    /// <summary>
    /// Bulk variant of <see cref="MarkAsProcessedAsync"/>. Marks every supplied id as
    /// <see cref="InboxMessageStatus.Processed"/> in a single round-trip, scoped by
    /// <paramref name="lockedBy"/> so a stale lock cannot complete someone else's work.
    /// Returns the number of rows updated — may be less than <paramref name="messageIds"/>
    /// when some locks expired during dispatch.
    /// </summary>
    Task<int> MarkAsProcessedBulkAsync(
        IReadOnlyCollection<Guid> messageIds,
        string lockedBy,
        CancellationToken ct = default);

    Task<bool> IncrementRetryAsync(
        Guid messageId,
        string lockedBy,
        DateTimeOffset nextRetryAt,
        string? error = null,
        CancellationToken ct = default);

    Task<bool> MarkAsDeadLetteredAsync(Guid messageId, string lockedBy, CancellationToken ct = default);

    Task ReleaseExpiredLocksAsync(CancellationToken ct = default);

    Task<int> PurgeProcessedMessagesAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
