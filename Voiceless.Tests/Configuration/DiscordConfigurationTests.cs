using Voiceless.Configuration;

namespace Voiceless.Tests.Configuration;

public class DiscordConfigurationTests
{
    [Test]
    public async Task DiscordConfiguration_ShouldAllowSettingToken()
    {
        var config = new DiscordConfiguration { Token = "test-token-123" };
        
        await Assert.That(config.Token).IsEqualTo("test-token-123");
    }

    [Test]
    public async Task DiscordConfiguration_ShouldInitializeWithDefaultValues()
    {
        var config = new DiscordConfiguration();
        
        await Assert.That(config.Token).IsNull();
    }
}
