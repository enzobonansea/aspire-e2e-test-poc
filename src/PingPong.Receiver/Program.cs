var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var endpointConfiguration = new EndpointConfiguration("PingPong.Receiver");
endpointConfiguration.UseSerialization<SystemJsonSerializer>();
endpointConfiguration.UseTransport<LearningTransport>();
endpointConfiguration.EnableOpenTelemetry();

builder.UseNServiceBus(endpointConfiguration);

var host = builder.Build();
await host.RunAsync();
