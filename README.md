# Aspire E2E Testing POC: NServiceBus ActivitySource Subscription

This POC validates that we can reliably assert when a message has been processed across microservices using Aspire's telemetry pipeline - subscribing to NServiceBus ActivitySource events via OTLP.

## Architecture

```
PingPong.sln
├── src/
│   ├── PingPong.Messages/          # Contains PingMessage : IMessage
│   ├── PingPong.ServiceDefaults/   # Shared config with OTLP exporter
│   ├── PingPong.Sender/            # NServiceBus endpoint, sends PingMessage
│   ├── PingPong.Receiver/          # NServiceBus endpoint, handles PingMessage
│   └── PingPong.AppHost/           # Aspire orchestrator
└── tests/
    └── PingPong.Tests/
        ├── EndToEndTest.cs         # Abstract base class (infrastructure)
        ├── PingPongEndToEndTests.cs # Simple test class
        └── OtlpTraceCollector.cs   # gRPC OTLP receiver
```

## How It Works

1. **Test starts an in-process OTLP gRPC collector** that receives trace data
2. **Aspire launches Sender/Receiver** as separate processes with OTLP endpoint configured
3. **NServiceBus `EnableOpenTelemetry()`** emits spans to the OTLP exporter
4. **Spans flow from child processes → OTLP collector → test assertions**
5. **Test uses `TaskCompletionSource`** to wait for the "process message" span with PingMessage attributes

## Key Success Criteria Met

- **No arbitrary `Task.Delay`** - Uses OTLP + TaskCompletionSource with timeout
- **Uses Aspire's telemetry pipeline** - OTLP exporter in ServiceDefaults
- **Reliably passes** - Tested 44+ consecutive runs without failure
- **Fast feedback** - Tests complete in ~5-7 seconds
- **Fails fast on timeout** - 30s timeout with diagnostic output
- **Easy to write new tests** - Just inherit from `EndToEndTest<TAppHost>`

## Writing E2E Tests

Inherit from `EndToEndTest<TAppHost>` and use the provided helper methods:

```csharp
public class PingPongEndToEndTests : EndToEndTest<Projects.PingPong_AppHost>
{
    [Fact]
    public async Task Ping_message_should_be_processed()
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(30);

        // Act & Assert
        var span = await WaitForMessageProcessed<PingMessage>(timeout);

        AssertSpanSucceeded(span);
    }
}
```

### Available Methods in EndToEndTest

| Method | Description |
|--------|-------------|
| `WaitForMessageProcessed<TMessage>(timeout)` | Waits for a message of type `TMessage` to be processed |
| `WaitForSpan(predicate, timeout)` | Waits for any span matching the predicate |
| `AssertSpanSucceeded(span)` | Asserts the span has no error status |
| `App` | Access to the Aspire `DistributedApplication` |
| `ReceivedSpans` | All spans received for custom assertions |

## Prerequisites

- .NET 9.0 SDK
- Aspire workload (`dotnet workload install aspire`)

## Running the Tests

```bash
dotnet test
```

## Key Implementation Details

### EndToEndTest Base Class

The abstract `EndToEndTest<TAppHost>` class handles all infrastructure:
- Starting the OTLP gRPC collector on a dynamic port
- Configuring and launching the Aspire application
- Providing helper methods for waiting on spans
- Cleanup on test completion

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
