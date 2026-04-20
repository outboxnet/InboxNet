namespace InboxNet.Interfaces;

public interface IInboxProcessor
{
    Task<int> ProcessBatchAsync(CancellationToken ct = default, IReadOnlySet<Guid>? skipIds = null);

    Task<bool> TryProcessByIdAsync(Guid messageId, CancellationToken ct = default);
}
