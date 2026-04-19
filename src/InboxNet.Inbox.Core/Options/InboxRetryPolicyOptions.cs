namespace InboxNet.Inbox.Options;

public class InboxRetryPolicyOptions
{
    public int MaxRetries { get; set; } = 5;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(5);
    public double JitterFactor { get; set; } = 0.2;
}
