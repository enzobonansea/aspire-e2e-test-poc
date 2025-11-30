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

    public async Task<UpdatePingPayload> UpdatePingAsync(
        Guid pingId,
        [Service] IMessageSession messageSession)
    {
        var updatePingMessage = new UpdatePingMessage { PingId = pingId };

        await messageSession.Send(updatePingMessage);

        return new UpdatePingPayload(pingId, updatePingMessage.SentAt);
    }
}

public record SendPingPayload(Guid Id, DateTime SentAt);
public record UpdatePingPayload(Guid PingId, DateTime SentAt);
