using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using InboxNet.Interfaces;
using InboxNet.Models;
using InboxNet.Options;

namespace InboxNet.EntityFrameworkCore.Stores;

internal sealed class EfCoreInboxStore : IInboxStore
{
    private const int SqlUniqueConstraintViolation = 2627;
    private const int SqlUniqueIndexViolation = 2601;

    private readonly InboxDbContext _dbContext;
    private readonly InboxOptions _options;
    private readonly ILogger<EfCoreInboxStore> _logger;

    public EfCoreInboxStore(
        InboxDbContext dbContext,
        IOptions<InboxOptions> options,
        ILogger<EfCoreInboxStore> logger)
    {
        _dbContext = dbContext;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(Guid Id, bool IsDuplicate)> InsertOrGetDuplicateAsync(
        InboxMessage message,
        CancellationToken ct = default)
    {
        _dbContext.InboxMessages.Add(message);
        try
        {
            await _dbContext.SaveChangesAsync(ct);
            return (message.Id, false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Duplicate webhook — the unique index on (ProviderKey, DedupKey) rejected the row.
            // Recover the existing row's ID so the caller can respond 200 OK with a stable
            // correlation identifier. Detach the unsaved entity so subsequent SaveChanges calls
            // on this context do not re-attempt the insert.
            _dbContext.Entry(message).State = EntityState.Detached;

            var providerKey = message.ProviderKey;
            var dedupKey = message.DedupKey;

            var existingId = await _dbContext.InboxMessages
                .AsNoTracking()
                .Where(m => m.ProviderKey == providerKey && m.DedupKey == dedupKey)
                .Select(m => m.Id)
                .FirstOrDefaultAsync(ct);

            _logger.LogDebug(
                "Duplicate inbox message rejected by dedup: provider={ProviderKey}, dedupKey={DedupKey}, existingId={ExistingId}",
                providerKey, dedupKey, existingId);

            return (existingId, true);
        }
    }

    public async Task<IReadOnlyList<InboxMessage>> LockNextBatchAsync(
        int batchSize,
        TimeSpan visibilityTimeout,
        string lockedBy,
        IReadOnlySet<Guid>? skipIds = null,
        CancellationToken ct = default)
    {
        var schema = _options.SchemaName;
        var visibilityTimeoutSeconds = (int)visibilityTimeout.TotalSeconds;

        var tenantFilterClause = _options.TenantFilter is not null
            ? "AND m.[TenantId] = @tenantFilter"
            : string.Empty;

        var skipIdsClause = skipIds is { Count: > 0 }
            ? "AND m.[Id] NOT IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@skipJson))"
            : string.Empty;

        // Null-safe partition equality: same pattern as Outbox. EntityId drives ordering but
        // we also include ProviderKey so that two providers emitting events for the same
        // entity don't block each other.
        var orderingClause = _options.EnableOrderedProcessing
            ? $"""

              AND (
                m.[EntityId] IS NULL
                OR NOT EXISTS (
                    SELECT 1 FROM [{schema}].[InboxMessages] m2 WITH (READCOMMITTEDLOCK)
                    WHERE m2.[Status] = @processingStatus
                      AND m2.[LockedUntil] > SYSDATETIMEOFFSET()
                      AND (m2.[TenantId]    = m.[TenantId]    OR (m2.[TenantId]    IS NULL AND m.[TenantId]    IS NULL))
                      AND  m2.[ProviderKey] = m.[ProviderKey]
                      AND  m2.[EntityId]    = m.[EntityId]
                      AND  m2.[Id] != m.[Id]
                )
              )
              """
            : string.Empty;

        var sql = $"""
            WITH Candidates AS (
                SELECT TOP (@batchSize) m.[Id]
                FROM [{schema}].[InboxMessages] m WITH (UPDLOCK, READPAST)
                WHERE m.[Status] IN (@pendingStatus, @processingStatus)
                  AND (m.[LockedUntil] IS NULL OR m.[LockedUntil] < SYSDATETIMEOFFSET())
                  AND (m.[NextRetryAt] IS NULL OR m.[NextRetryAt] <= SYSDATETIMEOFFSET())
                  {tenantFilterClause}
                  {skipIdsClause}
                {orderingClause}
                ORDER BY m.[ReceivedAt]
            )
            UPDATE m
            SET
                m.[Status] = @processingStatus,
                m.[LockedUntil] = DATEADD(SECOND, @visibilityTimeoutSeconds, SYSDATETIMEOFFSET()),
                m.[LockedBy] = @lockedBy
            OUTPUT
                INSERTED.[Id],
                INSERTED.[ProviderKey],
                INSERTED.[EventType],
                INSERTED.[Payload],
                INSERTED.[ContentSha256],
                INSERTED.[ProviderEventId],
                INSERTED.[DedupKey],
                INSERTED.[Status],
                INSERTED.[RetryCount],
                INSERTED.[ReceivedAt],
                INSERTED.[ProcessedAt],
                INSERTED.[LockedUntil],
                INSERTED.[LockedBy],
                INSERTED.[NextRetryAt],
                INSERTED.[LastError],
                INSERTED.[CorrelationId],
                INSERTED.[TraceId],
                INSERTED.[Headers],
                INSERTED.[TenantId],
                INSERTED.[EntityId]
            FROM [{schema}].[InboxMessages] m
            INNER JOIN Candidates c ON c.[Id] = m.[Id]
            """;

        var sqlParams = new List<SqlParameter>
        {
            new SqlParameter("@batchSize",                SqlDbType.Int)           { Value = batchSize },
            new SqlParameter("@processingStatus",         SqlDbType.Int)           { Value = (int)InboxMessageStatus.Processing },
            new SqlParameter("@pendingStatus",            SqlDbType.Int)           { Value = (int)InboxMessageStatus.Pending },
            new SqlParameter("@visibilityTimeoutSeconds", SqlDbType.Int)           { Value = visibilityTimeoutSeconds },
            new SqlParameter("@lockedBy",                 SqlDbType.NVarChar, 256) { Value = lockedBy },
        };

        if (_options.TenantFilter is not null)
            sqlParams.Add(new SqlParameter("@tenantFilter", SqlDbType.NVarChar, 256) { Value = _options.TenantFilter });

        if (skipIds is { Count: > 0 })
            sqlParams.Add(new SqlParameter("@skipJson", SqlDbType.NVarChar, -1)
                { Value = System.Text.Json.JsonSerializer.Serialize(skipIds) });

        var messages = await _dbContext.InboxMessages
            .FromSqlRaw(sql, sqlParams.ToArray<object>())
            .AsNoTracking()
            .ToListAsync(ct);

        _logger.LogDebug(
            "Locked {Count} inbox messages for dispatch by {LockedBy}",
            messages.Count, lockedBy);

        return messages;
    }

    public async Task<InboxMessage?> TryLockByIdAsync(
        Guid messageId,
        TimeSpan visibilityTimeout,
        string lockedBy,
        CancellationToken ct = default)
    {
        var schema = _options.SchemaName;
        var timeoutSeconds = (int)visibilityTimeout.TotalSeconds;

        var sql = $"""
            UPDATE [{schema}].[InboxMessages]
            SET [Status]      = @processingStatus,
                [LockedUntil] = DATEADD(SECOND, @timeoutSeconds, SYSDATETIMEOFFSET()),
                [LockedBy]    = @lockedBy
            OUTPUT
                INSERTED.[Id], INSERTED.[ProviderKey], INSERTED.[EventType], INSERTED.[Payload],
                INSERTED.[ContentSha256], INSERTED.[ProviderEventId], INSERTED.[DedupKey],
                INSERTED.[Status], INSERTED.[RetryCount], INSERTED.[ReceivedAt],
                INSERTED.[ProcessedAt], INSERTED.[LockedUntil], INSERTED.[LockedBy],
                INSERTED.[NextRetryAt], INSERTED.[LastError],
                INSERTED.[CorrelationId], INSERTED.[TraceId], INSERTED.[Headers],
                INSERTED.[TenantId], INSERTED.[EntityId]
            WHERE [Id]     = @id
              AND [Status] = @pendingStatus
              AND ([LockedUntil] IS NULL OR [LockedUntil] < SYSDATETIMEOFFSET())
              AND ([NextRetryAt] IS NULL OR [NextRetryAt] <= SYSDATETIMEOFFSET())
            """;

        var results = await _dbContext.InboxMessages
            .FromSqlRaw(sql,
                new SqlParameter("@id",               SqlDbType.UniqueIdentifier) { Value = messageId },
                new SqlParameter("@processingStatus", SqlDbType.Int)              { Value = (int)InboxMessageStatus.Processing },
                new SqlParameter("@pendingStatus",    SqlDbType.Int)              { Value = (int)InboxMessageStatus.Pending },
                new SqlParameter("@timeoutSeconds",   SqlDbType.Int)              { Value = timeoutSeconds },
                new SqlParameter("@lockedBy",         SqlDbType.NVarChar, 256)    { Value = lockedBy })
            .AsNoTracking()
            .ToListAsync(ct);

        return results.Count > 0 ? results[0] : null;
    }

    public async Task<bool> MarkAsProcessedAsync(Guid messageId, string lockedBy, CancellationToken ct = default)
    {
        var affected = await _dbContext.InboxMessages
            .Where(m => m.Id == messageId && m.LockedBy == lockedBy)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, InboxMessageStatus.Processed)
                .SetProperty(m => m.ProcessedAt, DateTimeOffset.UtcNow)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LockedBy, (string?)null), ct);

        return affected > 0;
    }

    public async Task<int> MarkAsProcessedBulkAsync(
        IReadOnlyCollection<Guid> messageIds,
        string lockedBy,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0) return 0;

        // OPENJSON keeps the plan stable regardless of batch size — a Contains() translation
        // would emit IN (@p0, @p1, ...) and cache one plan per distinct id-count.
        var schema = _options.SchemaName;
        var idsJson = System.Text.Json.JsonSerializer.Serialize(messageIds);

        var sql = $"""
            UPDATE m
            SET m.[Status]      = @processedStatus,
                m.[ProcessedAt] = SYSDATETIMEOFFSET(),
                m.[LockedUntil] = NULL,
                m.[LockedBy]    = NULL
            FROM [{schema}].[InboxMessages] m
            INNER JOIN OPENJSON(@ids) WITH ([value] UNIQUEIDENTIFIER '$') AS j
                    ON m.[Id] = j.[value]
            WHERE m.[LockedBy] = @lockedBy
            """;

        return await _dbContext.Database.ExecuteSqlRawAsync(sql,
            new[]
            {
                new SqlParameter("@processedStatus", SqlDbType.Int)              { Value = (int)InboxMessageStatus.Processed },
                new SqlParameter("@lockedBy",        SqlDbType.NVarChar, 256)    { Value = lockedBy },
                new SqlParameter("@ids",             SqlDbType.NVarChar, -1)     { Value = idsJson }
            }, ct);
    }

    public async Task<bool> IncrementRetryAsync(
        Guid messageId,
        string lockedBy,
        DateTimeOffset nextRetryAt,
        string? error = null,
        CancellationToken ct = default)
    {
        var affected = await _dbContext.InboxMessages
            .Where(m => m.Id == messageId && m.LockedBy == lockedBy)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, InboxMessageStatus.Pending)
                .SetProperty(m => m.RetryCount, m => m.RetryCount + 1)
                .SetProperty(m => m.NextRetryAt, nextRetryAt)
                .SetProperty(m => m.LastError, error)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LockedBy, (string?)null), ct);

        return affected > 0;
    }

    public async Task<bool> MarkAsDeadLetteredAsync(Guid messageId, string lockedBy, CancellationToken ct = default)
    {
        var affected = await _dbContext.InboxMessages
            .Where(m => m.Id == messageId && m.LockedBy == lockedBy)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, InboxMessageStatus.DeadLettered)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LockedBy, (string?)null), ct);

        return affected > 0;
    }

    public async Task ReleaseExpiredLocksAsync(CancellationToken ct = default)
    {
        // Do NOT increment RetryCount here. An expired lock means the dispatcher crashed —
        // infrastructure failure, not a handler failure. Counting it against the message's
        // retry budget would dead-letter healthy messages under transient pod restarts.
        var released = await _dbContext.InboxMessages
            .Where(m => m.Status == InboxMessageStatus.Processing
                     && m.LockedUntil != null
                     && m.LockedUntil < DateTimeOffset.UtcNow)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, InboxMessageStatus.Pending)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LockedBy, (string?)null), ct);

        if (released > 0)
            _logger.LogWarning("Released {Count} expired inbox message locks", released);
    }

    public async Task<int> PurgeProcessedMessagesAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        var deleted = await _dbContext.InboxMessages
            .Where(m => (m.Status == InboxMessageStatus.Processed || m.Status == InboxMessageStatus.DeadLettered)
                     && m.ReceivedAt < olderThan)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
            _logger.LogInformation("Purged {Count} processed/dead-lettered inbox messages older than {OlderThan}", deleted, olderThan);

        return deleted;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException sql &&
        (sql.Number == SqlUniqueConstraintViolation || sql.Number == SqlUniqueIndexViolation);
}
