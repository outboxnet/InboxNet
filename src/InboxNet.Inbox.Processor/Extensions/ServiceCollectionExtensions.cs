using Microsoft.Extensions.DependencyInjection;
using InboxNet.Inbox.Extensions;
using InboxNet.Inbox.Interfaces;
using InboxNet.Inbox.Options;

namespace InboxNet.Inbox.Processor.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the inbox background dispatcher. Runs a hot-path channel drain for
    /// sub-millisecond same-instance dispatch and a periodic cold-path batch scan for
    /// cross-instance messages, retries, and channel-overflow recovery.
    /// </summary>
    public static IInboxNetBuilder AddBackgroundDispatcher(
        this IInboxNetBuilder builder,
        Action<InboxProcessorOptions>? configure = null)
    {
        var options = new InboxProcessorOptions();
        configure?.Invoke(options);

        builder.Services.Configure<InboxProcessorOptions>(o =>
        {
            o.ColdPollingInterval = options.ColdPollingInterval;
        });

        // Singleton: pipeline only consumes singletons (IServiceScopeFactory, registry, retry
        // policy, options, logger) and creates its own child scopes per batch/message.
        builder.Services.AddSingleton<IInboxProcessor, InboxProcessingPipeline>();
        builder.Services.AddHostedService<InboxProcessorService>();

        return builder;
    }
}
