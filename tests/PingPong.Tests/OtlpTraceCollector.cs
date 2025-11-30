using Grpc.Core;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace PingPong.Tests;

/// <summary>
/// A gRPC service that receives OTLP trace data and allows tests to subscribe to span events.
/// This is a minimal in-memory collector that bridges the gap between Aspire's multi-process
/// architecture and our need to assert on NServiceBus activity completion.
/// </summary>
public class OtlpTraceCollector : TraceService.TraceServiceBase
{
    private readonly List<Span> _receivedSpans = new();
    private readonly object _lock = new();

    public event Action<Span>? SpanReceived;

    public IReadOnlyList<Span> ReceivedSpans
    {
        get
        {
            lock (_lock)
            {
                return _receivedSpans.ToList();
            }
        }
    }

    public override Task<ExportTraceServiceResponse> Export(
        ExportTraceServiceRequest request,
        ServerCallContext context)
    {
        foreach (var resourceSpans in request.ResourceSpans)
        {
            foreach (var scopeSpans in resourceSpans.ScopeSpans)
            {
                foreach (var span in scopeSpans.Spans)
                {
                    lock (_lock)
                    {
                        _receivedSpans.Add(span);
                    }

                    SpanReceived?.Invoke(span);
                }
            }
        }

        return Task.FromResult(new ExportTraceServiceResponse());
    }
}
