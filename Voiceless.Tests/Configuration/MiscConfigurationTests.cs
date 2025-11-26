using Voiceless.Configuration;

namespace Voiceless.Tests.Configuration;

public class MiscConfigurationTests
{
    [Test]
    public async Task MiscConfiguration_ShouldAllowSettingSilencer()
    {
        var config = new MiscConfiguration { Silencer = "!" };
        
        await Assert.That(config.Silencer).IsEqualTo("!");
    }

    [Test]
    public async Task MiscConfiguration_ShouldInitializeWithDefaultValues()
    {
        var config = new MiscConfiguration();
        
        await Assert.That(config.Silencer).IsNull();
    }
}
