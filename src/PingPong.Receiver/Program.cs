using PingPong.Data;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Add SQL Server DbContext
builder.AddSqlServerDbContext<PingPongDbContext>("pingpongdb");

var endpointConfiguration = new EndpointConfiguration("PingPong.Receiver");
endpointConfiguration.UseSerialization<SystemJsonSerializer>();
endpointConfiguration.UseTransport<LearningTransport>();
endpointConfiguration.EnableOpenTelemetry();

builder.UseNServiceBus(endpointConfiguration);

var host = builder.Build();

// Ensure database is created
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PingPongDbContext>();
    await db.Database.EnsureCreatedAsync();
}

await host.RunAsync();
