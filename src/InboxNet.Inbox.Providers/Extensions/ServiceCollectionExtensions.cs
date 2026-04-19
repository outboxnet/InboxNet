using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using InboxNet.Inbox.Extensions;
using InboxNet.Inbox.Interfaces;
using InboxNet.Inbox.Providers.Generic;
using InboxNet.Inbox.Providers.GitHub;
using InboxNet.Inbox.Providers.Stripe;

namespace InboxNet.Inbox.Providers.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="StripeWebhookProvider"/> under the default key <c>"stripe"</c>.
    /// </summary>
    public static IInboxNetBuilder AddStripeProvider(
        this IInboxNetBuilder builder,
        Action<StripeWebhookOptions> configure)
    {
        var opts = new StripeWebhookOptions();
        configure(opts);

        builder.Services.Configure<StripeWebhookOptions>(o =>
        {
            o.Key = opts.Key;
            o.SigningSecret = opts.SigningSecret;
            o.ToleranceSeconds = opts.ToleranceSeconds;
        });

        builder.Services.AddSingleton<IWebhookProvider, StripeWebhookProvider>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="GitHubWebhookProvider"/> under the default key <c>"github"</c>.
    /// </summary>
    public static IInboxNetBuilder AddGitHubProvider(
        this IInboxNetBuilder builder,
        Action<GitHubWebhookOptions> configure)
    {
        var opts = new GitHubWebhookOptions();
        configure(opts);

        builder.Services.Configure<GitHubWebhookOptions>(o =>
        {
            o.Key = opts.Key;
            o.Secret = opts.Secret;
        });

        builder.Services.AddSingleton<IWebhookProvider, GitHubWebhookProvider>();
        return builder;
    }

    /// <summary>
    /// Registers a <see cref="GenericHmacWebhookProvider"/> instance under the key set in
    /// <paramref name="configure"/>. Call multiple times with distinct keys to support
    /// several upstream services.
    /// </summary>
    public static IInboxNetBuilder AddGenericHmacProvider(
        this IInboxNetBuilder builder,
        Action<GenericHmacWebhookOptions> configure)
    {
        var opts = new GenericHmacWebhookOptions();
        configure(opts);

        if (string.IsNullOrWhiteSpace(opts.Key))
            throw new InvalidOperationException("GenericHmacWebhookOptions.Key must be set.");

        // Instance registration so multiple keyed providers can coexist. Options are bound
        // at construction; no Options pipeline is needed.
        var provider = new GenericHmacWebhookProvider(opts);
        builder.Services.AddSingleton<IWebhookProvider>(provider);
        return builder;
    }
}
