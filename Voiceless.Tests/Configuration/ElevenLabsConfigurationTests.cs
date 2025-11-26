using Voiceless.Configuration;

namespace Voiceless.Tests.Configuration;

public class ElevenLabsConfigurationTests
{
    [Test]
    public async Task ElevenLabsConfiguration_ShouldAllowSettingAllProperties()
    {
        var config = new ElevenLabsConfiguration
        {
            Token = "eleven-labs-token",
            VoiceID = "voice-123",
            Stability = 0.5f,
            Similarity = 0.75f
        };
        
        await Assert.That(config.Token).IsEqualTo("eleven-labs-token");
        await Assert.That(config.VoiceID).IsEqualTo("voice-123");
        await Assert.That(config.Stability).IsEqualTo(0.5f);
        await Assert.That(config.Similarity).IsEqualTo(0.75f);
    }

    [Test]
    public async Task ElevenLabsConfiguration_Token_ShouldBeNullable()
    {
        var config = new ElevenLabsConfiguration { Token = null };
        
        await Assert.That(config.Token).IsNull();
    }

    [Test]
    public async Task ElevenLabsConfiguration_FloatValues_ShouldDefaultToZero()
    {
        var config = new ElevenLabsConfiguration();
        
        await Assert.That(config.Stability).IsEqualTo(0f);
        await Assert.That(config.Similarity).IsEqualTo(0f);
    }
}
