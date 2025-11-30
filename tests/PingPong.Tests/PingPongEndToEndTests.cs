using Microsoft.EntityFrameworkCore;
using PingPong.Messages;

namespace PingPong.Tests;

public sealed class PingPongEndToEndTests : EndToEndTest<Projects.PingPong_AppHost>
{
    public PingPongEndToEndTests(AspireFixture<Projects.PingPong_AppHost> fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Ping_sent_via_graphql_should_be_stored_in_database()
    {
        // Arrange
        var mutation = "mutation { sendPing { id sentAt } }";
        var timeout = TimeSpan.FromSeconds(30);

        // Act
        var pingId = await SendMutationAndWaitForMessage<PingMessage, Guid>(
            mutation,
            mutationResult => mutationResult.GetProperty("sendPing").GetProperty("id").GetGuid(),
            timeout);

        // Assert
        await using var db = CreateDbContext();
        var ping = await db.Pings.FindAsync(pingId);
        Assert.NotNull(ping);
        Assert.NotNull(ping.ReceivedAt);
    }

    [Fact]
    public async Task Ping_pong_full_flow_should_store_pong_in_database()
    {
        // Arrange
        var mutation = "mutation { sendPing { id sentAt } }";
        var timeout = TimeSpan.FromSeconds(60);

        // Act
        var pingId = await SendMutationAndWaitForMessage<PongMessage, Guid>(
            mutation,
            mutationResult => mutationResult.GetProperty("sendPing").GetProperty("id").GetGuid(),
            timeout);

        // Assert
        await using var db = CreateDbContext();
        var pong = await db.Pongs.FirstOrDefaultAsync(p => p.PingId == pingId);
        Assert.NotNull(pong);
        Assert.NotNull(pong.ReceivedAt);
    }
}
