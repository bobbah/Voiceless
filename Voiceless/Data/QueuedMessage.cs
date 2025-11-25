namespace Voiceless.Data;

public record QueuedMessage(Stream Stream, ulong GuildId, string AudioFormat);