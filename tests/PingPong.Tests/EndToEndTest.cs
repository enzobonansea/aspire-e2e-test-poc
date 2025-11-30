using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Trace.V1;
using PingPong.Data;

namespace PingPong.Tests;

/// <summary>
/// Shared fixture that manages the Aspire application and OTLP collector lifecycle.
/// This is created once and shared across all tests in a test class.
/// </summary>
/// <typeparam name="TAppHost">The Aspire AppHost project type</typeparam>
public class AspireFixture<TAppHost> : IAsyncLifetime where TAppHost : class
{
    private DistributedApplication? _app;
    private WebApplication? _otlpServer;
    private OtlpTraceCollector? _traceCollector;
    private string? _connectionString;

    public DistributedApplication App => _app ?? throw new InvalidOperationException("App not initialized");
    public OtlpTraceCollector TraceCollector => _traceCollector ?? throw new InvalidOperationException("Collector not initialized");

    public async Task InitializeAsync()
    {
        // Start OTLP gRPC collector
        (_otlpServer, _traceCollector) = await StartOtlpCollector();

        var otlpEndpoint = GetOtlpEndpoint();

        // Configure and start Aspire app with OTLP endpoint
        // Pass as command line arg - format is "key=value" for configuration
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<TAppHost>([$"OTEL_EXPORTER_OTLP_ENDPOINT={otlpEndpoint}"]);

        _app = await appHost.BuildAsync();
        await _app.StartAsync();

        // Cache connection string
        _connectionString = await _app.GetConnectionStringAsync("pingpongdb");
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_otlpServer is not null)
        {
            await _otlpServer.StopAsync();
            await _otlpServer.DisposeAsync();
        }
    }

    public HttpClient CreateHttpClient(string resourceName, string endpointName = "http")
    {
        return App.CreateHttpClient(resourceName, endpointName);
    }

    public string GetConnectionString()
    {
        return _connectionString ?? throw new InvalidOperationException("Connection string not available");
    }

    public PingPongDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PingPongDbContext>()
            .UseSqlServer(GetConnectionString())
            .Options;
        return new PingPongDbContext(options);
    }

    private string GetOtlpEndpoint()
    {
        var server = _otlpServer!.Services.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>();
        var address = addressFeature?.Addresses.First()
            ?? throw new InvalidOperationException("No server address found");
        return address;
    }

    private static async Task<(WebApplication server, OtlpTraceCollector collector)> StartOtlpCollector()
    {
        var collector = new OtlpTraceCollector();

        var builder = WebApplication.CreateBuilder();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Loopback, 0, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
        });

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(collector);

        var app = builder.Build();
        app.MapGrpcService<OtlpTraceCollector>();

        await app.StartAsync();

        return (app, collector);
    }
}

/// <summary>
/// Abstract base class for end-to-end tests using Aspire with OTLP telemetry.
/// Uses IClassFixture to share the Aspire app across all tests in the class.
/// </summary>
/// <typeparam name="TAppHost">The Aspire AppHost project type</typeparam>
public abstract class EndToEndTest<TAppHost> : IClassFixture<AspireFixture<TAppHost>> where TAppHost : class
{
    protected readonly AspireFixture<TAppHost> Fixture;

    protected EndToEndTest(AspireFixture<TAppHost> fixture)
    {
        Fixture = fixture;
    }

    /// <summary>
    /// The Aspire distributed application instance.
    /// </summary>
    protected DistributedApplication App => Fixture.App;

    /// <summary>
    /// Creates an HTTP client configured to call the specified resource.
    /// </summary>
    protected HttpClient CreateHttpClient(string resourceName, string endpointName = "http")
    {
        return Fixture.CreateHttpClient(resourceName, endpointName);
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
    /// </summary>
    protected async Task<TResult> SendGraphQLMutation<TResult>(string mutation, Func<JsonElement, TResult> resultSelector)
    {
        using var httpClient = CreateHttpClient("api");
        var graphqlQuery = new { query = mutation };

        var content = new StringContent(
            JsonSerializer.Serialize(graphqlQuery),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await httpClient.PostAsync("/graphql", content);
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

        return resultSelector(jsonResponse.GetProperty("data"));
    }

    /// <summary>
    /// Sends a GraphQL mutation, waits for message processing, and returns the extracted result.
    /// </summary>
    protected async Task<TResult> SendMutationAndWaitForMessage<TMessage, TResult>(
        string mutation,
        Func<JsonElement, TResult> resultSelector,
        TimeSpan timeout)
    {
        var result = await SendGraphQLMutation(mutation, resultSelector);
        var span = await WaitForMessageProcessed<TMessage>(timeout);
        AssertSpanSucceeded(span);
        return result;
    }

    /// <summary>
    /// Waits for a message of the specified type to be processed successfully.
    /// </summary>
    protected async Task<Span> WaitForMessageProcessed<TMessage>(TimeSpan timeout)
    {
        var messageTypeName = typeof(TMessage).Name;
        return await WaitForSpan(
            span => IsMessageProcessSpan(span, messageTypeName),
            timeout,
            $"message '{messageTypeName}' to be processed");
    }

    /// <summary>
    /// Waits for a span matching the specified predicate.
    /// </summary>
    protected async Task<Span> WaitForSpan(Func<Span, bool> predicate, TimeSpan timeout, string? description = null)
    {
        var tcs = new TaskCompletionSource<Span>();
        var spansObserved = new List<string>();

        void OnSpanReceived(Span span)
        {
            var attrInfo = string.Join(", ", span.Attributes.Select(a => $"{a.Key}={a.Value.StringValue}"));
            lock (spansObserved)
            {
                spansObserved.Add($"{span.Name} [{attrInfo}]");
            }

            if (predicate(span))
            {
                tcs.TrySetResult(span);
            }
        }

        Fixture.TraceCollector.SpanReceived += OnSpanReceived;

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            string observedInfo;
            lock (spansObserved)
            {
                observedInfo = spansObserved.Count > 0
                    ? string.Join(Environment.NewLine, spansObserved.Select(s => $"  - {s}"))
                    : "  (none)";
            }

            var desc = description ?? "matching span";
            throw new TimeoutException(
                $"Timeout waiting for {desc}.\n\nReceived spans:\n{observedInfo}");
        }
        finally
        {
            Fixture.TraceCollector.SpanReceived -= OnSpanReceived;
        }
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

        // NServiceBus uses nservicebus.enclosed_message_types attribute
        var hasMatchingMessageType = span.Attributes.Any(a =>
            (a.Key.Contains("message_type", StringComparison.OrdinalIgnoreCase) ||
             a.Key.Equals("nservicebus.enclosed_message_types", StringComparison.OrdinalIgnoreCase)) &&
            a.Value?.StringValue?.Contains(messageTypeName, StringComparison.OrdinalIgnoreCase) == true);

        var isSuccess = span.Status == null || span.Status.Code != Status.Types.StatusCode.Error;

        return hasMatchingMessageType && isSuccess;
    }
}
