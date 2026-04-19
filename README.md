# InboxNet

[![CI](https://github.com/outboxnet/InboxNet/actions/workflows/ci.yml/badge.svg)](https://github.com/outboxnet/InboxNet/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/InboxNet.Core.svg)](https://www.nuget.org/packages/InboxNet.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A transactional outbox library for .NET that guarantees reliable webhook delivery in distributed systems. InboxNet ensures that when your application writes data and needs to notify external systems, either **both happen or neither does** — eliminating the class of bugs where your database commits but the notification is silently lost.

## The Problem

In distributed systems, the **dual-write problem** occurs when a service needs to update two different systems (e.g., a database and a webhook endpoint) with no built-in guarantee that both succeed or both fail.

```
1. Save order to database      ✅ succeeds
2. Send webhook to payment svc  ❌ app crashes / network timeout / partial failure
→ Order exists but payment never initiated
```

This isn't an edge case — in production systems handling thousands of requests these failures happen daily.

## The Solution: Transactional Outbox Pattern

```
1. BEGIN TRANSACTION
2.   Save order to database
3.   Write outbox message to OutboxMessages table (same DB, same transaction)
4. COMMIT TRANSACTION
5. Background processor picks up outbox messages and delivers webhooks
6. On success → mark delivered | On failure → retry with backoff | On exhaustion → dead-letter
```

By writing the outbox message in the *same database transaction* as your domain data, you get atomicity for free. The background processor guarantees **at-least-once** delivery with idempotency headers so consumers can safely deduplicate retries.

## Key Features

- **Transactional guarantee** — outbox writes participate in your existing database transaction
- **Sub-millisecond same-instance latency** — hot path drains a `Channel<Guid>` as messages are published; cold path handles cross-instance messages within one polling interval (default 1 s)
- **Duplicate-safe multi-instance delivery** — DB-level PK-seek `UPDATE WHERE Status=Pending` is the lock gate; only one instance wins per message, across any number of replicas
- **No hot+cold race** — in-flight hot-path IDs are excluded from the cold-path SQL query via `OPENJSON NOT IN`; no wasted lock attempts within the same process
- **Duplicate-safe delivery** — deterministic `X-Outbox-Delivery-Id` per attempt; processor tracks per-subscription success to skip already-delivered subscriptions on retry
- **Parallel delivery** — configurable concurrency at both the message and subscription level
- **HMAC-SHA256 webhook signing** — receivers can verify payload authenticity
- **Dead-letter queue** — exhausted messages are preserved for manual review
- **Per-subscription settings** — independent retry limit, timeout, and custom headers per endpoint
- **Multi-tenant** — per-tenant webhook routing, per-tenant secrets, ambient `TenantId`/`UserId` from HTTP context
- **Config-driven subscriptions** — define routes in `appsettings.json` without a database table
- **Ordered processing** — partition-key ordering ensures causality within a `(TenantId, UserId, EntityId)` group
- **Observability** — built-in OpenTelemetry `ActivitySource` and `System.Diagnostics.Metrics`
- **Two SQL Server providers** — EF Core for convenience, direct ADO.NET for minimal overhead
- **Azure Functions support** — timer-trigger variant for serverless hosting

## Packages

| Package | Description |
|---|---|
| `InboxNet.Core` | Core contracts, models, options, observability |
| `InboxNet.EntityFrameworkCore` | EF Core + SQL Server stores and publisher |
| `InboxNet.SqlServer` | Direct ADO.NET SQL Server stores and publisher (no EF dependency) |
| `InboxNet.Processor` | Background hosted service for outbox processing |
| `InboxNet.Delivery` | HTTP webhook delivery with HMAC-SHA256 signing and retry |
| `InboxNet.AzureStorageQueue` | Azure Storage Queue publisher for queue-mediated processing |
| `InboxNet.AzureFunctions` | Azure Functions timer trigger for serverless processing |

## Getting Started

### Step 1: Install packages

**EF Core app (most common):**
```bash
dotnet add package InboxNet.Core
dotnet add package InboxNet.EntityFrameworkCore
dotnet add package InboxNet.Processor
dotnet add package InboxNet.Delivery
```

**Direct ADO.NET / Dapper app:**
```bash
dotnet add package InboxNet.Core
dotnet add package InboxNet.SqlServer
dotnet add package InboxNet.Processor
dotnet add package InboxNet.Delivery
```

**Azure Functions (serverless):**
```bash
dotnet add package InboxNet.Core
dotnet add package InboxNet.EntityFrameworkCore  # or InboxNet.SqlServer
dotnet add package InboxNet.AzureFunctions
dotnet add package InboxNet.Delivery
```

### Step 2: Configure services

**Option A: Entity Framework Core**

```csharp
// Program.cs
builder.Services
    .AddInboxNet(options =>
    {
        options.SchemaName = "outbox";
        options.BatchSize = 50;
        options.DefaultVisibilityTimeout = TimeSpan.FromMinutes(5);
        options.MaxConcurrentDeliveries = 10;
        options.MaxConcurrentSubscriptionDeliveries = 4;
    })
    .UseSqlServerContext<AppDbContext>(
        builder.Configuration.GetConnectionString("Default"),
        sql => sql.MigrationsAssembly = "MyApp")
    .AddBackgroundProcessor()
    .AddWebhookDelivery();
```

**Option B: Direct SQL Server (no EF Core)**

```csharp
// Program.cs
builder.Services
    .AddInboxNet(options =>
    {
        options.SchemaName = "outbox";
        options.BatchSize = 50;
    })
    .UseDirectSqlServer(builder.Configuration.GetConnectionString("Default"))
    .AddBackgroundProcessor()
    .AddWebhookDelivery();

// Implement and register ISqlTransactionAccessor so the publisher
// can enlist in your ADO.NET transaction.
builder.Services.AddScoped<ISqlTransactionAccessor, MySqlTransactionAccessor>();
```

**Option C: Azure Functions**

```csharp
// Program.cs (Functions host)
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services
            .AddInboxNet()
            .UseSqlServerContext<AppDbContext>(connectionString)
            .AddAzureFunctionsProcessor()
            .AddWebhookDelivery();
    })
    .Build();
```

Set `Outbox:TimerCron` in `local.settings.json` (or App Settings) to control the timer interval:
```json
{ "Outbox:TimerCron": "*/30 * * * * *" }
```

### Step 3: Set up the database

**EF Core — apply outbox table configurations in your DbContext:**

```csharp
public class AppDbContext : DbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyOutboxConfigurations(schema: "outbox");
        // ... your own entity configurations
    }
}
```

Then generate and apply migrations:
```bash
dotnet ef migrations add AddOutbox --context AppDbContext
dotnet ef database update
```

**Direct SQL — generate the schema from a temporary EF Core migration** or write it manually. The three tables are `OutboxMessages`, `WebhookSubscriptions`, and `DeliveryAttempts`. See `InboxNet.EntityFrameworkCore/Configurations/` for exact column definitions.

### Step 4: Register webhook subscriptions

**Option A: Database-backed (dynamic)**

Insert rows into `WebhookSubscriptions`. Key columns:

| Column | Example | Notes |
|---|---|---|
| `EventType` | `order.placed` | Routing key |
| `WebhookUrl` | `https://payment-svc/webhooks` | Target endpoint |
| `Secret` | `whsec_abc123` | Used for HMAC-SHA256 signing |
| `TenantId` | `tenant-a` or `null` | `null` = global (applies to all tenants) |
| `IsActive` | `true` | |
| `MaxRetries` | `5` | Per-subscription retry limit |
| `TimeoutSeconds` | `30` | Per-request timeout |

**Option B: Config-driven (static)**

```csharp
// Global endpoint (all tenants)
builder.Services
    .AddInboxNet()
    .UseConfigWebhooks(builder.Configuration);
```

```json
// appsettings.json
{
  "Outbox": {
    "Webhooks": {
      "Mode": "Global",
      "Global": {
        "Url": "https://example.com/webhook",
        "Secret": "whsec_abc123",
        "MaxRetries": 5,
        "TimeoutSeconds": 30
      }
    }
  }
}
```

Or configure per-tenant routing:

```json
{
  "Outbox": {
    "Webhooks": {
      "Mode": "PerTenant",
      "Tenants": {
        "tenant-a": { "Url": "https://tenant-a.example.com/hook", "Secret": "s1" },
        "tenant-b": { "Url": "https://tenant-b.example.com/hook", "Secret": "s2" },
        "default":  { "Url": "https://fallback.example.com/hook", "Secret": "s3" }
      }
    }
  }
}
```

### Step 5: Publish outbox messages

**EF Core publisher — writes in the same transaction as your domain data:**

```csharp
public class PlaceOrderHandler
{
    private readonly AppDbContext _db;
    private readonly IOutboxPublisher _outbox;

    public PlaceOrderHandler(AppDbContext db, IOutboxPublisher outbox)
    {
        _db = db;
        _outbox = outbox;
    }

    public async Task Handle(PlaceOrderCommand cmd, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var order = new Order { /* ... */ };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);

        // This INSERT goes into the SAME transaction — atomic with the order write.
        await _outbox.PublishAsync(
            eventType: "order.placed",
            payload: new { order.Id, order.Total, order.CustomerId },
            correlationId: cmd.CorrelationId,
            entityId: order.Id.ToString(),   // optional: used for ordered processing
            cancellationToken: ct);

        await tx.CommitAsync(ct);
        // If commit fails → both the order AND the outbox message are rolled back.
        // If commit succeeds → the background processor delivers the webhook.
        // After commit, the publisher signals the processor for near-zero latency.
    }
}
```

**Direct SQL publisher — uses `ISqlTransactionAccessor`:**

```csharp
public class MySqlTransactionAccessor : ISqlTransactionAccessor
{
    public SqlConnection Connection { get; set; } = default!;
    public SqlTransaction Transaction { get; set; } = default!;
}

public class PlaceOrderHandler
{
    private readonly IOutboxPublisher _outbox;
    private readonly MySqlTransactionAccessor _txAccessor;
    private readonly string _connectionString;

    public async Task Handle(PlaceOrderCommand cmd, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync("INSERT INTO Orders ...", new { /* ... */ }, tx);

        // Provide the connection/transaction before publishing.
        _txAccessor.Connection = conn;
        _txAccessor.Transaction = tx;

        await _outbox.PublishAsync(
            eventType: "order.placed",
            payload: new { cmd.OrderId, cmd.Total },
            cancellationToken: ct);

        await tx.CommitAsync(ct);
    }
}
```

### Step 6: Handle webhooks on the receiver side

Every delivery includes these headers:

| Header | Value | Purpose |
|---|---|---|
| `X-Outbox-Signature` | `sha256={hex}` | HMAC-SHA256 of the raw payload body |
| `X-Outbox-Event` | `order.placed` | Event type |
| `X-Outbox-Message-Id` | UUID | Stable across all retries of the same message |
| `X-Outbox-Delivery-Id` | UUID | Unique per attempt (deterministic — same attempt always sends the same ID) |
| `X-Outbox-Subscription-Id` | UUID | Identifies which subscription matched |
| `X-Outbox-Timestamp` | Unix seconds | Time the delivery was attempted |
| `X-Outbox-Correlation-Id` | string | Forwarded from `PublishAsync` if provided |

**Verifying the signature:**
```csharp
[HttpPost("/webhooks")]
public IActionResult Receive()
{
    using var reader = new StreamReader(Request.Body);
    var rawBody = reader.ReadToEnd();

    var expected = "sha256=" + Convert.ToHexString(
        HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(webhookSecret),
            Encoding.UTF8.GetBytes(rawBody)));

    var received = Request.Headers["X-Outbox-Signature"].ToString();

    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(received)))
        return Unauthorized();

    // Process event...
    return Ok();
}
```

**Deduplicating retries:**

Use `X-Outbox-Message-Id` + `X-Outbox-Subscription-Id` as a composite idempotency key. InboxNet provides at-least-once delivery, so your receiver should be idempotent:

```csharp
var idempotencyKey = $"{Request.Headers["X-Outbox-Message-Id"]}:{Request.Headers["X-Outbox-Subscription-Id"]}";

if (await _cache.ExistsAsync(idempotencyKey))
    return Ok(); // already processed

await ProcessEventAsync(payload);
await _cache.SetAsync(idempotencyKey, true, TimeSpan.FromDays(7));
return Ok();
```

## Architecture

### Hot Path and Cold Path

InboxNet runs two concurrent processing loops inside every host instance. They have different responsibilities and are designed to complement each other, not compete.

```
  YOUR APPLICATION INSTANCE
  ┌──────────────────────────────────────────────────────────────────────────┐
  │                                                                          │
  │  ┌─────────────────┐  BEGIN TX                                          │
  │  │  Domain Logic   │──────────────────────────────────────────────┐     │
  │  │  (API handler,  │                                              │     │
  │  │   job, etc.)    │                                              ▼     │
  │  └─────────────────┘                                    ┌──────────────┐ │
  │                                                         │  SQL Server  │ │
  │  ┌─────────────────┐  (same TX, atomic)                │              │ │
  │  │ IOutboxPublisher│──── INSERT OutboxMessage ─────────>│  Orders      │ │
  │  │                 │                                    │  OutboxMsgs  │ │
  │  │  after COMMIT:  │                                    │  (Pending)   │ │
  │  │  Notify(msgId)  │                                    └──────┬───────┘ │
  │  └────────┬────────┘                                          │         │
  │           │                                                   │         │
  │           ▼  Channel<Guid>  capacity=10 000, DropOldest       │         │
  │  ┌─────────────────────────────────────────────────────────┐  │         │
  │  │              OutboxProcessorService                     │  │         │
  │  │                                                         │  │         │
  │  │  ┌──────────────────────────────────────────────────┐   │  │         │
  │  │  │  HOT PATH  (Task 1)                              │   │  │         │
  │  │  │                                                  │   │  │         │
  │  │  │  Parallel.ForEachAsync(channel.ReadAllAsync())   │   │  │         │
  │  │  │    ├─ mark ID as in-flight                       │   │  │         │
  │  │  │    ├─ TryLockByIdAsync (PK-seek UPDATE)  ────────┼───┼──┤         │
  │  │  │    │    WHERE Id=@id AND Status=Pending           │   │  │         │
  │  │  │    ├─ if locked: fetch subs → deliver → bookkeep │   │  │         │
  │  │  │    └─ remove from in-flight                       │   │  │         │
  │  │  │                                                  │   │  │         │
  │  │  │  Latency: <1 ms from Notify() to lock attempt    │   │  │         │
  │  │  └──────────────────────────────────────────────────┘   │  │         │
  │  │                                                         │  │         │
  │  │  ┌──────────────────────────────────────────────────┐   │  │         │
  │  │  │  COLD PATH  (Task 2)                             │   │  │         │
  │  │  │                                                  │   │  │         │
  │  │  │  loop every ColdPollingInterval (default: 1 s)   │   │  │         │
  │  │  │    ├─ snapshot in-flight IDs as skipIds           │   │  │         │
  │  │  │    ├─ LockNextBatchAsync  ───────────────────────┼───┼──┤         │
  │  │  │    │    WHERE Id NOT IN (skipIds via OPENJSON)    │   │  │         │
  │  │  │    │    WITH (UPDLOCK, READPAST)                  │   │  │         │
  │  │  │    └─ fetch subs → deliver (parallel) → bookkeep │   │  │         │
  │  │  │                                                  │   │  │         │
  │  │  │  Handles: cross-instance msgs, retries, overflow │   │  │         │
  │  │  └──────────────────────────────────────────────────┘   │  │         │
  │  └─────────────────────────────────────────────────────────┘  │         │
  │                                                               │         │
  └───────────────────────────────────────────────────────────────┘         │
                                                                            │
                                              ┌─────────────────────────────┘
                                              ▼
                                   ┌────────────────────┐
                                   │  External Services  │
                                   │  • Payment API      │
                                   │  • Inventory API    │
                                   │  • Analytics API    │
                                   └────────────────────┘
```

#### Hot path — same-instance, sub-millisecond delivery

When your code calls `PublishAsync`, the outbox INSERT is part of your database transaction. After the transaction commits, the publisher calls `_signal.Notify(messageId)` — a non-blocking write into a `Channel<Guid>`. The hot path is a `Parallel.ForEachAsync` loop draining that channel continuously.

For each message ID dequeued:

1. **Mark as in-flight** — the ID is added to `_hotInFlight` (`ConcurrentDictionary<Guid, byte>`)
2. **Atomic PK-seek lock** — `TryLockByIdAsync` issues a single-row `UPDATE WHERE Id=@id AND Status=Pending AND LockedUntil < NOW` with an `OUTPUT INSERTED.*` clause. SQL Server evaluates this atomically under read-committed isolation — only one instance across the entire cluster can win
3. **If won**: fetch subscriptions → deliver in parallel → save attempts → mark as processed/retry/dead-letter
4. **If lost** (another instance already locked it): returns immediately with no work done
5. **Remove from in-flight** — always in `finally`

The channel capacity is 10,000 with `DropOldest`. Under a burst that exceeds capacity, the oldest hints are dropped — those messages are still in the database and will be picked up by the cold path within one polling interval. No message is ever lost; only the sub-millisecond delivery optimisation degrades gracefully.

#### Cold path — cross-instance recovery, retries, overflow safety net

The cold path runs on a fixed `ColdPollingInterval` (default 1 second) regardless of queue activity. It serves three purposes:

| Scenario | How it's handled |
|---|---|
| Message published by **another instance** | Hot path channel is in-process only; the cold path's batch scan finds the row |
| **Scheduled retry** (`NextRetryAt` in the future) | Cold path query includes `NextRetryAt <= NOW`; the message becomes eligible automatically |
| **Channel overflow** (burst > 10,000 in-flight) | The dropped IDs are still in the DB; cold path catches them within 1 second |
| **Lock expiry recovery** | `ReleaseExpiredLocksAsync` (throttled to once per 30 s) resets any message whose lock expired without being processed |

Before the cold path queries the database, it snapshots the current `_hotInFlight` set and serialises it as JSON. The `LockNextBatchAsync` SQL includes:

```sql
AND m.[Id] NOT IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@skipJson))
```

This prevents the cold path from even attempting to lock a row that the hot path is currently processing, eliminating the intra-process race entirely. The `WITH (UPDLOCK, READPAST)` hint handles the cross-instance case: if the hot path on another instance holds an X lock on a row, `READPAST` skips it rather than blocking.

#### How retries interact with both paths

Retries **never** go through the hot path. When delivery fails:

```
Delivery fails
    │
    └─► IncrementRetryAsync(nextRetryAt = now + backoff)
             Sets Status=Pending, NextRetryAt=<future>, clears lock

                      ↓  (cold path, n seconds later)

    LockNextBatchAsync: WHERE NextRetryAt <= SYSDATETIMEOFFSET()
             Row becomes eligible once the backoff window expires
```

`Notify(messageId)` is called only at publish time. No signal is sent when a retry is scheduled. The cold path is the sole path for retries, and the `NextRetryAt` guard in the SQL is the scheduling mechanism.

---

### Multi-Instance Architecture

In a real deployment you typically run multiple API replicas (for scale) and one or more Azure Functions (for serverless processing). All share the same SQL Server database. InboxNet is designed for this topology from the ground up.

```
                              ┌────────────────────────────┐
                              │        SQL Server DB        │
                              │                             │
                              │  OutboxMessages             │
                              │  ┌────────────────────────┐ │
                              │  │ Id  │Status│LockedBy   │ │
                              │  │ ... │Pendng│           │ │
                              │  │ ... │Procsg│api-inst-1 │ │
                              │  │ ... │Pendng│           │ │
                              │  │ ... │Procsg│api-inst-2 │ │
                              │  └────────────────────────┘ │
                              │  WebhookSubscriptions        │
                              │  DeliveryAttempts            │
                              └──────────┬─────────────────┘
                                         │
              ┌──────────────────────────┼──────────────────────────────┐
              │                          │                              │
              ▼                          ▼                              ▼
  ┌────────────────────┐     ┌────────────────────┐       ┌───────────────────────┐
  │  ASP.NET Core API   │     │  ASP.NET Core API   │       │   Azure Functions     │
  │  Instance 1        │     │  Instance 2        │       │                       │
  │                    │     │                    │       │  ┌─────────────────┐  │
  │  ► Produces:       │     │  ► Produces:       │       │  │ Timer Trigger   │  │
  │    IOutboxPublisher│     │    IOutboxPublisher│       │  │ (every 30 s)    │  │
  │    (per request)   │     │    (per request)   │       │  │                 │  │
  │                    │     │                    │       │  │ ProcessBatchAsync│  │
  │  ► Consumes:       │     │  ► Consumes:       │       │  │ (cold path only)│  │
  │    Hot path loop   │     │    Hot path loop   │       │  └─────────────────┘  │
  │    Cold path loop  │     │    Cold path loop  │       │                       │
  │                    │     │                    │       │  ► Also Produces:     │
  │  InstanceId=       │     │  InstanceId=       │       │    IOutboxPublisher   │
  │  "api-inst-1"      │     │  "api-inst-2"      │       │    (from queue msgs)  │
  └────────┬───────────┘     └──────────┬─────────┘       └──────────┬────────────┘
           │                            │                             │
           │ Signals own channel only   │ Signals own channel only    │ No hot path
           │ (in-process, per instance) │ (in-process, per instance)  │ (timer-based)
           │                            │                             │
           └────────────────────────────┴─────────────────────────────┘
                                        │
                              All query same DB.
                    DB lock gate (UPDATE WHERE Status=Pending)
                    ensures exactly one instance wins per message.
                              │
                              ▼
                    ┌──────────────────────┐
                    │   External Services   │
                    │   (webhook receivers) │
                    └──────────────────────┘
```

#### How each instance type behaves

**ASP.NET Core API instances (run both paths)**

Each instance runs `OutboxProcessorService` as a `BackgroundService`. When Instance 1 publishes a message, its `Channel<Guid>` receives the ID immediately — the hot path attempts `TryLockByIdAsync` in under 1 ms. Instance 2's cold path will see the same message in its next batch scan, but by then `Status=Processing` and `LockedUntil` is in the future, so `UPDLOCK+READPAST` skips it. No duplicate delivery.

When Instance 2 publishes a message, the same logic applies in reverse. Its hot path wins the lock; Instance 1's cold path skips the row.

**Azure Functions (cold path only)**

The `OutboxTimerFunction` calls `ProcessBatchAsync` on each timer firing with no `skipIds` (there is no hot path in Functions). Multiple Function instances can fire simultaneously — the `WITH (UPDLOCK, READPAST)` CTE ensures each wins a disjoint subset of rows. Lock ownership is tracked by `LockedBy` (the `InstanceId`), so `MarkAsProcessedAsync` only succeeds for the instance that holds the lock.

Azure Functions can also **produce** outbox messages (e.g., messages triggered by Service Bus or Queue events). `IOutboxPublisher` works identically — wraps the INSERT in the active transaction and calls `Notify()`. Since Functions have no hot-path loop, the `Notify()` is effectively a no-op (the channel is internal to `ChannelOutboxSignal` but nothing drains it). The next timer firing picks up the message via the cold path.

#### Lock ownership and visibility timeout

Every locked row carries `LockedBy = InstanceId` and `LockedUntil = NOW + DefaultVisibilityTimeout`. All terminal operations (`MarkAsProcessedAsync`, `IncrementRetryAsync`, `MarkAsDeadLetteredAsync`) include `WHERE LockedBy = @lockedBy`. If an instance crashes mid-delivery, the lock expires after `DefaultVisibilityTimeout` and `ReleaseExpiredLocksAsync` resets the row to `Status=Pending` for re-processing by any surviving instance.

```
  Instance crashes during delivery
          │
          └── LockedUntil expires (default: 5 min)
                    │
                    └── ReleaseExpiredLocksAsync (cold path, runs every 30 s)
                              SET Status=Pending, LockedUntil=NULL, LockedBy=NULL
                                        │
                                        └── Any instance picks it up on next cold scan
```

Note: lock expiry intentionally does **not** increment `RetryCount`. An expired lock means infrastructure failure (crash, OOM, kill), not a delivery failure. Counting it against the retry budget would dead-letter healthy messages under transient pod restarts.

#### What you get end-to-end

| Scenario | Typical latency | Mechanism |
|---|---|---|
| Message published on the **same instance** that processes it | < 5 ms | Hot path Channel |
| Message published on **instance A**, processed by **instance B** | ≤ `ColdPollingInterval` (1 s default) | Cold path batch scan |
| Message published by **Azure Functions**, processed by Functions | ≤ timer interval (e.g. 30 s) | Timer trigger cold path |
| **Retry** after delivery failure | `RetryPolicy.GetNextDelay(retryCount)` | Cold path once `NextRetryAt` passes |
| **Lock expiry recovery** after crash | ≤ `DefaultVisibilityTimeout` + 30 s | `ReleaseExpiredLocksAsync` + cold path |

---

### Processing Pipeline (per message)

Once a message is locked (by either path), the pipeline is identical:

```
  Locked OutboxMessage
        │
        ▼
  1. Subscription pre-fetch
     GetForMessageAsync(message)
     One query per unique (EventType, TenantId) in the batch — cached for the
     batch duration; N messages with the same routing key = 1 DB query.
        │
        ▼
  2. Delivery state pre-fetch
     GetDeliveryStatesAsync(messageId, subscriptionIds)
     Single OPENJSON query returns (AttemptCount, HasSuccess) per subscription.
     Already-succeeded subscriptions are skipped — safe to retry without
     re-delivering to endpoints that already acknowledged.
        │
        ▼
  3. Parallel delivery  (Parallel.ForEachAsync, DOP = MaxConcurrentSubscriptionDeliveries)
     For each subscription not yet succeeded and not exhausted:
       ├─ DeliveryId = SHA256(MessageId ‖ SubscriptionId ‖ AttemptNumber)
       │    Deterministic — same (message, subscription, attempt) always yields
       │    the same ID. Receivers use X-Outbox-Delivery-Id as idempotency key.
       ├─ HTTP POST with HMAC-SHA256 signature + outbox headers
       └─ Record DeliveryAttempt (success/fail, status code, duration, error)
        │
        ▼
  4. Batch bookkeeping
     SaveAttemptsAsync([all new DeliveryAttempt records])
     Single INSERT for all subscriptions in one round-trip.

     ⚠ If this fails after a successful delivery:
        Do NOT retry immediately — that risks duplicate delivery.
        Log CRITICAL, leave the lock in place.
        Lock expires → message requeued → next attempt reads HasSuccess=true
        from any partial records that did save → skips already-delivered subs.
        Webhook consumers MUST be idempotent on X-Outbox-Message-Id.
        │
        ▼
  5. Decision
     ├─ All succeeded (or previously succeeded):  MarkAsProcessed
     ├─ Any failed:                               IncrementRetry + backoff
     │                                            (cold path picks up after NextRetryAt)
     └─ All exhausted, none succeeded:            MarkAsDeadLettered
```

## Configuration Reference

### `AddInboxNet(options => ...)` — core options

```csharp
builder.Services.AddInboxNet(options =>
{
    // Database schema for outbox tables. Default: "outbox"
    options.SchemaName = "outbox";

    // Messages locked per processing cycle. Default: 50
    options.BatchSize = 50;

    // How long a message is locked before being eligible for re-processing.
    // Must exceed worst-case time to deliver one full batch.
    // Default: 5 minutes
    options.DefaultVisibilityTimeout = TimeSpan.FromMinutes(5);

    // Unique identifier for this processor instance (for lock ownership).
    // Default: "{MachineName}-{Guid}" — auto-generated, usually leave as default.
    options.InstanceId = "my-instance-1";

    // Max messages processed concurrently within a batch. Default: 10
    options.MaxConcurrentDeliveries = 10;

    // Max subscriptions delivered concurrently per message. Default: 4
    // Fanout = BatchSize × MaxConcurrentDeliveries × MaxConcurrentSubscriptionDeliveries
    // Keep fanout under ~200 to avoid connection pool exhaustion.
    options.MaxConcurrentSubscriptionDeliveries = 4;

    // DirectDelivery (default): processor calls webhook directly.
    // QueueMediated: processor publishes to IMessagePublisher (e.g. Azure Storage Queue).
    options.ProcessingMode = ProcessingMode.DirectDelivery;

    // Enforce causal ordering within a (TenantId, UserId, EntityId) partition.
    // Default: true — a partition's messages are processed in creation order.
    options.EnableOrderedProcessing = true;

    // Optional: only process messages for this tenant (for sharded multi-instance deployments).
    // Default: null (process all tenants).
    options.TenantFilter = "tenant-a";
});
```

### `.AddBackgroundProcessor(options => ...)` — polling behavior

```csharp
.AddBackgroundProcessor(options =>
{
    // How often the cold path scans the database for messages that were
    // published by other instances, scheduled retries, or dropped hot-path hints.
    // The hot path delivers same-instance messages immediately via Channel<Guid>
    // with no polling delay at all.
    // Default: 1 second. Lower values improve cross-instance latency at the cost
    // of one additional lightweight indexed scan per interval per instance.
    options.ColdPollingInterval = TimeSpan.FromSeconds(1);
});
```

### `.AddWebhookDelivery(options => ...)` — HTTP delivery and retry

```csharp
.AddWebhookDelivery(options =>
{
    // Global HTTP client timeout. Default: 30 seconds
    options.HttpTimeout = TimeSpan.FromSeconds(30);

    // Retry policy (applied to the global retry counter on OutboxMessage,
    // separate from per-subscription MaxRetries).
    options.Retry.MaxRetries = 5;
    options.Retry.BaseDelay = TimeSpan.FromSeconds(5);
    options.Retry.MaxDelay = TimeSpan.FromMinutes(5);
    options.Retry.JitterFactor = 0.2; // ±20% jitter
});
```

### `.UseHttpContextAccessor(options => ...)` — ambient tenant/user context

Extracts `TenantId` and `UserId` from the current HTTP request's claims and makes them available to the publisher. Required for automatic per-tenant partitioning and routing.

```csharp
.UseHttpContextAccessor(options =>
{
    options.TenantIdClaimType = "tid";   // claim type for TenantId
    options.UserIdClaimType   = "sub";   // claim type for UserId
});
```

### `.UseTenantSecretRetriever(options => ...)` — per-tenant HMAC secrets

Resolves per-tenant webhook secrets from `IConfiguration` at delivery time. Because `IConfiguration` is provider-agnostic, this works transparently with Azure Key Vault, AWS Secrets Manager, environment variables, or `appsettings.json`.

```csharp
.UseTenantSecretRetriever(options =>
{
    // Key pattern for IConfiguration lookup. {tenantId} is replaced at runtime.
    // When Azure Key Vault is configured, Key Vault secrets are auto-resolved.
    // Default: "Outbox:Secrets:{tenantId}:WebhookSecret"
    options.KeyPattern = "Outbox:Secrets:{tenantId}:WebhookSecret";

    // Cache duration for resolved secrets. TimeSpan.Zero disables caching.
    // Default: 5 minutes
    options.SecretCacheTtl = TimeSpan.FromMinutes(5);
});
```

Or plug in a custom retriever:

```csharp
.UseTenantSecretRetriever<MyVaultSecretRetriever>();
```

## Advanced Usage

### Multi-tenant setup

```csharp
builder.Services
    .AddInboxNet()
    .UseSqlServerContext<AppDbContext>(connectionString)
    .UseHttpContextAccessor(opts =>
    {
        opts.TenantIdClaimType = "tid";
        opts.UserIdClaimType   = "sub";
    })
    .UseTenantSecretRetriever(opts =>
    {
        opts.KeyPattern = "Outbox:Secrets:{tenantId}:WebhookSecret";
    })
    .AddBackgroundProcessor()
    .AddWebhookDelivery();
```

With `UseHttpContextAccessor`, every call to `PublishAsync` automatically stamps `TenantId` and `UserId` onto the outbox message from the current HTTP request's claims. The processor then routes each message to the correct per-tenant subscription and signs with the per-tenant secret.

### Publishing with partition key for ordered processing

```csharp
await _outbox.PublishAsync(
    eventType: "order.updated",
    payload: new { orderId, status },
    entityId: orderId.ToString(),  // all events for the same order processed in order
    cancellationToken: ct);
```

When `EnableOrderedProcessing = true` (default), messages with the same `(TenantId, UserId, EntityId)` are processed in creation order using a `NOT EXISTS` SQL guard in `LockNextBatchAsync`.

### Custom subscription reader

Implement `ISubscriptionReader` to route messages from any source (database, service registry, feature flag, etc.):

```csharp
public class MyCustomSubscriptionReader : ISubscriptionReader
{
    public Task<IReadOnlyList<WebhookSubscription>> GetForMessageAsync(
        OutboxMessage message, CancellationToken ct)
    {
        // Return subscriptions for this message's event type / tenant.
    }
}

builder.Services.AddSingleton<ISubscriptionReader, MyCustomSubscriptionReader>();
```

### Custom retry policy

```csharp
public class LinearRetryPolicy : IRetryPolicy
{
    public TimeSpan? GetNextDelay(int retryCount) =>
        retryCount < 10 ? TimeSpan.FromSeconds(30) : null; // null = dead-letter
}

builder.Services.AddSingleton<IRetryPolicy, LinearRetryPolicy>();
```

### Azure Storage Queue (queue-mediated mode)

```csharp
builder.Services
    .AddInboxNet(opts => opts.ProcessingMode = ProcessingMode.QueueMediated)
    .UseSqlServerContext<AppDbContext>(connectionString)
    .UseAzureStorageQueue(opts =>
    {
        opts.ConnectionString = storageConnectionString;
        opts.QueueName = "outbox-messages";
    })
    .AddBackgroundProcessor()
    .AddWebhookDelivery();
```

In queue-mediated mode the processor publishes locked messages to Azure Storage Queue rather than delivering webhooks directly. A separate consumer (e.g., another Azure Functions instance) reads from the queue and handles delivery.

## Observability

InboxNet emits OpenTelemetry signals out of the box.

### Traces

Register the activity source in your telemetry setup:
```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("InboxNet"));
```

Activities emitted: `outbox.publish`, `outbox.process_batch`, `outbox.deliver_webhook`.

### Metrics

Register the meter:
```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("InboxNet"));
```

| Metric | Type | Tags |
|---|---|---|
| `outbox.messages.published` | Counter | `event_type` |
| `outbox.messages.processed` | Counter | `event_type` |
| `outbox.messages.failed` | Counter | `event_type` |
| `outbox.messages.dead_lettered` | Counter | `event_type` |
| `outbox.delivery.attempts` | Counter | `event_type` |
| `outbox.delivery.successes` | Counter | `event_type` |
| `outbox.delivery.failures` | Counter | `event_type` |
| `outbox.delivery.duration_ms` | Histogram | `event_type` |
| `outbox.batches.processed` | Counter | — |
| `outbox.batch.size` | Histogram | — |
| `outbox.processing.duration_ms` | Histogram | — |

## Which SQL Server Package?

| If you... | Use |
|---|---|
| Already use EF Core and want migrations + DbContext integration | `InboxNet.EntityFrameworkCore` |
| Use Dapper, raw ADO.NET, or want zero EF Core overhead | `InboxNet.SqlServer` |
| Need outbox writes in the same transaction as your EF Core `SaveChangesAsync` | `InboxNet.EntityFrameworkCore` |
| Need outbox writes in the same transaction as a raw `SqlTransaction` | `InboxNet.SqlServer` |

## Project Structure

```
InboxNet/
├── src/
│   ├── InboxNet.Core/                    # Contracts, models, options, observability
│   ├── InboxNet.EntityFrameworkCore/     # EF Core + SQL Server stores & publisher
│   ├── InboxNet.SqlServer/               # Direct ADO.NET SQL Server stores & publisher
│   ├── InboxNet.Processor/               # Background processing hosted service
│   ├── InboxNet.Delivery/                # HTTP webhook delivery + HMAC + retry
│   ├── InboxNet.AzureStorageQueue/       # Azure Storage Queue transport
│   └── InboxNet.AzureFunctions/          # Azure Functions timer trigger
├── tests/
│   ├── InboxNet.Core.Tests/
│   ├── InboxNet.Delivery.Tests/
│   └── InboxNet.Processor.Tests/
├── InboxNet.SampleApp/                   # Full ASP.NET Core sample application
├── Directory.Build.props                  # Shared build + NuGet package properties
├── Directory.Packages.props               # Centralized package version management
└── .github/workflows/
    ├── ci.yml                             # Build + test on every push/PR
    └── publish.yml                        # Publish to NuGet on GitHub release
```

## Publishing to NuGet

### Automated (GitHub Actions)

1. Add your NuGet API key as a repository secret named `NUGET_API_KEY` (Settings → Secrets → Actions)
2. Create a GitHub release with a version tag (e.g. `1.0.0` or `1.2.0-preview.1`)
3. The workflow builds, tests, packs all packages with the release tag as the version, and pushes to nuget.org

### Manual

```bash
dotnet pack -c Release -o ./nupkgs /p:Version=1.0.0
dotnet nuget push ./nupkgs/*.nupkg \
    --api-key YOUR_API_KEY \
    --source https://api.nuget.org/v3/index.json \
    --skip-duplicate
```

The version is controlled by `<Version>` in `Directory.Build.props`. All packages share the same version.

## License

[MIT](LICENSE)
