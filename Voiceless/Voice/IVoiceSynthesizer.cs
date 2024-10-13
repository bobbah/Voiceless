namespace Voiceless.Voice;

public interface IVoiceSynthesizer
{
    string AudioFormat { get; }
    Task<Stream?> SynthesizeTextToSpeechAsync(string text);
}