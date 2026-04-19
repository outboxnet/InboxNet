namespace InboxNet.Inbox.Options;

public class InboxOptions
{
    /// <summary>Database schema for inbox tables. Default: "inbox".</summary>
    public string SchemaName { get; set; } = "inbox";

    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// How long a locked inbox message is invisible to other dispatcher instances.
    /// Must exceed worst-case per-message dispatch time (sum of all handler timeouts × sequential).
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan DefaultVisibilityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public string InstanceId { get; set; } = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    /// <summary>How many inbox messages are dispatched concurrently within a single batch. Default: 10.</summary>
    public int MaxConcurrentDispatch { get; set; } = 10;

    /// <summary>
    /// When true (default), messages with the same <c>(TenantId, ProviderKey, EntityId)</c>
    /// partition are dispatched strictly in receive order. A message is not locked until any
    /// in-flight message for the same partition has finished. Messages without an EntityId
    /// are unaffected and dispatched concurrently.
    /// </summary>
    public bool EnableOrderedProcessing { get; set; } = true;

    /// <summary>
    /// Optional tenant sharding. When set, only messages whose <c>TenantId</c> matches are
    /// dispatched by this instance. Null (default) = all tenants.
    /// </summary>
    public string? TenantFilter { get; set; }
}
