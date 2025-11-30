using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PingPong.Data;
using PingPong.Messages;

namespace PingPong.Tests;

public class PingPongEndToEndTests : EndToEndTest<Projects.PingPong_AppHost>
{
    [Fact]
    public async Task Ping_message_should_be_processed()
    {
        // Arrange - longer timeout for SQL Server container startup
        var timeout = TimeSpan.FromSeconds(120);

        // Act & Assert
        var span = await WaitForMessageProcessed<PingMessage>(timeout);

        AssertSpanSucceeded(span);
    }

    [Fact]
    public async Task Ping_sent_via_graphql_should_be_stored_in_database()
    {
        // Arrange - longer timeout for SQL Server container startup
        var timeout = TimeSpan.FromSeconds(120);
        using var httpClient = CreateHttpClient("api");

        var graphqlQuery = new
        {
            query = "mutation { sendPing { id sentAt } }"
        };

        // Act - Send GraphQL mutation
        var content = new StringContent(
            JsonSerializer.Serialize(graphqlQuery),
            System.Text.Encoding.UTF8,
            "application/json");
        var response = await httpClient.PostAsync("/graphql", content);
        var responseContent = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"GraphQL request failed with status {response.StatusCode}. Response: {responseContent}");
        }

        var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
        var pingId = jsonResponse.GetProperty("data").GetProperty("sendPing").GetProperty("id").GetGuid();

        // Wait for message to be processed
        var span = await WaitForMessageProcessed<PingMessage>(timeout);
        AssertSpanSucceeded(span);

        // Assert - Verify ping is stored in database
        var connectionString = await GetConnectionStringAsync("pingpongdb");
        var options = new DbContextOptionsBuilder<PingPongDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        await using var dbContext = new PingPongDbContext(options);
        var storedPing = await dbContext.Pings.FindAsync(pingId);

        Assert.NotNull(storedPing);
        Assert.Equal(pingId, storedPing.Id);
        Assert.NotNull(storedPing.ReceivedAt);
    }
}
