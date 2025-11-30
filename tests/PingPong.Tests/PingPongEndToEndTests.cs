using Microsoft.EntityFrameworkCore;
using PingPong.Data;
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

    [Fact]
    public async Task Update_ping_should_update_both_ping_and_pong()
    {
        // Arrange - seed ping and pong in database
        var pingId = Guid.NewGuid();
        var pongId = Guid.NewGuid();

        await using (var db = CreateDbContext())
        {
            db.Pings.Add(new Ping
            {
                Id = pingId,
                SentAt = DateTime.UtcNow.AddMinutes(-5),
                ReceivedAt = DateTime.UtcNow.AddMinutes(-4)
            });

            db.Pongs.Add(new Pong
            {
                Id = pongId,
                PingId = pingId,
                SentAt = DateTime.UtcNow.AddMinutes(-3),
                ReceivedAt = DateTime.UtcNow.AddMinutes(-2)
            });

            await db.SaveChangesAsync();
        }

        var mutation = $"mutation {{ updatePing(pingId: \"{pingId}\") {{ pingId sentAt }} }}";
        var timeout = TimeSpan.FromSeconds(60);

        // Act
        await SendMutationAndWaitForMessage<PingUpdatedMessage, Guid>(
            mutation,
            mutationResult => mutationResult.GetProperty("updatePing").GetProperty("pingId").GetGuid(),
            timeout);

        // Assert
        await using var dbAssert = CreateDbContext();

        var ping = await dbAssert.Pings.FindAsync(pingId);
        Assert.NotNull(ping);
        Assert.NotNull(ping.UpdatedAt);

        var pong = await dbAssert.Pongs.FirstOrDefaultAsync(p => p.PingId == pingId);
        Assert.NotNull(pong);
        Assert.NotNull(pong.UpdatedAt);
    }
}
