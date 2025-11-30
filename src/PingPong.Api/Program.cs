using PingPong.Api;
using PingPong.Messages;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Configure NServiceBus
var endpointConfiguration = new EndpointConfiguration("PingPong.Api");
endpointConfiguration.UseSerialization<SystemJsonSerializer>();
endpointConfiguration.UseTransport<LearningTransport>();
endpointConfiguration.EnableOpenTelemetry();

// Configure routing to send PingMessage to the Receiver
var routing = endpointConfiguration.UseTransport<LearningTransport>().Routing();
routing.RouteToEndpoint(typeof(PingMessage), "PingPong.Receiver");

builder.UseNServiceBus(endpointConfiguration);

// Configure HotChocolate GraphQL
builder.Services
    .AddGraphQLServer()
    .AddMutationType<Mutation>()
    .AddQueryType<Query>()
    .ModifyRequestOptions(opt => opt.IncludeExceptionDetails = true);

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapGraphQL();

app.Run();
