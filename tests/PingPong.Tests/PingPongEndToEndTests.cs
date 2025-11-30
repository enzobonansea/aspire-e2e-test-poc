using PingPong.Messages;

namespace PingPong.Tests;

public class PingPongEndToEndTests : EndToEndTest<Projects.PingPong_AppHost>
{
    [Fact]
    public async Task Ping_message_should_be_processed()
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(30);

        // Act & Assert
        var span = await WaitForMessageProcessed<PingMessage>(timeout);

        AssertSpanSucceeded(span);
    }
}
