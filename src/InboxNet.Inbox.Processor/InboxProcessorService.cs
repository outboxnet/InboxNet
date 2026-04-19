using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using InboxNet.Inbox.Interfaces;
using InboxNet.Inbox.Options;

namespace InboxNet.Inbox.Processor;

/// <summary>
/// Inbox dispatcher hosted service. Runs a hot path (drains the in-process signal channel for
/// sub-millisecond same-instance dispatch) and a cold path (periodic batch scan for
/// cross-instance messages, retries, and overflow recovery). Same architecture as the outbox
/// processor; the DB lock gate guarantees at-most-once dispatch across replicas.
/// </summary>
public sealed class InboxProcessorService : BackgroundService
{
    private readonly IInboxProcessor _processor;
    private readonly IInboxSignal _signal;
    private readonly InboxProcessorOptions _processorOptions;
    private readonly InboxOptions _inboxOptions;
    private readonly ILogger<InboxProcessorService> _logger;

    // IDs currently being dispatched by the hot path. The cold path skips these so fresh
    // same-instance messages are handled exclusively by the hot path, and the cold path
    // focuses on cross-instance messages, retries, and channel-overflow recovery.
    private readonly ConcurrentDictionary<Guid, byte> _hotInFlight = new();

    public InboxProcessorService(
        IInboxProcessor processor,
        IInboxSignal signal,
        IOptions<InboxProcessorOptions> processorOptions,
        IOptions<InboxOptions> inboxOptions,
        ILogger<InboxProcessorService> logger)
    {
        _processor = processor;
        _signal = signal;
        _processorOptions = processorOptions.Value;
        _inboxOptions = inboxOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Inbox dispatcher started. Cold polling interval: {Interval}ms, MaxConcurrentDispatch: {Max}",
            _processorOptions.ColdPollingInterval.TotalMilliseconds,
            _inboxOptions.MaxConcurrentDispatch);

        await Task.WhenAll(
            RunHotPathAsync(stoppingToken),
            RunColdPathAsync(stoppingToken));
    }

    private async Task RunHotPathAsync(CancellationToken ct)
    {
        try
        {
            await Parallel.ForEachAsync(
                _signal.Reader.ReadAllAsync(ct),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _inboxOptions.MaxConcurrentDispatch,
                    CancellationToken = ct
                },
                async (messageId, token) =>
                {
                    _hotInFlight.TryAdd(messageId, 0);
                    try
                    {
                        await _processor.TryProcessByIdAsync(messageId, token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Hot-path inbox dispatch failed for message {MessageId}", messageId);
                    }
                    finally
                    {
                        _hotInFlight.TryRemove(messageId, out _);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunColdPathAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var skipIds = _hotInFlight.IsEmpty
                    ? null
                    : new HashSet<Guid>(_hotInFlight.Keys);
                await _processor.ProcessBatchAsync(ct, skipIds);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cold-path inbox dispatch batch failed");
            }

            try
            {
                await Task.Delay(_processorOptions.ColdPollingInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
