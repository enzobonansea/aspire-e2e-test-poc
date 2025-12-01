using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Proto.Trace.V1;
using PingPong.Data;

namespace PingPong.DeployedTests;

/// <summary>
/// Configuration for tests running against deployed K8s environments.
/// </summary>
public class DeployedTestConfiguration
{
    /// <summary>
    /// Base URL of the API service (e.g., https://api.test.example.com)
    /// Environment variable: TEST_API_URL
    /// </summary>
    public required string ApiBaseUrl { get; init; }

    /// <summary>
    /// URL of the Jaeger Query service (e.g., http://jaeger-query.observability:16686)
    /// Environment variable: TEST_JAEGER_URL
    /// </summary>
    public required string JaegerQueryUrl { get; init; }

    /// <summary>
    /// Database connection string for assertions.
    /// Environment variable: TEST_DB_CONNECTION_STRING
    /// </summary>
    public required string DatabaseConnectionString { get; init; }

    /// <summary>
    /// Polling interval when querying traces from the backend.
    /// Default: 500ms
    /// </summary>
    public TimeSpan TracePollingInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Creates configuration from environment variables.
    /// </summary>
    public static DeployedTestConfiguration FromEnvironment()
    {
        return new DeployedTestConfiguration
        {
            ApiBaseUrl = GetRequiredEnvVar("TEST_API_URL"),
            JaegerQueryUrl = GetRequiredEnvVar("TEST_JAEGER_URL"),
            DatabaseConnectionString = GetRequiredEnvVar("TEST_DB_CONNECTION_STRING"),
            TracePollingInterval = TimeSpan.FromMilliseconds(
                int.TryParse(Environment.GetEnvironmentVariable("TEST_TRACE_POLLING_MS"), out var ms) ? ms : 500)
        };
    }

    private static string GetRequiredEnvVar(string name)
    {
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");
    }
}

/// <summary>
/// Shared fixture for tests running against deployed K8s environments.
/// Uses Jaeger (or other trace backend) for span queries instead of local OTLP collector.
/// </summary>
public class DeployedFixture : IAsyncLifetime, IDisposable
{
    private readonly DeployedTestConfiguration _config;
    private HttpClient? _apiClient;
    private JaegerTraceClient? _traceClient;

    public DeployedFixture()
    {
        _config = DeployedTestConfiguration.FromEnvironment();
    }

    public DeployedTestConfiguration Config => _config;
    public HttpClient ApiClient => _apiClient ?? throw new InvalidOperationException("Fixture not initialized");
    public ITraceQueryClient TraceClient => _traceClient ?? throw new InvalidOperationException("Fixture not initialized");

    public Task InitializeAsync()
    {
        _apiClient = new HttpClient { BaseAddress = new Uri(_config.ApiBaseUrl) };
        _traceClient = new JaegerTraceClient(_config.JaegerQueryUrl);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _apiClient?.Dispose();
        _traceClient?.Dispose();
        GC.SuppressFinalize(this);
    }

    public PingPongDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PingPongDbContext>()
            .UseSqlServer(_config.DatabaseConnectionString)
            .Options;
        return new PingPongDbContext(options);
    }
}

/// <summary>
/// Abstract base class for end-to-end tests running against deployed K8s environments.
/// Uses TraceId correlation and polls Jaeger for span verification.
/// </summary>
public abstract class DeployedEndToEndTest : IClassFixture<DeployedFixture>
{
    protected readonly DeployedFixture Fixture;

    protected DeployedEndToEndTest(DeployedFixture fixture)
    {
        Fixture = fixture;
    }

    /// <summary>
    /// Creates a new DbContext for database assertions.
    /// </summary>
    protected PingPongDbContext CreateDbContext()
    {
        return Fixture.CreateDbContext();
    }

    /// <summary>
    /// Sends a GraphQL mutation and extracts a result using the provided selector.
    /// Returns both the result and the TraceId for span correlation.
    /// </summary>
    protected async Task<(TResult Result, string TraceId)> SendGraphQLMutation<TResult>(
        string mutation,
        Func<JsonElement, TResult> resultSelector)
    {
        // Start an activity to establish trace context that will propagate through all downstream services
        using var activity = new Activity("TestMutation").Start();
        var traceId = activity.TraceId.ToString();

        var graphqlQuery = new { query = mutation };

        var content = new StringContent(
            JsonSerializer.Serialize(graphqlQuery),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await Fixture.ApiClient.PostAsync("/graphql", content);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"GraphQL request failed: {response.StatusCode}. Response: {responseContent}");
        }

        var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

        // Check for GraphQL errors
        if (jsonResponse.TryGetProperty("errors", out var errors))
        {
            throw new Exception($"GraphQL errors: {errors}");
        }

        return (resultSelector(jsonResponse.GetProperty("data")), traceId);
    }

    /// <summary>
    /// Sends a GraphQL mutation, waits for message processing, and returns the extracted result.
    /// Uses TraceId correlation to ensure we only match spans from this specific request.
    /// </summary>
    protected async Task<TResult> SendMutationAndWaitForMessage<TMessage, TResult>(
        string mutation,
        Func<JsonElement, TResult> resultSelector,
        TimeSpan timeout)
    {
        var (result, traceId) = await SendGraphQLMutation(mutation, resultSelector);
        var span = await WaitForMessageProcessed<TMessage>(timeout, traceId);
        AssertSpanSucceeded(span);
        return result;
    }

    /// <summary>
    /// Waits for a message of the specified type to be processed successfully.
    /// </summary>
    protected async Task<Span> WaitForMessageProcessed<TMessage>(TimeSpan timeout, string traceId)
    {
        var messageTypeName = typeof(TMessage).Name;
        return await WaitForSpan(
            span => IsMessageProcessSpan(span, messageTypeName),
            timeout,
            traceId,
            $"message '{messageTypeName}' to be processed");
    }

    /// <summary>
    /// Waits for a span matching the specified predicate by polling the trace backend.
    /// </summary>
    protected async Task<Span> WaitForSpan(
        Func<Span, bool> predicate,
        TimeSpan timeout,
        string traceId,
        string? description = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var spansObserved = new List<string>();

        while (DateTime.UtcNow < deadline)
        {
            var spans = await Fixture.TraceClient.GetTraceAsync(traceId);

            foreach (var span in spans)
            {
                var attrInfo = string.Join(", ", span.Attributes.Select(a => $"{a.Key}={a.Value.StringValue}"));
                var spanDesc = $"{span.Name} [{attrInfo}]";

                if (!spansObserved.Contains(spanDesc))
                {
                    spansObserved.Add(spanDesc);
                }

                if (predicate(span))
                {
                    return span;
                }
            }

            await Task.Delay(Fixture.Config.TracePollingInterval);
        }

        var desc = description ?? "matching span";
        var observedInfo = spansObserved.Count > 0
            ? string.Join(Environment.NewLine, spansObserved.Select(s => $"  - {s}"))
            : "  (none)";

        throw new TimeoutException(
            $"Timeout waiting for {desc} (TraceId: {traceId}).\n\nReceived spans:\n{observedInfo}");
    }

    /// <summary>
    /// Asserts that a span completed successfully (no error status).
    /// </summary>
    protected static void AssertSpanSucceeded(Span span)
    {
        if (span.Status?.Code == Status.Types.StatusCode.Error)
        {
            var message = span.Status.Message ?? "Unknown error";
            throw new Xunit.Sdk.XunitException($"Span '{span.Name}' failed with error: {message}");
        }
    }

    private static bool IsMessageProcessSpan(Span span, string messageTypeName)
    {
        if (!span.Name.Equals("process message", StringComparison.OrdinalIgnoreCase))
            return false;

        var hasMatchingMessageType = span.Attributes.Any(a =>
            (a.Key.Contains("message_type", StringComparison.OrdinalIgnoreCase) ||
             a.Key.Equals("nservicebus.enclosed_message_types", StringComparison.OrdinalIgnoreCase)) &&
            a.Value?.StringValue?.Contains(messageTypeName, StringComparison.OrdinalIgnoreCase) == true);

        var isSuccess = span.Status == null || span.Status.Code != Status.Types.StatusCode.Error;

        return hasMatchingMessageType && isSuccess;
    }
}
