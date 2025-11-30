var builder = DistributedApplication.CreateBuilder(args);

// Get OTLP endpoint from configuration (can be overridden by tests)
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

// Add SQL Server
var sqlServer = builder.AddSqlServer("sql")
    .AddDatabase("pingpongdb");

var receiver = builder.AddProject<Projects.PingPong_Receiver>("receiver")
    .WithReference(sqlServer)
    .WaitFor(sqlServer);

var sender = builder.AddProject<Projects.PingPong_Sender>("sender");

var api = builder.AddProject<Projects.PingPong_Api>("api")
    .WithHttpEndpoint(name: "http")
    .WithExternalHttpEndpoints();

// If OTLP endpoint is configured, pass it to the services
if (!string.IsNullOrEmpty(otlpEndpoint))
{
    receiver.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
    sender.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
    api.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
}

builder.Build().Run();
