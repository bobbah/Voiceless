using DSharpPlus.VoiceNext;

namespace Voiceless.Data;

public record QueuedMessage(Stream Stream, VoiceNextConnection VoiceChannel, string AudioFormat);