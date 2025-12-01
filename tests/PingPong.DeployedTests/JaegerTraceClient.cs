using System.Net.Http.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace PingPong.DeployedTests;

/// <summary>
/// Client for querying traces from Jaeger's HTTP API.
/// </summary>
public class JaegerTraceClient : ITraceQueryClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Creates a new Jaeger trace client.
    /// </summary>
    /// <param name="jaegerQueryUrl">Base URL of Jaeger Query service (e.g., http://localhost:16686 or http://jaeger-query.observability)</param>
    public JaegerTraceClient(string jaegerQueryUrl)
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(jaegerQueryUrl.TrimEnd('/')) };
        _ownsHttpClient = true;
    }

    /// <summary>
    /// Creates a new Jaeger trace client with a provided HttpClient.
    /// </summary>
    public JaegerTraceClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _ownsHttpClient = false;
    }

    public async Task<IReadOnlyList<Span>> GetTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"/api/traces/{traceId}", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Trace not found or error - return empty list
            return Array.Empty<Span>();
        }

        var jaegerResponse = await response.Content.ReadFromJsonAsync<JaegerTraceResponse>(cancellationToken: cancellationToken);

        if (jaegerResponse?.Data == null || jaegerResponse.Data.Count == 0)
        {
            return Array.Empty<Span>();
        }

        // Convert Jaeger spans to OpenTelemetry spans
        var spans = new List<Span>();
        foreach (var trace in jaegerResponse.Data)
        {
            if (trace.Spans == null) continue;
            foreach (var jaegerSpan in trace.Spans)
            {
                spans.Add(ConvertToOtelSpan(jaegerSpan, traceId));
            }
        }

        return spans;
    }

    private static Span ConvertToOtelSpan(JaegerSpan jaegerSpan, string traceId)
    {
        var span = new Span
        {
            TraceId = ByteString.CopyFrom(Convert.FromHexString(traceId)),
            SpanId = ByteString.CopyFrom(Convert.FromHexString(jaegerSpan.SpanId)),
            Name = jaegerSpan.OperationName,
            StartTimeUnixNano = (ulong)(jaegerSpan.StartTime * 1000), // microseconds to nanoseconds
            EndTimeUnixNano = (ulong)((jaegerSpan.StartTime + jaegerSpan.Duration) * 1000),
        };

        // Convert tags to attributes
        if (jaegerSpan.Tags != null)
        {
            foreach (var tag in jaegerSpan.Tags)
            {
                var attr = new KeyValue { Key = tag.Key };
                attr.Value = tag.Type?.ToLowerInvariant() switch
                {
                    "bool" => new AnyValue { BoolValue = tag.Value?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false },
                    "int64" => new AnyValue { IntValue = long.TryParse(tag.Value?.ToString(), out var l) ? l : 0 },
                    "float64" => new AnyValue { DoubleValue = double.TryParse(tag.Value?.ToString(), out var d) ? d : 0 },
                    _ => new AnyValue { StringValue = tag.Value?.ToString() ?? string.Empty }
                };
                span.Attributes.Add(attr);
            }
        }

        // Map error tag to status
        var errorTag = jaegerSpan.Tags?.FirstOrDefault(t => t.Key == "error");
        if (errorTag?.Value?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
        {
            span.Status = new Status { Code = Status.Types.StatusCode.Error };
        }

        // Map otel.status_code if present
        var statusCodeTag = jaegerSpan.Tags?.FirstOrDefault(t => t.Key == "otel.status_code");
        if (statusCodeTag?.Value != null)
        {
            var statusStr = statusCodeTag.Value.ToString()?.ToUpperInvariant();
            span.Status = statusStr switch
            {
                "ERROR" => new Status { Code = Status.Types.StatusCode.Error },
                "OK" => new Status { Code = Status.Types.StatusCode.Ok },
                _ => new Status { Code = Status.Types.StatusCode.Unset }
            };
        }

        return span;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    // Jaeger API response models
    private class JaegerTraceResponse
    {
        public List<JaegerTrace>? Data { get; set; }
    }

    private class JaegerTrace
    {
        public string? TraceId { get; set; }
        public List<JaegerSpan>? Spans { get; set; }
    }

    private class JaegerSpan
    {
        public string SpanId { get; set; } = string.Empty;
        public string OperationName { get; set; } = string.Empty;
        public long StartTime { get; set; }
        public long Duration { get; set; }
        public List<JaegerTag>? Tags { get; set; }
    }

    private class JaegerTag
    {
        public string Key { get; set; } = string.Empty;
        public string? Type { get; set; }
        public object? Value { get; set; }
    }
}
