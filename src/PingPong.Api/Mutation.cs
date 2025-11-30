using PingPong.Messages;

namespace PingPong.Api;

public class Mutation
{
    public async Task<SendPingPayload> SendPingAsync([Service] IMessageSession messageSession)
    {
        var pingMessage = new PingMessage();

        await messageSession.Send(pingMessage);

        return new SendPingPayload(pingMessage.Id, pingMessage.SentAt);
    }
}

public record SendPingPayload(Guid Id, DateTime SentAt);
