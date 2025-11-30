namespace PingPong.Data;

public class Pong
{
    public Guid Id { get; set; }
    public Guid PingId { get; set; }
    public DateTime SentAt { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
