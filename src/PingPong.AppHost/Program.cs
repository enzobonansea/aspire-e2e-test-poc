var builder = DistributedApplication.CreateBuilder(args);

// Get OTLP endpoint from configuration (can be overridden by tests)
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

// Shared path for LearningTransport - all services must use the same path
var learningTransportPath = Path.Combine(Path.GetTempPath(), "pingpong-learningtransport");

// Add SQL Server
var sqlServer = builder.AddSqlServer("sql")
    .AddDatabase("pingpongdb");

var receiver = builder.AddProject<Projects.PingPong_Receiver>("receiver")
    .WithReference(sqlServer)
    .WaitFor(sqlServer)
    .WithEnvironment("LEARNING_TRANSPORT_PATH", learningTransportPath);

var sender = builder.AddProject<Projects.PingPong_Sender>("sender")
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
    receiver.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
    sender.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
    api.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
}

builder.Build().Run();
