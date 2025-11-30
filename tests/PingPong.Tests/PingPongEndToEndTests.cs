using System.Text.Json;
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
        using var httpClient = CreateHttpClient("api");
        var graphqlQuery = new { query = "mutation { sendPing { id sentAt } }" };

        // Act - Send GraphQL mutation
        var content = new StringContent(
            JsonSerializer.Serialize(graphqlQuery),
            System.Text.Encoding.UTF8,
            "application/json");
        var response = await httpClient.PostAsync("/graphql", content);
        var responseContent = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"GraphQL request failed: {response.StatusCode}. Response: {responseContent}");
        }

        var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
        var pingId = jsonResponse.GetProperty("data").GetProperty("sendPing").GetProperty("id").GetGuid();

        // Wait for message to be processed
        var span = await WaitForMessageProcessed<PingMessage>(TimeSpan.FromSeconds(30));
        AssertSpanSucceeded(span);

        // Assert - Verify ping is stored in database
        await using var dbContext = CreateDbContext();
        var storedPing = await dbContext.Pings.FindAsync(pingId);

        Assert.NotNull(storedPing);
        Assert.Equal(pingId, storedPing.Id);
        Assert.NotNull(storedPing.ReceivedAt);
    }

    [Fact]
    public async Task Ping_pong_full_flow_should_store_pong_in_database()
    {
        // Arrange
        using var httpClient = CreateHttpClient("api");
        var graphqlQuery = new { query = "mutation { sendPing { id sentAt } }" };

        // Act - Send GraphQL mutation
        var content = new StringContent(
            JsonSerializer.Serialize(graphqlQuery),
            System.Text.Encoding.UTF8,
            "application/json");
        var response = await httpClient.PostAsync("/graphql", content);
        var responseContent = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"GraphQL request failed: {response.StatusCode}. Response: {responseContent}");
        }

        var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
        var pingId = jsonResponse.GetProperty("data").GetProperty("sendPing").GetProperty("id").GetGuid();

        // Wait for Pong message to be processed (full flow: API -> Receiver -> Sender)
        var span = await WaitForMessageProcessed<PongMessage>(TimeSpan.FromSeconds(60));
        AssertSpanSucceeded(span);

        // Assert - Verify pong is stored in database
        await using var dbContext = CreateDbContext();
        var storedPong = await dbContext.Pongs.FirstOrDefaultAsync(p => p.PingId == pingId);

        Assert.NotNull(storedPong);
        Assert.Equal(pingId, storedPong.PingId);
        Assert.NotNull(storedPong.ReceivedAt);
    }
}
