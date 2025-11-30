using Microsoft.Extensions.Logging;
using PingPong.Data;
using PingPong.Messages;

namespace PingPong.Receiver;

public class PingMessageHandler : IHandleMessages<PingMessage>
{
    private readonly ILogger<PingMessageHandler> _logger;
    private readonly PingPongDbContext _dbContext;

    public PingMessageHandler(ILogger<PingMessageHandler> logger, PingPongDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task Handle(PingMessage message, IMessageHandlerContext context)
    {
        _logger.LogInformation("Received PingMessage with Id: {Id}, SentAt: {SentAt}",
            message.Id, message.SentAt);

        var ping = new Ping
        {
            Id = message.Id,
            SentAt = message.SentAt,
            ReceivedAt = DateTime.UtcNow
        };

        _dbContext.Pings.Add(ping);
        await _dbContext.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation("Stored ping {Id} in database", ping.Id);

        // Send Pong to Sender
        var pongMessage = new PongMessage
        {
            PingId = message.Id
        };

        await context.Send(pongMessage);

        _logger.LogInformation("Sent PongMessage to Sender for ping {PingId}", message.Id);
    }
}
