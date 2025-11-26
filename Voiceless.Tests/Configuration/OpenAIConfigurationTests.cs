using Voiceless.Configuration;

namespace Voiceless.Tests.Configuration;

public class OpenAIConfigurationTests
{
    [Test]
    public async Task OpenAIConfiguration_ShouldAllowSettingAllProperties()
    {
        var config = new OpenAIConfiguration
        {
            Token = "sk-test-token",
            Model = "tts-1",
            AttachmentDetail = "low",
            MaxAttachmentTokens = 500,
            DescribeAttachments = true,
            AttachmentPrompt = "Describe this image",
            FlavorPrompt = "Transform this text"
        };
        
        await Assert.That(config.Token).IsEqualTo("sk-test-token");
        await Assert.That(config.Model).IsEqualTo("tts-1");
        await Assert.That(config.AttachmentDetail).IsEqualTo("low");
        await Assert.That(config.MaxAttachmentTokens).IsEqualTo(500);
        await Assert.That(config.DescribeAttachments).IsTrue();
        await Assert.That(config.AttachmentPrompt).IsEqualTo("Describe this image");
        await Assert.That(config.FlavorPrompt).IsEqualTo("Transform this text");
    }

    [Test]
    public async Task OpenAIConfiguration_DescribeAttachments_ShouldDefaultToFalse()
    {
        var config = new OpenAIConfiguration();
        
        await Assert.That(config.DescribeAttachments).IsFalse();
    }

    [Test]
    public async Task OpenAIConfiguration_MaxAttachmentTokens_ShouldDefaultToZero()
    {
        var config = new OpenAIConfiguration();
        
        await Assert.That(config.MaxAttachmentTokens).IsEqualTo(0);
    }
}
