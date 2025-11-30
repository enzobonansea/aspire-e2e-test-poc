using NServiceBus;

namespace PingPong.Messages;

public class PongMessage : IMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PingId { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}
