using OpenTelemetry.Proto.Trace.V1;

namespace PingPong.DeployedTests;

/// <summary>
/// Interface for querying traces from a tracing backend (Jaeger, SigNoz, etc.)
/// </summary>
public interface ITraceQueryClient
{
    /// <summary>
    /// Retrieves all spans for a given trace ID.
    /// </summary>
    /// <param name="traceId">The 32-character hex trace ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of spans belonging to the trace, or empty if not found</returns>
    Task<IReadOnlyList<Span>> GetTraceAsync(string traceId, CancellationToken cancellationToken = default);
}
