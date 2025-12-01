using Microsoft.EntityFrameworkCore;
using PingPong.Data;
using PingPong.Messages;

namespace PingPong.DeployedTests;

/// <summary>
/// End-to-end tests for PingPong application running on deployed K8s pods.
/// These tests use TraceId correlation to isolate spans and can run concurrently.
///
/// Required environment variables:
/// - TEST_API_URL: Base URL of the deployed API (e.g., https://api.test.example.com)
/// - TEST_JAEGER_URL: URL of Jaeger Query service (e.g., http://jaeger-query.observability:16686)
/// - TEST_DB_CONNECTION_STRING: Database connection string for assertions
/// </summary>
public sealed class DeployedPingPongTests : DeployedEndToEndTest
{
    public DeployedPingPongTests(DeployedFixture fixture) : base(fixture)
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
