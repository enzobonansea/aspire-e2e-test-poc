using PingPong.Messages;
using PingPong.Sender;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var endpointConfiguration = new EndpointConfiguration("PingPong.Sender");
endpointConfiguration.UseSerialization<SystemJsonSerializer>();
endpointConfiguration.UseTransport<LearningTransport>();
endpointConfiguration.EnableOpenTelemetry();

// Configure routing to send PingMessage to the Receiver
var routing = endpointConfiguration.UseTransport<LearningTransport>().Routing();
routing.RouteToEndpoint(typeof(PingMessage), "PingPong.Receiver");

builder.UseNServiceBus(endpointConfiguration);

// Add background service to send the ping message
builder.Services.AddHostedService<SenderBackgroundService>();

var host = builder.Build();
await host.RunAsync();
