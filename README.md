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
    └── PingPong.Tests/             # xUnit + Aspire testing + OTLP collector
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

## Test Flow

```csharp
[Fact]
public async Task Ping_message_should_be_processed()
{
    // Arrange: OTLP collector already running, subscribed to SpanReceived event
    // with TaskCompletionSource awaiting "process message" span for PingMessage

    // Act: Sender automatically sends PingMessage on startup

    // Assert: Await TCS (30s timeout), verify span completed successfully
}
```

## Prerequisites

- .NET 9.0 SDK
- Aspire workload (`dotnet workload install aspire`)

## Running the Tests

```bash
dotnet test
```

## Key Implementation Details

### ServiceDefaults (OTLP Export)

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("NServiceBus.Core");
        tracing.AddOtlpExporter();
    });
```

### Test OTLP Collector

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
- Attribute: `nservicebus.enclosed_message_types` contains `"PingMessage"`
- Status: Not error

## What This Proves

That we can use Aspire's OTLP telemetry pipeline to subscribe to NServiceBus ActivitySource events across process boundaries, enabling reliable end-to-end test assertions without:
- Arbitrary `Task.Delay` calls
- External telemetry systems (Jaeger, Application Insights, etc.)
- Flaky timing-based assertions
