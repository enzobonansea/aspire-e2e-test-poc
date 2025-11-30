using PingPong.Api;
using PingPong.Messages;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Configure NServiceBus as send-only endpoint
var endpointConfiguration = new EndpointConfiguration("PingPong.Api");
endpointConfiguration.UseSerialization<SystemJsonSerializer>();
endpointConfiguration.EnableOpenTelemetry();
endpointConfiguration.SendOnly();

// Configure LearningTransport with shared path
var learningTransportPath = builder.Configuration["LEARNING_TRANSPORT_PATH"];
var transport = endpointConfiguration.UseTransport<LearningTransport>();
if (!string.IsNullOrEmpty(learningTransportPath))
{
    transport.StorageDirectory(learningTransportPath);
}

// Configure routing to send PingMessage to PingServiceBus
var routing = transport.Routing();
routing.RouteToEndpoint(typeof(PingMessage), "PingPong.PingServiceBus");

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
