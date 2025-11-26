using Voiceless.Data;

namespace Voiceless.Tests.Data;

public class QueuedMessageTests
{
    [Test]
    public async Task QueuedMessage_ShouldStoreAllPropertiesCorrectly()
    {
        using var stream = new MemoryStream();
        var guildId = 123456789012345678UL;
        var audioFormat = "mp3";
        
        var message = new QueuedMessage(stream, guildId, audioFormat);
        
        await Assert.That(message.Stream).IsSameReferenceAs(stream);
        await Assert.That(message.GuildId).IsEqualTo(guildId);
        await Assert.That(message.AudioFormat).IsEqualTo(audioFormat);
    }

    [Test]
    public async Task QueuedMessage_ShouldSupportDifferentAudioFormats()
    {
        using var stream = new MemoryStream();
        
        var mp3Message = new QueuedMessage(stream, 123, "mp3");
        var oggMessage = new QueuedMessage(stream, 123, "ogg");
        
        await Assert.That(mp3Message.AudioFormat).IsEqualTo("mp3");
        await Assert.That(oggMessage.AudioFormat).IsEqualTo("ogg");
    }

    [Test]
    public async Task QueuedMessage_ShouldBeARecord()
    {
        using var stream = new MemoryStream();
        var message1 = new QueuedMessage(stream, 123, "mp3");
        var message2 = new QueuedMessage(stream, 123, "mp3");
        
        await Assert.That(message1).IsEqualTo(message2);
    }

    [Test]
    public async Task QueuedMessage_ShouldSupportDeconstruction()
    {
        using var stream = new MemoryStream();
        var message = new QueuedMessage(stream, 456, "ogg");
        
        var (deconstructedStream, deconstructedGuildId, deconstructedFormat) = message;
        
        await Assert.That(deconstructedStream).IsSameReferenceAs(stream);
        await Assert.That(deconstructedGuildId).IsEqualTo(456UL);
        await Assert.That(deconstructedFormat).IsEqualTo("ogg");
    }
}
