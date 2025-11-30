using Microsoft.Extensions.Logging;
using PingPong.Data;
using PingPong.Messages;

namespace PingPong.PingServiceBus;

public class UpdatePingMessageHandler : IHandleMessages<UpdatePingMessage>
{
    private readonly ILogger<UpdatePingMessageHandler> _logger;
    private readonly PingPongDbContext _dbContext;

    public UpdatePingMessageHandler(ILogger<UpdatePingMessageHandler> logger, PingPongDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task Handle(UpdatePingMessage message, IMessageHandlerContext context)
    {
        _logger.LogInformation("Received UpdatePingMessage for PingId: {PingId}", message.PingId);

        var ping = await _dbContext.Pings.FindAsync([message.PingId], context.CancellationToken);

        if (ping == null)
        {
            _logger.LogWarning("Ping {PingId} not found", message.PingId);
            return;
        }

        ping.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation("Updated ping {PingId} in database", ping.Id);

        // Send PingUpdatedMessage to PongServiceBus
        var pingUpdatedMessage = new PingUpdatedMessage
        {
            PingId = message.PingId,
            UpdatedAt = ping.UpdatedAt.Value
        };

        await context.Send(pingUpdatedMessage);

        _logger.LogInformation("Sent PingUpdatedMessage to PongServiceBus for ping {PingId}", message.PingId);
    }
}
