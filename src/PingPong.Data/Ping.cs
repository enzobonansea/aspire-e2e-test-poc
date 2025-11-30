namespace PingPong.Data;

public class Ping
{
    public Guid Id { get; set; }
    public DateTime SentAt { get; set; }
    public DateTime? ReceivedAt { get; set; }
}
