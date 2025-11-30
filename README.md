# Aspire E2E Testing POC: NServiceBus ActivitySource Subscription

## Executive Summary

This POC demonstrates a **reliable, non-flaky approach to end-to-end testing** of distributed microservices that communicate via messaging (NServiceBus). Instead of polling the database hoping data has arrived, we **subscribe to the actual message processing events** emitted by NServiceBus through OpenTelemetry, allowing tests to assert **at exactly the right moment**.

### The Problem We Solve

In distributed systems with asynchronous messaging, a common testing challenge is: **"How do I know when a message has been fully processed before I assert?"**

Traditional approaches have significant drawbacks:

| Approach | Problem |
|----------|---------|
| `Task.Delay(5000)` | Arbitrary wait times. Too short = flaky tests. Too long = slow CI/CD. |
| Database polling with retry | Race conditions, complex retry logic, still potentially flaky under load. |
| In-memory test doubles | Doesn't test real infrastructure, misses integration issues. |

### Our Solution

We leverage **OpenTelemetry (OTLP)** - the same telemetry pipeline used in production for observability - to receive **real-time notifications** when messages are processed:

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              TEST PROCESS                                       │
│  ┌─────────────────┐                           ┌─────────────────────────────┐  │
│  │   Test Code     │                           │   OTLP Collector (gRPC)     │  │
│  │                 │                           │                             │  │
│  │  1. Send GraphQL│                           │  3. Receives "process       │  │
│  │     request     │                           │     message" span from      │  │
│  │                 │                           │     NServiceBus             │  │
│  │  4. Assert DB   │◄──── Signal ───────────── │                             │  │
│  │     (now safe!) │   (TaskCompletionSource)  │  "PongMessage processed!"   │  │
│  └────────┬────────┘                           └──────────────▲──────────────┘  │
│           │                                                   │                 │
└───────────┼───────────────────────────────────────────────────┼─────────────────┘
            │ GraphQL                                            │ OTLP/gRPC
            ▼                                                   │
┌───────────────────────────────────────────────────────────────┼─────────────────┐
│                         ASPIRE-MANAGED PROCESSES              │                 │
│                                                               │                 │
│  ┌─────────┐        ┌────────────────┐        ┌───────────────┴──┐              │
│  │   API   │──Msg──►│ PingServiceBus │──Msg──►│  PongServiceBus  │              │
│  └─────────┘        └───────┬────────┘        └────────┬─────────┘              │
│                             │                          │                        │
│                             ▼                          ▼                        │
│                        ┌────────────────────────────────────┐                   │
│                        │           SQL Server               │                   │
│                        └────────────────────────────────────┘                   │
└─────────────────────────────────────────────────────────────────────────────────┘
```

**Key insight**: NServiceBus emits OpenTelemetry spans for every message it processes. We intercept these spans in-process and use them as **synchronization signals** for our tests.

---

## Why This Approach Avoids Flakiness

### The Flaky Database Polling Approach

```csharp
// ❌ FLAKY: Race condition between message processing and assertion
await SendGraphQLMutation("mutation { sendPing { id } }");

// How long should we wait? Nobody knows!
await Task.Delay(2000); // Too short on slow CI? Too long wastes time.

// Or polling - complex and still racy
for (int i = 0; i < 10; i++)
{
    var ping = await db.Pings.FindAsync(pingId);
    if (ping != null) break;
    await Task.Delay(500); // Still arbitrary!
}
```

**Problems:**
1. Message processing time varies (network latency, CPU load, database contention)
2. CI/CD environments are slower and more variable than dev machines
3. Under load, delays compound unpredictably
4. You're guessing when processing completes

### The Reliable OTLP Approach

```csharp
// ✅ RELIABLE: Wait for the actual processing event
await SendGraphQLMutation("mutation { sendPing { id } }");

// Wait for NServiceBus to tell us "I finished processing PongMessage"
await WaitForMessageProcessed<PongMessage>(timeout: TimeSpan.FromSeconds(60));

// NOW we can safely assert - processing is guaranteed complete
var pong = await db.Pongs.FindAsync(pongId);
Assert.NotNull(pong);
```

**Why this works:**
1. **Event-driven, not time-based**: We wait for the *actual event* (message processed), not an arbitrary duration
2. **Deterministic**: The signal comes *after* the handler commits to the database
3. **Self-adjusting**: Fast systems complete quickly; slow systems just take longer (up to timeout)
4. **Observable**: On timeout, we can dump all received spans for debugging

---

## When Do Assertions Happen?

The timing is **precise and deterministic**:

```
Timeline:
─────────────────────────────────────────────────────────────────────────────────►

1. Test sends GraphQL mutation
   │
   ▼
2. API receives request, sends NServiceBus message
   │
   ▼
3. PingServiceBus receives message, processes it, saves to DB, sends PongMessage
   │
   │  ──► OTLP span emitted: "process message" (PingMessage)
   ▼
4. PongServiceBus receives PongMessage, processes it, saves to DB
   │
   │  ──► OTLP span emitted: "process message" (PongMessage)  ◄─── TEST RECEIVES THIS
   ▼
5. Test's TaskCompletionSource is signaled
   │
   ▼
6. Test proceeds to assertions ◄─── DATABASE IS GUARANTEED TO HAVE THE DATA
```

**The span is emitted AFTER the handler completes successfully**, which means:
- The database transaction has been committed
- Any outgoing messages have been sent
- It's safe to query the database

---

## How the OTLP Collector Connects to the System

### Connection Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     TEST PROCESS (xUnit)                        │
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │  AspireFixture (IClassFixture - one per test class)       │  │
│  │                                                           │  │
│  │  1. Starts OTLP gRPC server on random port (e.g., :54321) │  │
│  │  2. Sets OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:54321│  │
│  │  3. Launches Aspire AppHost with this environment var     │  │
│  └───────────────────────────────────────────────────────────┘  │
│                              │                                  │
│                              │ Aspire starts child processes    │
│                              ▼                                  │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │  Child Processes (API, PingServiceBus, PongServiceBus)    │  │
│  │                                                           │  │
│  │  - Inherit OTEL_EXPORTER_OTLP_ENDPOINT from environment   │  │
│  │  - NServiceBus.EnableOpenTelemetry() registers spans      │  │
│  │  - OpenTelemetry SDK exports spans via OTLP/gRPC          │  │
│  │                                                           │  │
│  │         ┌─────────────────────────────────────────┐       │  │
│  │         │  Span: "process message"                │       │  │
│  │         │  Attributes:                            │       │  │
│  │         │    nservicebus.enclosed_message_types:  │       │  │
│  │         │      "PingPong.Messages.PongMessage"    │       │  │
│  │         └─────────────────────────────────────────┘       │  │
│  └───────────────────────────────────────────────────────────┘  │
│                              │                                  │
│                              │ OTLP/gRPC (automatic export)     │
│                              ▼                                  │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │  OtlpTraceCollector (gRPC TraceService implementation)    │  │
│  │                                                           │  │
│  │  - Receives ExportTraceServiceRequest                     │  │
│  │  - Fires SpanReceived event for each span                 │  │
│  │  - Test subscribes and signals TaskCompletionSource       │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

### Key Configuration Points

**1. OTLP Collector Setup (in test fixture):**
```csharp
// Start gRPC server on random available port
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, 0, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2; // Required for gRPC
    });
});
builder.Services.AddGrpc();
builder.Services.AddSingleton(collector);
app.MapGrpcService<OtlpTraceCollector>();
```

**2. Passing OTLP Endpoint to Aspire:**
```csharp
var appHost = await DistributedApplicationTestingBuilder
    .CreateAsync<TAppHost>([$"OTEL_EXPORTER_OTLP_ENDPOINT={otlpEndpoint}"]);
```

**3. ServiceDefaults Configuration (in each service):**
```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("NServiceBus.Core");  // Subscribe to NServiceBus spans
        tracing.AddOtlpExporter();              // Export to OTEL_EXPORTER_OTLP_ENDPOINT
    });
```

**4. NServiceBus OpenTelemetry Integration:**
```csharp
endpointConfiguration.EnableOpenTelemetry();  // Emit spans for message processing
```

---

## Project Structure

```
PingPong.sln
├── src/
│   ├── PingPong.Messages/          # Messages: PingMessage, PongMessage, UpdatePingMessage, PingUpdatedMessage
│   ├── PingPong.Data/              # EF Core DbContext, Ping & Pong entities (with UpdatedAt)
│   ├── PingPong.ServiceDefaults/   # Shared OpenTelemetry + OTLP exporter config
│   ├── PingPong.Api/               # GraphQL API (HotChocolate), send-only NServiceBus
│   ├── PingPong.PingServiceBus/    # Handles PingMessage/UpdatePingMessage, sends Pong/PingUpdated
│   ├── PingPong.PongServiceBus/    # Handles PongMessage/PingUpdatedMessage
│   └── PingPong.AppHost/           # Aspire orchestrator with SQL Server container
└── tests/
    └── PingPong.Tests/
        ├── EndToEndTest.cs         # Base class with OTLP integration + helper methods
        ├── PingPongEndToEndTests.cs # 3 E2E tests demonstrating the pattern
        └── OtlpTraceCollector.cs   # gRPC OTLP receiver implementation
```

---

## Message Flows

### Flow 1: Create Ping → Pong

```
┌─────────┐   GraphQL    ┌────────────────┐                ┌────────────────┐
│   API   │──mutation───►│ PingServiceBus │───PongMessage─►│ PongServiceBus │
│(send-   │  "sendPing"  │                │                │                │
│ only)   │              │  stores Ping   │                │  stores Pong   │
└─────────┘              └────────────────┘                └────────────────┘
                                │                                  │
                                └──────────┐    ┌──────────────────┘
                                           ▼    ▼
                                      ┌──────────────┐
                                      │  SQL Server  │
                                      └──────────────┘
```

### Flow 2: Update Ping → Update Pong

```
┌─────────┐  GraphQL      ┌────────────────┐                     ┌────────────────┐
│   API   │──mutation────►│ PingServiceBus │──PingUpdatedMsg───►│ PongServiceBus │
│(send-   │ "updatePing"  │                │                     │                │
│ only)   │               │ updates Ping   │                     │ updates Pong   │
└─────────┘               │ (UpdatedAt)    │                     │ (UpdatedAt)    │
                          └────────────────┘                     └────────────────┘
```

---

## Writing E2E Tests

Inherit from `EndToEndTest<TAppHost>` and use the provided helper methods:

```csharp
public sealed class PingPongEndToEndTests : EndToEndTest<Projects.PingPong_AppHost>
{
    public PingPongEndToEndTests(AspireFixture<Projects.PingPong_AppHost> fixture)
        : base(fixture) { }

    [Fact]
    public async Task Ping_sent_via_graphql_should_be_stored_in_database()
    {
        // Arrange
        var mutation = "mutation { sendPing { id sentAt } }";
        var timeout = TimeSpan.FromSeconds(30);

        // Act - sends mutation AND waits for PingMessage to be processed
        var pingId = await SendMutationAndWaitForMessage<PingMessage, Guid>(
            mutation,
            mutationResult => mutationResult.GetProperty("sendPing").GetProperty("id").GetGuid(),
            timeout);

        // Assert - safe to query DB because we waited for processing to complete
        await using var db = CreateDbContext();
        var ping = await db.Pings.FindAsync(pingId);
        Assert.NotNull(ping);
        Assert.NotNull(ping.ReceivedAt);
    }

    [Fact]
    public async Task Ping_pong_full_flow_should_store_pong_in_database()
    {
        // Arrange
        var mutation = "mutation { sendPing { id sentAt } }";
        var timeout = TimeSpan.FromSeconds(60);

        // Act - wait for PongMessage (the LAST message in the chain)
        var pingId = await SendMutationAndWaitForMessage<PongMessage, Guid>(
            mutation,
            mutationResult => mutationResult.GetProperty("sendPing").GetProperty("id").GetGuid(),
            timeout);

        // Assert - entire flow is complete
        await using var db = CreateDbContext();
        var pong = await db.Pongs.FirstOrDefaultAsync(p => p.PingId == pingId);
        Assert.NotNull(pong);
        Assert.NotNull(pong.ReceivedAt);
    }

    [Fact]
    public async Task Update_ping_should_update_both_ping_and_pong()
    {
        // Arrange - seed existing data
        var pingId = Guid.NewGuid();
        var pongId = Guid.NewGuid();

        await using (var db = CreateDbContext())
        {
            db.Pings.Add(new Ping { Id = pingId, SentAt = DateTime.UtcNow.AddMinutes(-5), ReceivedAt = DateTime.UtcNow.AddMinutes(-4) });
            db.Pongs.Add(new Pong { Id = pongId, PingId = pingId, SentAt = DateTime.UtcNow.AddMinutes(-3), ReceivedAt = DateTime.UtcNow.AddMinutes(-2) });
            await db.SaveChangesAsync();
        }

        var mutation = $"mutation {{ updatePing(pingId: \"{pingId}\") {{ pingId sentAt }} }}";
        var timeout = TimeSpan.FromSeconds(60);

        // Act - wait for PingUpdatedMessage (processed by PongServiceBus)
        await SendMutationAndWaitForMessage<PingUpdatedMessage, Guid>(
            mutation,
            mutationResult => mutationResult.GetProperty("updatePing").GetProperty("pingId").GetGuid(),
            timeout);

        // Assert - both entities updated
        await using var dbAssert = CreateDbContext();
        var ping = await dbAssert.Pings.FindAsync(pingId);
        Assert.NotNull(ping?.UpdatedAt);

        var pong = await dbAssert.Pongs.FirstOrDefaultAsync(p => p.PingId == pingId);
        Assert.NotNull(pong?.UpdatedAt);
    }
}
```

### Available Helper Methods

| Method | Description |
|--------|-------------|
| `SendMutationAndWaitForMessage<TMessage, TResult>(...)` | Sends GraphQL mutation, waits for message processing, returns extracted result |
| `SendGraphQLMutation<TResult>(...)` | Sends GraphQL mutation and extracts result (no waiting) |
| `WaitForMessageProcessed<TMessage>(timeout)` | Waits for a specific message type to be processed |
| `WaitForSpan(predicate, timeout)` | Waits for any span matching custom predicate |
| `AssertSpanSucceeded(span)` | Asserts span has no error status |
| `CreateDbContext()` | Creates DbContext for database assertions |
| `CreateHttpClient(resourceName)` | Creates HTTP client for calling Aspire resources |

---

## Prerequisites

- .NET 9.0 SDK
- Aspire workload (`dotnet workload install aspire`)
- Docker (for SQL Server container)

## Running the Tests

```bash
dotnet test
```

Tests complete in ~30 seconds (including SQL Server container startup). Subsequent runs are faster due to container reuse.

---

## Key Implementation Details

### Span Detection Logic

The test identifies successful message processing by matching:
- **Span name**: `"process message"` (emitted by NServiceBus)
- **Attribute**: `nservicebus.enclosed_message_types` contains the message type name
- **Status**: Not error (indicates successful processing)

```csharp
private static bool IsMessageProcessSpan(Span span, string messageTypeName)
{
    if (!span.Name.Equals("process message", StringComparison.OrdinalIgnoreCase))
        return false;

    var hasMatchingMessageType = span.Attributes.Any(a =>
        a.Key.Equals("nservicebus.enclosed_message_types", StringComparison.OrdinalIgnoreCase) &&
        a.Value?.StringValue?.Contains(messageTypeName, StringComparison.OrdinalIgnoreCase) == true);

    var isSuccess = span.Status == null || span.Status.Code != Status.Types.StatusCode.Error;

    return hasMatchingMessageType && isSuccess;
}
```

### Timeout and Diagnostics

If a test times out waiting for a message, it provides diagnostic output showing all spans received during the wait period:

```
Timeout waiting for message 'PongMessage' to be processed.

Received spans:
  - process message [nservicebus.enclosed_message_types=PingPong.Messages.PingMessage]
  - POST /graphql [http.status_code=200]
  - ...
```

This helps identify whether the message was never sent, got stuck, or was processed with a different name.

---

## What This Proves

This POC demonstrates that we can build **reliable, non-flaky E2E tests** for distributed messaging systems by:

1. **Reusing production telemetry infrastructure** (OpenTelemetry) for test synchronization
2. **Eliminating arbitrary delays** - tests complete as fast as the system allows
3. **Providing deterministic assertions** - we assert only after processing is confirmed complete
4. **Maintaining real integration** - tests exercise actual NServiceBus, SQL Server, and process boundaries
5. **Enabling simple test authoring** - the complexity is hidden in the base class

### Business Value

| Stakeholder | Benefit |
|-------------|---------|
| **Developers** | Write tests that don't randomly fail; clear, readable test code |
| **QA** | Reliable test suite that can be trusted; faster feedback on failures |
| **Architects** | Pattern that scales to complex message flows; uses standard OpenTelemetry |
| **DevOps** | Faster CI/CD pipelines (no padding with delays); fewer flaky test investigations |
| **Management** | Higher confidence in releases; reduced time debugging test infrastructure |

---

## Features Demonstrated

- **NServiceBus messaging** with LearningTransport and OpenTelemetry integration
- **Multi-hop message flows** (API → Service A → Service B)
- **Update flows** with seeded data demonstrating entity updates across services
- **GraphQL API** using HotChocolate with mutations (sendPing, updatePing)
- **SQL Server test container** managed by Aspire with automatic lifecycle
- **Shared test fixture** using xUnit IClassFixture for efficient test execution
- **Full E2E assertion safety**: API → Message Processing → Database → Verified
