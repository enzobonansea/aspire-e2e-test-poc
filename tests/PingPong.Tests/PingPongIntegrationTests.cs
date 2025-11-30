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

public class PingPongIntegrationTests : IAsyncLifetime
{
    private DistributedApplication? _app;
    private WebApplication? _otlpServer;
    private OtlpTraceCollector? _traceCollector;
    private int _otlpPort;

    public async Task InitializeAsync()
    {
        // Start OTLP gRPC collector first to get a port
        (_otlpServer, _traceCollector, _otlpPort) = await StartOtlpCollector();

        var otlpEndpoint = $"http://localhost:{_otlpPort}";

        // Configure Aspire to use our collector via configuration
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.PingPong_AppHost>([$"--OTEL_EXPORTER_OTLP_ENDPOINT={otlpEndpoint}"]);

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

    [Fact]
    public async Task Ping_message_should_be_processed()
    {
        // Arrange: Set up TaskCompletionSource to wait for the process span
        var tcs = new TaskCompletionSource<Span>();
        var spansObserved = new List<string>();

        _traceCollector!.SpanReceived += span =>
        {
            var spanName = span.Name;

            // Build attribute info for debugging
            var attrInfo = string.Join(", ", span.Attributes.Select(a => $"{a.Key}={a.Value}"));
            lock (spansObserved)
            {
                spansObserved.Add($"Received span: {spanName} [{attrInfo}]");
            }

            // NServiceBus "process message" span indicates message handling completion
            // Check attributes for the message type (nservicebus.message_type contains PingMessage)
            var isPingProcessSpan = spanName.Equals("process message", StringComparison.OrdinalIgnoreCase) &&
                                   span.Attributes.Any(a =>
                                       a.Key.Contains("message_type", StringComparison.OrdinalIgnoreCase) &&
                                       a.Value?.StringValue?.Contains("PingMessage", StringComparison.OrdinalIgnoreCase) == true);

            if (isPingProcessSpan)
            {
                // Check for successful completion (status code 0 = Unset, 1 = Ok)
                if (span.Status == null || span.Status.Code != Status.Types.StatusCode.Error)
                {
                    tcs.TrySetResult(span);
                }
            }
        };

        // Assert: Await the TaskCompletionSource (with 30s timeout)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            var completedSpan = await tcs.Task.WaitAsync(cts.Token);

            // Verify the span completed successfully
            Assert.NotNull(completedSpan);
            Assert.True(completedSpan.Status == null || completedSpan.Status.Code != Status.Types.StatusCode.Error,
                "Span should not have error status");

            // Log what we observed for debugging
            lock (spansObserved)
            {
                foreach (var observed in spansObserved)
                {
                    Console.WriteLine(observed);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Provide diagnostic information if test times out
            string observedInfo;
            string allSpanNames;
            lock (spansObserved)
            {
                observedInfo = string.Join(Environment.NewLine, spansObserved);
            }
            var allSpans = _traceCollector.ReceivedSpans;
            allSpanNames = string.Join(Environment.NewLine, allSpans.Select(s => $"  - {s.Name}"));

            Assert.Fail($"Timeout waiting for PingMessage processing span.\n\nObserved events:\n{observedInfo}\n\nAll received spans:\n{allSpanNames}");
        }
    }

    private static async Task<(WebApplication server, OtlpTraceCollector collector, int port)> StartOtlpCollector()
    {
        var collector = new OtlpTraceCollector();

        var builder = WebApplication.CreateBuilder();

        // Configure Kestrel to use HTTP/2 for gRPC with dynamic port
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

        // Get the actual port from Kestrel
        var server = app.Services.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>();
        var address = addressFeature?.Addresses.First() ?? throw new InvalidOperationException("No server address found");
        var port = new Uri(address).Port;

        return (app, collector, port);
    }
}
