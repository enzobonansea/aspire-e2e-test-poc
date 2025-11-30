using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Trace.V1;

namespace PingPong.Tests;

/// <summary>
/// Abstract base class for end-to-end tests using Aspire with OTLP telemetry.
/// Handles all infrastructure: OTLP collector, Aspire app hosting, and span collection.
///
/// Derived classes only need to:
/// 1. Specify the AppHost type via generic parameter
/// 2. Write simple test methods using WaitForMessageProcessed() or WaitForSpan()
/// </summary>
/// <typeparam name="TAppHost">The Aspire AppHost project type</typeparam>
public abstract class EndToEndTest<TAppHost> : IAsyncLifetime where TAppHost : class
{
    private DistributedApplication? _app;
    private WebApplication? _otlpServer;
    private OtlpTraceCollector? _traceCollector;

    /// <summary>
    /// The Aspire distributed application instance.
    /// </summary>
    protected DistributedApplication App => _app ?? throw new InvalidOperationException("App not initialized");

    /// <summary>
    /// Access to all received spans for custom assertions.
    /// </summary>
    protected IReadOnlyList<Span> ReceivedSpans => _traceCollector?.ReceivedSpans ?? [];

    /// <summary>
    /// Creates an HTTP client configured to call the specified resource.
    /// </summary>
    /// <param name="resourceName">The name of the resource in the AppHost</param>
    /// <param name="endpointName">Optional endpoint name (defaults to "http")</param>
    /// <returns>An HttpClient configured for the resource</returns>
    protected HttpClient CreateHttpClient(string resourceName, string endpointName = "http")
    {
        return App.CreateHttpClient(resourceName, endpointName);
    }

    /// <summary>
    /// Gets the connection string for the specified resource.
    /// </summary>
    /// <param name="resourceName">The name of the resource in the AppHost</param>
    /// <returns>The connection string</returns>
    protected async Task<string> GetConnectionStringAsync(string resourceName)
    {
        return await App.GetConnectionStringAsync(resourceName)
            ?? throw new InvalidOperationException($"Connection string for '{resourceName}' not found");
    }

    public async Task InitializeAsync()
    {
        // Start OTLP gRPC collector
        (_otlpServer, _traceCollector) = await StartOtlpCollector();

        var otlpEndpoint = GetOtlpEndpoint();

        // Configure and start Aspire app
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<TAppHost>([$"--OTEL_EXPORTER_OTLP_ENDPOINT={otlpEndpoint}"]);

        _app = await appHost.BuildAsync();
        await _app.StartAsync();
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

    /// <summary>
    /// Waits for a message of the specified type to be processed successfully.
    /// </summary>
    /// <typeparam name="TMessage">The message type to wait for</typeparam>
    /// <param name="timeout">Timeout duration</param>
    /// <returns>The span representing the processed message</returns>
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
    /// <param name="predicate">Predicate to match spans</param>
    /// <param name="timeout">Timeout duration</param>
    /// <param name="description">Description for error messages</param>
    /// <returns>The matching span</returns>
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

        _traceCollector!.SpanReceived += OnSpanReceived;

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
            _traceCollector!.SpanReceived -= OnSpanReceived;
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

        var hasMatchingMessageType = span.Attributes.Any(a =>
            a.Key.Contains("message_type", StringComparison.OrdinalIgnoreCase) &&
            a.Value?.StringValue?.Contains(messageTypeName, StringComparison.OrdinalIgnoreCase) == true);

        var isSuccess = span.Status == null || span.Status.Code != Status.Types.StatusCode.Error;

        return hasMatchingMessageType && isSuccess;
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
