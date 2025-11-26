using Voiceless.Configuration;

namespace Voiceless.Tests.Configuration;

public class TargetServerTests
{
    [Test]
    public async Task TargetServer_ShouldStoreServerIdCorrectly()
    {
        var server = new TargetServer { Server = 123456789012345678, Channels = [] };
        
        await Assert.That(server.Server).IsEqualTo(123456789012345678UL);
    }

    [Test]
    public async Task TargetServer_ShouldStoreChannelsCorrectly()
    {
        var channels = new List<ulong> { 111111111, 222222222, 333333333 };
        var server = new TargetServer { Server = 999, Channels = channels };
        
        await Assert.That(server.Channels).IsNotNull();
        await Assert.That(server.Channels.Count).IsEqualTo(3);
        await Assert.That(server.Channels[0]).IsEqualTo(111111111UL);
        await Assert.That(server.Channels[1]).IsEqualTo(222222222UL);
        await Assert.That(server.Channels[2]).IsEqualTo(333333333UL);
    }

    [Test]
    public async Task TargetServer_ShouldAllowEmptyChannelsList()
    {
        var server = new TargetServer { Server = 123, Channels = [] };
        
        await Assert.That(server.Channels).IsNotNull();
        await Assert.That(server.Channels.Count).IsEqualTo(0);
    }
}
