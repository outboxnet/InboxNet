# InboxNet

[![CI](https://github.com/undefined-exception/InboxNet/actions/workflows/ci.yml/badge.svg)](https://github.com/undefined-exception/InboxNet/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/InboxNet.Core.svg)](https://www.nuget.org/packages/InboxNet.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A transactional inbox library for .NET. InboxNet receives incoming webhooks from external providers, persists them atomically, and dispatches them to your application handlers with at-least-once delivery guarantees, deduplication, and retry.

## The Problem

Webhook receivers share a common failure pattern: the HTTP endpoint does business logic inline, or writes to the database and crashes mid-way, or processes the same event twice during provider retries.

```
POST /webhooks/stripe
  1. Validate signature          ✅
  2. Call _paymentService.Apply() ❌ database timeout
  → Stripe retries → event processed twice
  → or: database updated but handler throws → event lost
```

The root issues:
- **No durability** — if your process crashes after acknowledging the webhook, the event is lost
- **No deduplication** — providers retry on network errors; your handler sees the same event multiple times
- **No retry** — a failing handler has to succeed immediately or the event is dropped

## The Solution: Transactional Inbox Pattern

InboxNet decouples *receiving* from *processing*:

```
1. POST /webhooks/stripe arrives
2. Validate signature (reject bad requests immediately)
3. INSERT INTO InboxMessages — same DB transaction, dedup by provider event ID
4. Return 202 Accepted

5. Background dispatcher locks the message
6. Runs your IInboxHandler(s) sequentially
7. On success → mark processed
8. On failure → retry with backoff | On exhaustion → dead-letter
```

Persisting the message in the same database that your handlers write to gives you atomicity. The dispatcher guarantees every accepted webhook reaches your handlers at least once — even across restarts, crashes, and multi-instance deployments.

## Key Features

- **Transactional receipt** — webhook is persisted in the same DB as your domain data before you return 202
- **Built-in deduplication** — duplicate events (by provider event ID or content SHA-256) return 200 OK without a second insert
- **Per-handler attempt tracking** — each handler's success is recorded independently; retries skip handlers that already succeeded
- **At-least-once dispatch** — failed handlers are retried with exponential backoff; exhausted handlers are dead-lettered
- **Hot + cold dispatch paths** — same-instance messages drain a `Channel<Guid>` for sub-millisecond latency; cross-instance messages are caught by the cold-path batch scan
- **Multi-instance safe** — DB-level `UPDATE WHERE Status=Pending` is the lock gate; exactly one instance wins per message
- **Ordered processing** — messages with the same `(TenantId, ProviderKey, EntityId)` are dispatched in arrival order
- **Provider model** — validate and parse each provider's signature scheme once, independently of handler logic
- **Built-in providers** — Stripe (v1 HMAC scheme), GitHub (X-Hub-Signature-256), Generic HMAC-SHA256
- **Observability** — built-in OpenTelemetry `ActivitySource` and `System.Diagnostics.Metrics`

## Packages

| Package | Description |
|---|---|
| `InboxNet.Core` | Contracts, models, options, observability |
| `InboxNet.EntityFrameworkCore` | EF Core + SQL Server persistence and publisher |
| `InboxNet.AspNetCore` | `MapInboxWebhooks()` endpoint extension |
| `InboxNet.Processor` | Background dispatcher hosted service |
| `InboxNet.Providers` | Built-in providers: Stripe, GitHub, Generic HMAC |

## Getting Started

### Step 1: Install packages

```bash
dotnet add package InboxNet.Core
dotnet add package InboxNet.EntityFrameworkCore
dotnet add package InboxNet.AspNetCore
dotnet add package InboxNet.Processor
dotnet add package InboxNet.Providers   # optional — for Stripe/GitHub built-ins
```

### Step 2: Configure services

```csharp
// Program.cs
builder.Services
    .AddInboxNet(options =>
    {
        options.SchemaName = "inbox";
        options.BatchSize = 50;
        options.MaxConcurrentDispatch = 10;
    })
    .UseSqlServer(connectionString)
    .AddBackgroundDispatcher()
    .AddStripeProvider(o => o.SigningSecret = config["Webhooks:Stripe:SigningSecret"])
    .AddGitHubProvider(o => o.Secret = config["Webhooks:GitHub:Secret"])
    .AddHandler<OrderPaidHandler>(h => h
        .ForProvider("stripe")
        .ForEvent("invoice.paid"))
    .AddHandler<PushEventHandler>(h => h
        .ForProvider("github")
        .ForEvent("push"));
```

### Step 3: Set up the database

Apply InboxNet's EF Core configurations inside your `DbContext`:

```csharp
public class AppDbContext : DbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyInboxConfigurations(schema: "inbox");
        // ... your own configurations
    }
}
```

Then generate and apply migrations:

```bash
dotnet ef migrations add AddInbox --context InboxDbContext
dotnet ef database update
```

Or use `EnsureCreatedAsync()` for demos and development.

### Step 4: Map the webhook endpoint

```csharp
var app = builder.Build();

// Maps POST /webhooks/{providerKey}
// e.g. POST /webhooks/stripe, POST /webhooks/github
app.MapInboxWebhooks();

app.Run();
```

Or map a fixed URL per provider:

```csharp
app.MapInboxWebhook("/stripe/events", "stripe");
app.MapInboxWebhook("/github/hooks", "github");
```

**Response codes from the endpoint:**

| Code | Meaning |
|---|---|
| `202 Accepted` | New message persisted, will be dispatched |
| `200 OK` | Duplicate — already received this event (safe to acknowledge to the provider) |
| `400 Bad Request` | Signature invalid or body malformed |
| `404 Not Found` | No provider registered under that key |

### Step 5: Write handlers

```csharp
public class OrderPaidHandler : IInboxHandler
{
    private readonly AppDbContext _db;
    private readonly IEmailService _email;

    public OrderPaidHandler(AppDbContext db, IEmailService email)
    {
        _db = db;
        _email = email;
    }

    public async Task HandleAsync(InboxMessage message, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<StripeInvoice>(message.Payload)!;

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.StripeId == payload.Id, ct);
        if (order is null) return; // idempotent — nothing to do

        order.Status = OrderStatus.Paid;
        await _db.SaveChangesAsync(ct);
        await _email.SendReceiptAsync(order, ct);
    }
}
```

Handlers are resolved from a per-message DI scope, so injecting `DbContext` and other scoped services is safe.

**Important:** handlers must be idempotent on `InboxMessage.Id`. InboxNet provides at-least-once delivery — a handler can run more than once if a lock expires before the attempt record is persisted.

## Providers

A provider validates the incoming request's signature and extracts the canonical event type, event ID, and payload. Register it once; all matching webhooks go through it automatically.

### Built-in providers

**Stripe:**
```csharp
.AddStripeProvider(o =>
{
    o.SigningSecret = "whsec_...";
    o.Key = "stripe";           // optional, default "stripe"
    o.ToleranceSeconds = 300;   // optional, default 300
})
```

**GitHub:**
```csharp
.AddGitHubProvider(o =>
{
    o.Secret = "your-webhook-secret";
    o.Key = "github";   // optional, default "github"
})
```

**Generic HMAC-SHA256:**
```csharp
.AddGenericHmacProvider(o =>
{
    o.Key = "acme";
    o.Secret = "...";
    o.SignatureHeader = "X-Acme-Signature";
    o.SignaturePrefix = "sha256=";
})
```

### Custom provider

Implement `IWebhookProvider` and register it:

```csharp
public class AcmeProvider : IWebhookProvider
{
    public string Key => "acme";

    public Task<WebhookParseResult> ParseAsync(WebhookRequestContext context, CancellationToken ct)
    {
        // 1. Validate signature
        if (!ValidateSignature(context))
            return Task.FromResult(WebhookParseResult.Invalid("Signature mismatch"));

        // 2. Extract event fields
        using var doc = JsonDocument.Parse(context.RawBody);
        var eventType = doc.RootElement.GetProperty("event").GetString()!;
        var eventId   = doc.RootElement.GetProperty("id").GetString();

        return Task.FromResult(WebhookParseResult.Valid(
            eventType:      eventType,
            payload:        context.RawBody,
            contentSha256:  WebhookSignatureHelpers.ComputeContentSha256(context.RawBody),
            providerEventId: eventId));
    }
}

// Registration
.AddProvider<AcmeProvider>()
```

Or split validation and parsing into separate injectable classes:

```csharp
.AddProvider<AcmeSignatureValidator, AcmePayloadMapper>("acme")
```

## Handler Registration

```csharp
.AddHandler<MyHandler>()                           // matches all providers and events
.AddHandler<MyHandler>(h => h.ForProvider("stripe"))           // stripe only, all events
.AddHandler<MyHandler>(h => h.ForEvent("invoice.paid"))        // all providers, this event
.AddHandler<MyHandler>(h => h
    .ForProvider("stripe")
    .ForEvent("invoice.paid")
    .WithMaxRetries(10)                            // default 5
    .WithName("MyApp.Billing.OrderPaidHandler"))   // stable identity for attempt records
```

Multiple handlers can target the same `(provider, event)` pair. They run sequentially in registration order. A failing handler stops the chain — remaining handlers run on the next retry.

## Architecture

### Receive → Persist → Dispatch

```
  External Provider (Stripe, GitHub, etc.)
        │
        │  POST /webhooks/{providerKey}
        ▼
  ┌─────────────────────────────┐
  │  MapInboxWebhooks endpoint  │
  │                             │
  │  1. Resolve IWebhookProvider│
  │  2. ParseAsync()            │──── signature invalid ──▶ 400
  │  3. IInboxPublisher         │
  │     InsertOrGetDuplicate()  │──── duplicate ──────────▶ 200
  │  4. Signal dispatcher       │
  └──────────────┬──────────────┘
                 │ 202 Accepted
                 ▼
        ┌─────────────────┐
        │  InboxMessages  │  (SQL Server)
        │  Status=Pending │
        └────────┬────────┘
                 │
  ┌──────────────▼──────────────────────────────────────┐
  │              InboxProcessorService                   │
  │                                                      │
  │  HOT PATH  ─ Channel<Guid> drained per commit        │
  │    TryLockByIdAsync (PK-seek UPDATE)                 │
  │    → DispatchMessageAsync                            │
  │                                                      │
  │  COLD PATH ─ polls every ColdPollingInterval (1 s)   │
  │    LockNextBatchAsync (batch UPDATE + OPENJSON skip)  │
  │    → DispatchMessageAsync (parallel, DOP configured) │
  └──────────────┬──────────────────────────────────────┘
                 │
        ┌────────▼────────┐
        │ DispatchMessage  │
        │                  │
        │  for each handler│
        │  (in order):     │
        │  - skip if       │
        │    already       │
        │    succeeded     │
        │  - HandleAsync() │
        │  - save attempt  │
        │  - stop on fail  │
        └────────┬─────────┘
                 │
     ┌───────────┼──────────────┐
     ▼           ▼              ▼
  Processed   Schedule      Dead-letter
  (all ok)    retry at      (retries
              NextRetryAt   exhausted)
```

### Hot path and cold path

When a webhook is accepted, the publisher writes to the database and then pushes the message ID into an in-process `Channel<Guid>`. The hot path drains the channel continuously, locking each message with a single PK-seek `UPDATE WHERE Status=Pending`. This gives sub-millisecond same-instance dispatch latency.

The cold path scans the database on a fixed interval (default 1 second). It handles:
- Messages published by other instances (the channel is in-process only)
- Scheduled retries (`NextRetryAt <= NOW`)
- Hot-path overflow (channel capacity 10,000; dropped IDs are still in the DB)
- Expired lock recovery (`ReleaseExpiredLocksAsync`, throttled to once per 30 s)

The cold path excludes IDs currently in the hot-path's in-flight set via `NOT IN (OPENJSON(@skipJson))`, preventing intra-process races.

### Per-handler attempt tracking

Each handler's outcome is stored in `InboxHandlerAttempts` as a separate row. When a message is retried, the dispatcher reads the existing attempt records and skips any handler that already has a success record. This means:

- Handler A succeeds, Handler B fails → retry runs Handler B only
- If attempt records cannot be saved after a successful handler run, the lock is left to expire rather than immediately retrying — this avoids re-running handlers that actually succeeded

### Multi-instance safety

All instances share the same SQL Server database. The lock acquisition uses:

```sql
UPDATE InboxMessages
SET Status = 'Processing', LockedBy = @instanceId, LockedUntil = @now + @timeout
OUTPUT INSERTED.*
WHERE Id = @id AND Status = 'Pending' AND LockedUntil < @now
```

Only one instance can win the atomic update. Every write-back (`MarkAsProcessed`, `IncrementRetry`, `MarkAsDeadLettered`) includes `WHERE LockedBy = @lockedBy` — if another instance has reclaimed the lock, the update affects 0 rows and is silently ignored.

## Configuration Reference

### `AddInboxNet(options => ...)`

```csharp
services.AddInboxNet(options =>
{
    // SQL schema for inbox tables. Default: "inbox"
    options.SchemaName = "inbox";

    // Messages locked per processing cycle. Default: 50
    options.BatchSize = 50;

    // How long a message stays locked before being eligible for re-processing.
    // Must exceed worst-case total handler execution time for one message.
    // Default: 5 minutes
    options.DefaultVisibilityTimeout = TimeSpan.FromMinutes(5);

    // Unique identity for this instance (used in LockedBy column).
    // Default: "{MachineName}-{Guid}" — auto-generated, leave as default in most cases.
    options.InstanceId = "my-instance-1";

    // Max messages dispatched concurrently within a batch. Default: 10
    options.MaxConcurrentDispatch = 10;

    // Dispatch messages with the same (TenantId, ProviderKey, EntityId) in arrival order.
    // Default: true
    options.EnableOrderedProcessing = true;

    // Only dispatch messages for this tenant. Null = all tenants (default).
    options.TenantFilter = null;
});
```

### `.AddBackgroundDispatcher(options => ...)`

```csharp
.AddBackgroundDispatcher(options =>
{
    // How often the cold path scans the DB.
    // Default: 1 second
    options.ColdPollingInterval = TimeSpan.FromSeconds(1);
})
```

### `.ConfigureRetry(options => ...)`

```csharp
.ConfigureRetry(options =>
{
    // Default: 5
    options.MaxRetries = 5;
    // Default: 5 seconds
    options.BaseDelay = TimeSpan.FromSeconds(5);
    // Default: 5 minutes
    options.MaxDelay = TimeSpan.FromMinutes(5);
    // Default: 0.2 (±20% jitter)
    options.JitterFactor = 0.2;
})
```

## Observability

### Traces

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("InboxNet"));
```

| Span | Tags |
|---|---|
| `inbox.receive` | `inbox.provider_key`, `inbox.valid`, `inbox.invalid_reason` |
| `inbox.process_batch` | `inbox.batch_size` |
| `inbox.dispatch` | `inbox.message_id`, `inbox.provider_key`, `inbox.event_type` |

### Metrics

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("InboxNet"));
```

| Metric | Type | Tags |
|---|---|---|
| `inbox.messages.received` | Counter | `provider` |
| `inbox.messages.invalid` | Counter | `provider` |
| `inbox.messages.processed` | Counter | `provider`, `event_type` |
| `inbox.messages.failed` | Counter | `provider`, `event_type` |
| `inbox.messages.dead_lettered` | Counter | `provider`, `event_type` |
| `inbox.handlers.attempts` | Counter | `handler`, `provider`, `event_type` |
| `inbox.handlers.successes` | Counter | `handler`, `provider`, `event_type` |
| `inbox.handlers.failures` | Counter | `handler`, `provider`, `event_type` |
| `inbox.handlers.duration_ms` | Histogram | `handler` |
| `inbox.batches.processed` | Counter | — |
| `inbox.batch.size` | Histogram | — |
| `inbox.processing.duration_ms` | Histogram | — |

## Project Structure

```
InboxNet/
├── src/
│   ├── InboxNet.Core/                # Contracts, models, options, observability
│   ├── InboxNet.EntityFrameworkCore/ # EF Core + SQL Server persistence
│   ├── InboxNet.AspNetCore/          # MapInboxWebhooks() endpoint extension
│   ├── InboxNet.Processor/           # Background dispatcher hosted service
│   └── InboxNet.Providers/           # Built-in providers: Stripe, GitHub, Generic HMAC
├── tests/
│   ├── InboxNet.Core.Tests/
│   ├── InboxNet.Delivery.Tests/
│   ├── InboxNet.LoadTests/
│   └── InboxNet.Processor.Tests/
├── samples/
│   └── InboxNet.SampleApp/           # ASP.NET Core sample with Stripe, GitHub, Acme providers
├── Directory.Build.props             # Shared build + NuGet package properties
├── Directory.Packages.props          # Centralized package version management
└── .github/workflows/
    ├── ci.yml                        # Build + test on every push/PR
    └── publish.yml                   # Publish to NuGet on GitHub release
```

## Publishing to NuGet

### Automated (GitHub Actions)

1. Add your NuGet API key as a repository secret named `NUGET_API_KEY`
2. Create a GitHub release with a version tag (e.g. `1.0.0` or `1.2.0-preview.1`)
3. The workflow builds, tests, packs all packages with the tag as the version, and pushes to nuget.org

### Manual

```bash
dotnet pack -c Release -o ./nupkgs /p:Version=1.0.0
dotnet nuget push ./nupkgs/*.nupkg \
    --api-key YOUR_API_KEY \
    --source https://api.nuget.org/v3/index.json \
    --skip-duplicate
```

## License

[MIT](LICENSE)
