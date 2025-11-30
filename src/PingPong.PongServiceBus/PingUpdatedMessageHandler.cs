using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NServiceBus;
using PingPong.Data;
using PingPong.Messages;

namespace PingPong.PongServiceBus;

public class PingUpdatedMessageHandler : IHandleMessages<PingUpdatedMessage>
{
    private readonly ILogger<PingUpdatedMessageHandler> _logger;
    private readonly PingPongDbContext _dbContext;

    public PingUpdatedMessageHandler(ILogger<PingUpdatedMessageHandler> logger, PingPongDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task Handle(PingUpdatedMessage message, IMessageHandlerContext context)
    {
        _logger.LogInformation("Received PingUpdatedMessage for PingId: {PingId}, UpdatedAt: {UpdatedAt}",
            message.PingId, message.UpdatedAt);

        var pong = await _dbContext.Pongs
            .FirstOrDefaultAsync(p => p.PingId == message.PingId, context.CancellationToken);

        if (pong == null)
        {
            _logger.LogWarning("Pong for PingId {PingId} not found", message.PingId);
            return;
        }

        pong.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation("Updated pong {PongId} for ping {PingId} in database", pong.Id, pong.PingId);
    }
}
