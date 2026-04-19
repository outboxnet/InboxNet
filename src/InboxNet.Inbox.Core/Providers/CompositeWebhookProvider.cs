using InboxNet.Inbox.Interfaces;
using InboxNet.Inbox.Models;

namespace InboxNet.Inbox.Providers;

/// <summary>
/// Bridges a keyed (<see cref="IWebhookSignatureValidator"/>, <see cref="IWebhookPayloadMapper"/>)
/// pair into the <see cref="IWebhookProvider"/> seam the registry and endpoint consume.
/// Registered indirectly via
/// <c>AddProvider&lt;TValidator, TMapper&gt;(providerKey)</c>.
/// </summary>
internal sealed class CompositeWebhookProvider : IWebhookProvider
{
    private readonly IWebhookSignatureValidator _validator;
    private readonly IWebhookPayloadMapper _mapper;

    public CompositeWebhookProvider(
        string key,
        IWebhookSignatureValidator validator,
        IWebhookPayloadMapper mapper)
    {
        Key = key;
        _validator = validator;
        _mapper = mapper;
    }

    public string Key { get; }

    public async Task<WebhookParseResult> ParseAsync(
        WebhookRequestContext context,
        CancellationToken ct = default)
    {
        var validation = await _validator.ValidateAsync(context, ct);
        if (!validation.IsValid)
            return WebhookParseResult.Invalid(validation.FailureReason ?? "Signature validation failed");

        return await _mapper.MapAsync(context, ct);
    }
}
