var builder = DistributedApplication.CreateBuilder(args);

// Get OTLP endpoint from configuration (can be overridden by tests)
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

var receiver = builder.AddProject<Projects.PingPong_Receiver>("receiver");
var sender = builder.AddProject<Projects.PingPong_Sender>("sender");

// If OTLP endpoint is configured, pass it to the services
if (!string.IsNullOrEmpty(otlpEndpoint))
{
    receiver.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
    sender.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
}

builder.Build().Run();
