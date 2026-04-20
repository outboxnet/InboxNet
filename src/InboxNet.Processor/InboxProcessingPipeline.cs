using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using InboxNet.Dispatch;
using InboxNet.Interfaces;
using InboxNet.Models;
using InboxNet.Observability;
using InboxNet.Options;

namespace InboxNet.Processor;

/// <summary>
/// Inbox dispatch pipeline. Locks messages, resolves matching handlers, runs them sequentially
/// in registration order, records per-handler attempts, and decides the message's final
/// disposition. Handlers are sequential (unlike the outbox's parallel subscription delivery)
/// because inbox handlers often share a logical entity and ordering matters.
/// </summary>
public sealed class InboxProcessingPipeline : IInboxProcessor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInboxHandlerRegistry _handlerRegistry;
    private readonly IInboxRetryPolicy _retryPolicy;
    private readonly InboxOptions _options;
    private readonly ILogger<InboxProcessingPipeline> _logger;

    // ReleaseExpiredLocksAsync is a table-wide UPDATE; throttle so a saturated hot path doesn't
    // trigger it on every batch.
    private static readonly TimeSpan ReleaseExpiredLocksInterval = TimeSpan.FromSeconds(30);
    private long _lastLockReleaseTicks = DateTimeOffset.MinValue.UtcTicks;

    public InboxProcessingPipeline(
        IServiceScopeFactory scopeFactory,
        IInboxHandlerRegistry handlerRegistry,
        IInboxRetryPolicy retryPolicy,
        IOptions<InboxOptions> options,
        ILogger<InboxProcessingPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _handlerRegistry = handlerRegistry;
        _retryPolicy = retryPolicy;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct = default, IReadOnlySet<Guid>? skipIds = null)
    {
        using var activity = InboxActivitySource.Source.StartActivity("inbox.process_batch");
        var batchStopwatch = Stopwatch.StartNew();
        var lockedBy = _options.InstanceId;

        try
        {
            using var batchScope = _scopeFactory.CreateScope();
            var sp = batchScope.ServiceProvider;
            var store = sp.GetRequiredService<IInboxStore>();

            var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            var lastTicks = Interlocked.Read(ref _lastLockReleaseTicks);
            if (nowTicks - lastTicks >= ReleaseExpiredLocksInterval.Ticks
                && Interlocked.CompareExchange(ref _lastLockReleaseTicks, nowTicks, lastTicks) == lastTicks)
            {
                await store.ReleaseExpiredLocksAsync(ct);
            }

            var messages = await store.LockNextBatchAsync(
                _options.BatchSize,
                _options.DefaultVisibilityTimeout,
                lockedBy,
                skipIds,
                ct);

            if (messages.Count == 0)
                return 0;

            InboxMetrics.BatchesProcessed.Add(1);
            InboxMetrics.BatchSize.Record(messages.Count);
            activity?.SetTag("inbox.batch_size", messages.Count);

            _logger.LogInformation("Processing batch of {Count} inbox messages", messages.Count);

            await Parallel.ForEachAsync(
                messages,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _options.MaxConcurrentDispatch,
                    CancellationToken = ct
                },
                async (message, token) =>
                {
                    using var messageScope = _scopeFactory.CreateScope();
                    var msp = messageScope.ServiceProvider;

                    await DispatchMessageAsync(
                        message,
                        lockedBy,
                        msp,
                        token);
                });

            return messages.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error processing inbox batch");
            throw;
        }
        finally
        {
            batchStopwatch.Stop();
            InboxMetrics.ProcessingDuration.Record(batchStopwatch.Elapsed.TotalMilliseconds);
        }
    }

    public async Task<bool> TryProcessByIdAsync(Guid messageId, CancellationToken ct = default)
    {
        var lockedBy = _options.InstanceId;

        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IInboxStore>();

        var message = await store.TryLockByIdAsync(
            messageId, _options.DefaultVisibilityTimeout, lockedBy, ct);

        if (message is null)
            return false;

        await DispatchMessageAsync(message, lockedBy, sp, ct);
        return true;
    }

    // ── Core dispatch ─────────────────────────────────────────────────────────

    private async Task DispatchMessageAsync(
        InboxMessage message,
        string lockedBy,
        IServiceProvider sp,
        CancellationToken ct)
    {
        using var activity = InboxActivitySource.Source.StartActivity("inbox.dispatch");
        activity?.SetTag("inbox.message_id", message.Id.ToString());
        activity?.SetTag("inbox.provider_key", message.ProviderKey);
        activity?.SetTag("inbox.event_type", message.EventType);

        var store = sp.GetRequiredService<IInboxStore>();
        var attemptStore = sp.GetRequiredService<IInboxHandlerAttemptStore>();

        var matching = _handlerRegistry.GetMatching(message.ProviderKey, message.EventType);

        if (matching.Count == 0)
        {
            _logger.LogDebug(
                "No handlers registered for provider {ProviderKey}, event {EventType} — marking message {MessageId} as processed",
                message.ProviderKey, message.EventType, message.Id);

            if (await store.MarkAsProcessedAsync(message.Id, lockedBy, ct))
            {
                InboxMetrics.MessagesProcessed.Add(1,
                    new KeyValuePair<string, object?>("provider", message.ProviderKey),
                    new KeyValuePair<string, object?>("event_type", message.EventType));
            }
            return;
        }

        IReadOnlyDictionary<string, HandlerAttemptState> states;
        try
        {
            var handlerNames = matching.Select(r => r.HandlerName).ToList();
            states = await attemptStore.GetHandlerStatesAsync(message.Id, handlerNames, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to fetch handler states for message {MessageId}", message.Id);
            await HandleMessageFailureAsync(message, lockedBy, store, ex.Message, ct);
            return;
        }

        // ── DISPATCH PHASE ─────────────────────────────────────────────────────
        // Handlers run sequentially in registration order. If one fails we stop and let the
        // retry cycle pick up where we left off — successful handlers won't re-run because
        // their success records are persisted.

        var outcome = await RunHandlersAsync(message, matching, states, sp, ct);

        // ── BOOKKEEPING PHASE ──────────────────────────────────────────────────
        if (outcome.NewAttempts.Count > 0)
        {
            var saved = await TrySaveAttemptsAsync(attemptStore, outcome.NewAttempts, message.Id, ct);

            if (!saved && outcome.SuccessCount > 0)
            {
                // Attempts were lost. Don't increment retry — that would re-run successful handlers.
                // Leave the lock to expire; another instance will retry after the visibility timeout.
                _logger.LogCritical(
                    "CRITICAL: Could not persist handler attempt records for message {MessageId}. " +
                    "The message will be retried after lock expiry and handlers that succeeded MAY re-run. " +
                    "Inbox handlers MUST be idempotent on InboxMessage.Id.",
                    message.Id);
                return;
            }
        }

        // ── DECISION PHASE ─────────────────────────────────────────────────────
        try
        {
            if (outcome.FailedCount == 0 && outcome.ExhaustedCount == 0)
            {
                if (await store.MarkAsProcessedAsync(message.Id, lockedBy, ct))
                {
                    InboxMetrics.MessagesProcessed.Add(1,
                        new KeyValuePair<string, object?>("provider", message.ProviderKey),
                        new KeyValuePair<string, object?>("event_type", message.EventType));
                }
                else
                {
                    _logger.LogWarning(
                        "Lock lost for message {MessageId} after successful dispatch " +
                        "(success records saved — no re-dispatch will occur)",
                        message.Id);
                }
            }
            else if (outcome.FailedCount > 0)
            {
                await HandleMessageFailureAsync(
                    message, lockedBy, store,
                    outcome.LastError ?? "One or more handlers failed", ct);
            }
            else
            {
                // All remaining handlers are exhausted — dead-letter the message so it doesn't spin.
                _logger.LogError(
                    "Message {MessageId}: {Exhausted} handler(s) exhausted retries — dead-lettering",
                    message.Id, outcome.ExhaustedCount);

                if (await store.MarkAsDeadLetteredAsync(message.Id, lockedBy, ct))
                {
                    InboxMetrics.MessagesDeadLettered.Add(1,
                        new KeyValuePair<string, object?>("provider", message.ProviderKey),
                        new KeyValuePair<string, object?>("event_type", message.EventType));
                }
                else
                {
                    _logger.LogWarning(
                        "Lock lost for message {MessageId} during dead-letter handling",
                        message.Id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error updating message status for {MessageId}", message.Id);
        }
    }

    // ── Sequential handler dispatch ───────────────────────────────────────────

    private sealed record DispatchOutcome(
        int SuccessCount,
        int FailedCount,
        int ExhaustedCount,
        string? LastError,
        List<InboxHandlerAttempt> NewAttempts);

    private async Task<DispatchOutcome> RunHandlersAsync(
        InboxMessage message,
        IReadOnlyList<InboxHandlerRegistration> handlers,
        IReadOnlyDictionary<string, HandlerAttemptState> states,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var successCount = 0;
        var failedCount = 0;
        var exhaustedCount = 0;
        string? lastError = null;
        var newAttempts = new List<InboxHandlerAttempt>(handlers.Count);

        foreach (var reg in handlers)
        {
            states.TryGetValue(reg.HandlerName, out var state);
            var attemptCount = state?.AttemptCount ?? 0;
            var hasSuccess = state?.HasSuccess ?? false;

            if (hasSuccess)
            {
                successCount++;
                continue;
            }

            if (attemptCount >= reg.MaxRetries)
            {
                exhaustedCount++;
                _logger.LogWarning(
                    "Handler {HandlerName} exhausted {Max} attempts for message {MessageId}",
                    reg.HandlerName, reg.MaxRetries, message.Id);
                continue;
            }

            var attemptNumber = attemptCount + 1;
            var stopwatch = Stopwatch.StartNew();
            InboxHandlerStatus status;
            string? errorMessage = null;

            try
            {
                var handler = (IInboxHandler)sp.GetRequiredService(reg.HandlerType);
                await handler.HandleAsync(message, ct);
                stopwatch.Stop();
                status = InboxHandlerStatus.Success;
                successCount++;

                InboxMetrics.HandlerSuccesses.Add(1,
                    new KeyValuePair<string, object?>("handler", reg.HandlerName),
                    new KeyValuePair<string, object?>("provider", message.ProviderKey),
                    new KeyValuePair<string, object?>("event_type", message.EventType));
            }
            catch (OperationCanceledException)
            {
                // Propagate cancellation — no attempt record, no retry scheduling.
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                errorMessage = ex.Message;
                failedCount++;
                lastError = ex.Message;

                // Reached the per-handler limit with this failure → will be treated as exhausted
                // on the next dispatch (attemptCount == MaxRetries at that point).
                status = attemptNumber >= reg.MaxRetries
                    ? InboxHandlerStatus.DeadLettered
                    : InboxHandlerStatus.Failed;

                InboxMetrics.HandlerFailures.Add(1,
                    new KeyValuePair<string, object?>("handler", reg.HandlerName),
                    new KeyValuePair<string, object?>("provider", message.ProviderKey),
                    new KeyValuePair<string, object?>("event_type", message.EventType));

                _logger.LogError(ex,
                    "Handler {HandlerName} failed for message {MessageId} (attempt {Attempt}/{Max})",
                    reg.HandlerName, message.Id, attemptNumber, reg.MaxRetries);
            }

            InboxMetrics.HandlerAttempts.Add(1,
                new KeyValuePair<string, object?>("handler", reg.HandlerName),
                new KeyValuePair<string, object?>("provider", message.ProviderKey),
                new KeyValuePair<string, object?>("event_type", message.EventType));
            InboxMetrics.HandlerDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("handler", reg.HandlerName));

            newAttempts.Add(new InboxHandlerAttempt
            {
                Id = Guid.NewGuid(),
                InboxMessageId = message.Id,
                HandlerName = reg.HandlerName,
                AttemptNumber = attemptNumber,
                Status = status,
                DurationMs = (long)stopwatch.Elapsed.TotalMilliseconds,
                ErrorMessage = errorMessage,
                AttemptedAt = DateTimeOffset.UtcNow
            });

            // Sequential dispatch — stop on first failure so downstream handlers observe
            // a consistent entity state on the next retry.
            if (status != InboxHandlerStatus.Success)
                break;
        }

        return new DispatchOutcome(successCount, failedCount, exhaustedCount, lastError, newAttempts);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> TrySaveAttemptsAsync(
        IInboxHandlerAttemptStore store,
        List<InboxHandlerAttempt> attempts,
        Guid messageId,
        CancellationToken ct)
    {
        if (attempts.Count == 0) return true;

        try
        {
            await store.SaveAttemptsAsync(attempts, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Failed to save {Count} handler attempt(s) for message {MessageId}",
                attempts.Count, messageId);
            return false;
        }
    }

    private async Task HandleMessageFailureAsync(
        InboxMessage message,
        string lockedBy,
        IInboxStore store,
        string error,
        CancellationToken ct)
    {
        var nextDelay = _retryPolicy.GetNextDelay(message.RetryCount);

        if (nextDelay.HasValue)
        {
            var nextRetryAt = DateTimeOffset.UtcNow.Add(nextDelay.Value);

            if (await store.IncrementRetryAsync(message.Id, lockedBy, nextRetryAt, error, ct))
            {
                InboxMetrics.MessagesFailed.Add(1,
                    new KeyValuePair<string, object?>("provider", message.ProviderKey),
                    new KeyValuePair<string, object?>("event_type", message.EventType));

                _logger.LogWarning(
                    "Message {MessageId} handler dispatch failed, scheduled retry at {NextRetryAt}",
                    message.Id, nextRetryAt);
            }
            else
            {
                _logger.LogWarning("Lock lost for message {MessageId} during failure handling", message.Id);
            }
        }
        else
        {
            if (await store.MarkAsDeadLetteredAsync(message.Id, lockedBy, ct))
            {
                InboxMetrics.MessagesDeadLettered.Add(1,
                    new KeyValuePair<string, object?>("provider", message.ProviderKey),
                    new KeyValuePair<string, object?>("event_type", message.EventType));

                _logger.LogError("Message {MessageId} exhausted global retries, moved to dead letter", message.Id);
            }
            else
            {
                _logger.LogWarning(
                    "Lock lost for message {MessageId} during dead-letter handling",
                    message.Id);
            }
        }
    }
}
