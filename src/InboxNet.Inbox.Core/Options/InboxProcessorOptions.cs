namespace InboxNet.Inbox.Options;

public class InboxProcessorOptions
{
    /// <summary>
    /// Cold-path scan interval for messages that arrived on a different instance or are
    /// due for retry. The hot-path channel delivers same-instance messages immediately.
    /// Default: 1 second.
    /// </summary>
    public TimeSpan ColdPollingInterval { get; set; } = TimeSpan.FromSeconds(1);
}
