using NServiceBus;

namespace PingPong.Messages;

public class UpdatePingMessage : IMessage
{
    public Guid PingId { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}
