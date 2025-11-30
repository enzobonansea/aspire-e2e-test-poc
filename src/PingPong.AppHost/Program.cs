var builder = DistributedApplication.CreateBuilder(args);

// Get OTLP endpoint from configuration (can be overridden by tests)
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

// Shared path for LearningTransport - all services must use the same path
var learningTransportPath = Path.Combine(Path.GetTempPath(), "pingpong-learningtransport");

// Add SQL Server
var sqlServer = builder.AddSqlServer("sql")
    .AddDatabase("pingpongdb");

var pingServiceBus = builder.AddProject<Projects.PingPong_PingServiceBus>("pingservicebus")
    .WithReference(sqlServer)
    .WaitFor(sqlServer)
    .WithEnvironment("LEARNING_TRANSPORT_PATH", learningTransportPath);

var pongServiceBus = builder.AddProject<Projects.PingPong_PongServiceBus>("pongservicebus")
    .WithReference(sqlServer)
    .WaitFor(sqlServer)
    .WithEnvironment("LEARNING_TRANSPORT_PATH", learningTransportPath);

var api = builder.AddProject<Projects.PingPong_Api>("api")
    .WithHttpEndpoint(name: "http")
    .WithExternalHttpEndpoints()
    .WithEnvironment("LEARNING_TRANSPORT_PATH", learningTransportPath);

// If OTLP endpoint is configured, pass it to the services
if (!string.IsNullOrEmpty(otlpEndpoint))
{
    pingServiceBus.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
    pongServiceBus.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
    api.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
}

builder.Build().Run();
