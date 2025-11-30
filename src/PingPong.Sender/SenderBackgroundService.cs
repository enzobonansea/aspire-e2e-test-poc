using Microsoft.Extensions.Logging;
using PingPong.Messages;

namespace PingPong.Sender;

public class SenderBackgroundService : BackgroundService
{
    private readonly IMessageSession _messageSession;
    private readonly ILogger<SenderBackgroundService> _logger;

    public SenderBackgroundService(IMessageSession messageSession, ILogger<SenderBackgroundService> logger)
    {
        _messageSession = messageSession;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a moment for the receiver to be ready
        await Task.Delay(2000, stoppingToken);

        var pingMessage = new PingMessage();
        _logger.LogInformation("Sending PingMessage with Id: {Id}", pingMessage.Id);

        await _messageSession.Send(pingMessage, stoppingToken);

        _logger.LogInformation("PingMessage sent successfully");
    }
}
