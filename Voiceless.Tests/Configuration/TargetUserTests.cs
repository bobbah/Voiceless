using Voiceless.Configuration;

namespace Voiceless.Tests.Configuration;

public class TargetUserTests
{
    [Test]
    public async Task TargetUser_ShouldStoreUserIdCorrectly()
    {
        var user = new TargetUser { User = 987654321012345678, Voice = "echo", Servers = [] };
        
        await Assert.That(user.User).IsEqualTo(987654321012345678UL);
    }

    [Test]
    public async Task TargetUser_ShouldStoreVoiceCorrectly()
    {
        var user = new TargetUser { User = 123, Voice = "alloy", Servers = [] };
        
        await Assert.That(user.Voice).IsEqualTo("alloy");
    }

    [Test]
    public async Task TargetUser_ShouldStoreServersCorrectly()
    {
        var servers = new List<TargetServer>
        {
            new() { Server = 111222333, Channels = [444555666, 777888999] }
        };
        var user = new TargetUser { User = 123, Voice = "test", Servers = servers };
        
        await Assert.That(user.Servers).IsNotNull();
        await Assert.That(user.Servers.Count).IsEqualTo(1);
        await Assert.That(user.Servers[0].Server).IsEqualTo(111222333UL);
    }
}
