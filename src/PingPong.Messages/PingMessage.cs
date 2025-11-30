using NServiceBus;

namespace PingPong.Messages;

public class PingMessage : IMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}
