using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using InboxNet.Interfaces;
using InboxNet.Models;
using InboxNet.Observability;

namespace InboxNet.AspNetCore;

public static class InboxEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a POST endpoint that receives webhooks for a single provider. The raw request
    /// body is buffered once, handed to the provider's <see cref="IWebhookProvider.ParseAsync"/>
    /// for validation, then persisted via <see cref="IInboxPublisher"/>.
    /// <para>
    /// Response codes:
    /// <list type="bullet">
    /// <item><description><c>202 Accepted</c> — new message accepted</description></item>
    /// <item><description><c>200 OK</c> — duplicate of an already-received message (idempotent replay)</description></item>
    /// <item><description><c>400 Bad Request</c> — provider rejected the request (bad signature, malformed body, etc.)</description></item>
    /// <item><description><c>404 Not Found</c> — no provider is registered under that key</description></item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">Route pattern. Must contain a <c>{providerKey}</c> segment. Defaults to <c>/webhooks/{providerKey}</c>.</param>
    public static RouteHandlerBuilder MapInboxWebhooks(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/webhooks/{providerKey}")
    {
        return endpoints.MapPost(pattern,
            (string providerKey, HttpContext context) => HandleAsync(context, providerKey));
    }

    /// <summary>
    /// Maps a POST endpoint bound to a single provider key. Useful when the provider key
    /// isn't a route segment (e.g. one hard-coded endpoint per provider).
    /// </summary>
    public static RouteHandlerBuilder MapInboxWebhook(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        string providerKey)
    {
        // Cast to Delegate to pick the MapPost(Delegate) overload rather than
        // MapPost(RequestDelegate), which returns IEndpointConventionBuilder.
        return endpoints.MapPost(pattern,
            (Delegate)((HttpContext context) => HandleAsync(context, providerKey)));
    }

    private static async Task<IResult> HandleAsync(HttpContext context, string providerKey)
    {
        var sp = context.RequestServices;
        var registry = sp.GetRequiredService<IWebhookProviderRegistry>();
        var publisher = sp.GetRequiredService<IInboxPublisher>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("InboxNet.Webhook");

        var provider = registry.Get(providerKey);
        if (provider is null)
        {
            logger.LogWarning("No webhook provider registered for key {ProviderKey}", providerKey);
            return Results.NotFound(new { error = $"Unknown provider '{providerKey}'" });
        }

        using var activity = InboxActivitySource.Source.StartActivity("inbox.receive");
        activity?.SetTag("inbox.provider_key", providerKey);

        // Buffer the body once. Providers need the raw bytes for signature verification
        // and for computing a content SHA — re-reading request.Body is not safe.
        string rawBody;
        try
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            rawBody = await reader.ReadToEndAsync(context.RequestAborted);
            context.Request.Body.Position = 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to buffer webhook body for provider {ProviderKey}", providerKey);
            return Results.BadRequest(new { error = "Failed to read request body" });
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in context.Request.Headers)
        {
            if (h.Value.Count > 0)
                headers[h.Key] = h.Value.ToString();
        }

        var webhookContext = new WebhookRequestContext
        {
            ProviderKey = providerKey,
            RawBody = rawBody,
            Headers = headers,
            RoutePath = context.Request.Path.HasValue ? context.Request.Path.Value : null,
            QueryString = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null
        };

        var parse = await provider.ParseAsync(webhookContext, context.RequestAborted);

        if (!parse.IsValid)
        {
            InboxMetrics.MessagesInvalid.Add(1,
                new KeyValuePair<string, object?>("provider", providerKey));

            logger.LogWarning(
                "Webhook rejected by provider {ProviderKey}: {Reason}",
                providerKey, parse.InvalidReason);

            activity?.SetTag("inbox.valid", false);
            activity?.SetTag("inbox.invalid_reason", parse.InvalidReason);

            return Results.BadRequest(new { error = parse.InvalidReason ?? "Invalid webhook" });
        }

        var result = await publisher.PublishAsync(providerKey, parse, context.RequestAborted);

        if (result.IsDuplicate)
        {
            // 200 rather than 202 so providers that distinguish them see idempotent replay.
            return Results.Ok(new { messageId = result.MessageId, duplicate = true });
        }

        return Results.Accepted(value: new { messageId = result.MessageId });
    }
}
