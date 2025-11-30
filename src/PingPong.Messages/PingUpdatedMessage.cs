using NServiceBus;

namespace PingPong.Messages;

public class PingUpdatedMessage : IMessage
{
    public Guid PingId { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}
