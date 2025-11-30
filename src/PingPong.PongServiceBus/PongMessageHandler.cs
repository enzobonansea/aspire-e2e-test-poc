using Microsoft.Extensions.Logging;
using NServiceBus;
using PingPong.Data;
using PingPong.Messages;

namespace PingPong.PongServiceBus;

public class PongMessageHandler : IHandleMessages<PongMessage>
{
    private readonly ILogger<PongMessageHandler> _logger;
    private readonly PingPongDbContext _dbContext;

    public PongMessageHandler(ILogger<PongMessageHandler> logger, PingPongDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task Handle(PongMessage message, IMessageHandlerContext context)
    {
        _logger.LogInformation("Received PongMessage with Id: {Id}, PingId: {PingId}, SentAt: {SentAt}",
            message.Id, message.PingId, message.SentAt);

        var pong = new Pong
        {
            Id = message.Id,
            PingId = message.PingId,
            SentAt = message.SentAt,
            ReceivedAt = DateTime.UtcNow
        };

        _dbContext.Pongs.Add(pong);
        await _dbContext.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation("Stored pong {Id} for ping {PingId} in database", pong.Id, pong.PingId);
    }
}
