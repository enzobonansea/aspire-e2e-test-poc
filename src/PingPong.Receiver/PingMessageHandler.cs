using Microsoft.Extensions.Logging;
using PingPong.Messages;

namespace PingPong.Receiver;

public class PingMessageHandler : IHandleMessages<PingMessage>
{
    private readonly ILogger<PingMessageHandler> _logger;

    public PingMessageHandler(ILogger<PingMessageHandler> logger)
    {
        _logger = logger;
    }

    public Task Handle(PingMessage message, IMessageHandlerContext context)
    {
        _logger.LogInformation("Received PingMessage with Id: {Id}, SentAt: {SentAt}",
            message.Id, message.SentAt);

        return Task.CompletedTask;
    }
}
