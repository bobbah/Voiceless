using Voiceless.Configuration;

namespace Voiceless.Tests.Configuration;

public class TargetConfigurationTests
{
    [Test]
    public async Task TargetConfiguration_ShouldInitializeWithDefaultValues()
    {
        var config = new TargetConfiguration();
        
        await Assert.That(config.Users).IsNull();
    }

    [Test]
    public async Task TargetConfiguration_ShouldAllowSettingUsers()
    {
        var users = new List<TargetUser>
        {
            new() { User = 123456789, Voice = "test-voice", Servers = [] }
        };
        var config = new TargetConfiguration { Users = users };
        
        await Assert.That(config.Users).IsNotNull();
        await Assert.That(config.Users.Count).IsEqualTo(1);
        await Assert.That(config.Users[0].User).IsEqualTo(123456789UL);
    }
}
