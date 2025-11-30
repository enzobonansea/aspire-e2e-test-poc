# Aspire E2E Testing POC: NServiceBus ActivitySource Subscription

This POC validates that we can reliably assert when a message has been processed across microservices using Aspire's telemetry pipeline - subscribing to NServiceBus ActivitySource events via OTLP.

## Architecture

```
PingPong.sln
├── src/
│   ├── PingPong.Messages/          # PingMessage & PongMessage : IMessage
│   ├── PingPong.Data/              # EF Core DbContext, Ping & Pong entities
│   ├── PingPong.ServiceDefaults/   # Shared config with OTLP exporter
│   ├── PingPong.Api/               # GraphQL API (HotChocolate), send-only NServiceBus
│   ├── PingPong.PingServiceBus/    # NServiceBus endpoint, handles PingMessage, sends Pong
│   ├── PingPong.PongServiceBus/    # NServiceBus endpoint, handles PongMessage
│   └── PingPong.AppHost/           # Aspire orchestrator with SQL Server container
└── tests/
    └── PingPong.Tests/
        ├── EndToEndTest.cs         # Base class with shared IClassFixture
        ├── PingPongEndToEndTests.cs # Test class with 2 E2E tests
        └── OtlpTraceCollector.cs   # gRPC OTLP receiver
```

## Message Flow

```
┌─────────┐   GraphQL    ┌────────────────┐                ┌────────────────┐
│   API   │──mutation───►│ PingServiceBus │───PongMessage─►│ PongServiceBus │
│(send-   │              │                │                │                │
│ only)   │              │  stores Ping   │                │  stores Pong   │
└─────────┘              └────────────────┘                └────────────────┘
                                │                                  │
                                └──────────┐    ┌──────────────────┘
                                           ▼    ▼
                                      ┌──────────────┐
                                      │  SQL Server  │
                                      │  (pingpongdb)│
                                      └──────────────┘
```

## How It Works

1. **Test fixture starts an in-process OTLP gRPC collector** that receives trace data
2. **Aspire launches API/PingServiceBus/PongServiceBus** as separate processes with OTLP endpoint configured
3. **NServiceBus `EnableOpenTelemetry()`** emits spans to the OTLP exporter
4. **Spans flow from child processes → OTLP collector → test assertions**
5. **Test uses `TaskCompletionSource`** to wait for the "process message" span with message attributes
6. **Shared fixture** keeps Aspire app running across all tests in the class for speed

## Key Success Criteria Met

- **No arbitrary `Task.Delay`** - Uses OTLP + TaskCompletionSource with timeout
- **Uses Aspire's telemetry pipeline** - OTLP exporter in ServiceDefaults
- **Full Ping-Pong flow** - API → PingServiceBus → PongServiceBus with database persistence
- **Shared test infrastructure** - Uses IClassFixture to share Aspire app across tests
- **Fast feedback** - Tests complete in ~30 seconds (including SQL Server container startup)
- **Fails fast on timeout** - 30-60s timeout with diagnostic output showing received spans
- **Easy to write new tests** - Just inherit from `EndToEndTest<TAppHost>`

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

        // Act
        var pingId = await SendMutationAndWaitForMessage<PingMessage, Guid>(
            mutation,
            data => data.GetProperty("sendPing").GetProperty("id").GetGuid(),
            timeout);

        // Assert
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

        // Act
        var pingId = await SendMutationAndWaitForMessage<PongMessage, Guid>(
            mutation,
            data => data.GetProperty("sendPing").GetProperty("id").GetGuid(),
            timeout);

        // Assert
        await using var db = CreateDbContext();
        var pong = await db.Pongs.FirstOrDefaultAsync(p => p.PingId == pingId);
        Assert.NotNull(pong);
        Assert.NotNull(pong.ReceivedAt);
    }
}
```

### Available Methods in EndToEndTest

| Method | Description |
|--------|-------------|
| `SendMutationAndWaitForMessage<TMessage, TResult>(mutation, resultSelector, timeout)` | Sends GraphQL mutation, waits for message processing, returns extracted result |
| `SendGraphQLMutation<TResult>(mutation, resultSelector)` | Sends GraphQL mutation and extracts result using selector |
| `WaitForMessageProcessed<TMessage>(timeout)` | Waits for a message of type `TMessage` to be processed |
| `WaitForSpan(predicate, timeout)` | Waits for any span matching the predicate |
| `AssertSpanSucceeded(span)` | Asserts the span has no error status |
| `CreateDbContext()` | Creates a DbContext for database assertions |
| `CreateHttpClient(resourceName)` | Creates an HTTP client for calling a resource |

## Prerequisites

- .NET 9.0 SDK
- Aspire workload (`dotnet workload install aspire`)
- Docker (for SQL Server container)

## Running the Tests

```bash
dotnet test
```

## Key Implementation Details

### EndToEndTest Base Class with Shared Fixture

The `EndToEndTest<TAppHost>` class uses xUnit's `IClassFixture` pattern:
- **AspireFixture** starts the OTLP collector and Aspire app once per test class
- **Shared infrastructure** means tests run faster (no container restart per test)
- **DbContext factory** for database assertions
- **Span collection** continues throughout all tests in the class

### ServiceDefaults (OTLP Export)

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("NServiceBus.Core");
        tracing.AddOtlpExporter();
    });
```

### OTLP Trace Collector

The test hosts a gRPC server implementing the OTLP TraceService that captures spans:

```csharp
public class OtlpTraceCollector : TraceService.TraceServiceBase
{
    public event Action<Span>? SpanReceived;

    public override Task<ExportTraceServiceResponse> Export(
        ExportTraceServiceRequest request, ServerCallContext context)
    {
        foreach (var span in request.ResourceSpans.SelectMany(...))
        {
            SpanReceived?.Invoke(span);
        }
        return Task.FromResult(new ExportTraceServiceResponse());
    }
}
```

### Span Detection

The test identifies successful message processing by matching:
- Span name: `"process message"`
- Attribute: `nservicebus.enclosed_message_types` contains the message type name
- Status: Not error

## What This Proves

That we can use Aspire's OTLP telemetry pipeline to subscribe to NServiceBus ActivitySource events across process boundaries, enabling reliable end-to-end test assertions without:
- Arbitrary `Task.Delay` calls
- External telemetry systems (Jaeger, Application Insights, etc.)
- Flaky timing-based assertions
- Boilerplate infrastructure code in each test

## Features Demonstrated

- **NServiceBus messaging** with LearningTransport and OpenTelemetry
- **Ping-Pong message flow** with send-only API endpoint and receive-capable services
- **GraphQL API** using HotChocolate with mutations
- **SQL Server test container** with Entity Framework Core
- **Database persistence** for both Ping and Pong messages
- **Shared test fixture** using xUnit IClassFixture for efficient test runs
- **Full E2E flow**: API → PingServiceBus → PongServiceBus → Database → Assertion
