using PingPong.Data;
using PingPong.Messages;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Add SQL Server DbContext
builder.AddSqlServerDbContext<PingPongDbContext>("pingpongdb");

var endpointConfiguration = new EndpointConfiguration("PingPong.PingServiceBus");
endpointConfiguration.UseSerialization<SystemJsonSerializer>();
endpointConfiguration.EnableOpenTelemetry();

// Configure LearningTransport with shared path
var learningTransportPath = builder.Configuration["LEARNING_TRANSPORT_PATH"];
var transport = endpointConfiguration.UseTransport<LearningTransport>();
if (!string.IsNullOrEmpty(learningTransportPath))
{
    transport.StorageDirectory(learningTransportPath);
}

// Configure routing to send messages to PongServiceBus
var routing = transport.Routing();
routing.RouteToEndpoint(typeof(PongMessage), "PingPong.PongServiceBus");
routing.RouteToEndpoint(typeof(PingUpdatedMessage), "PingPong.PongServiceBus");

builder.UseNServiceBus(endpointConfiguration);

var host = builder.Build();

// Ensure database schema exists (ignore if already created by another service)
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PingPongDbContext>();
    try
    {
        await db.Database.EnsureCreatedAsync();
    }
    catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 1801)
    {
        // Database already exists - created by another service
    }
}

await host.RunAsync();
