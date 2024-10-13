using OpenAI;
using OpenAI.Audio;
using Voiceless.Configuration;

namespace Voiceless.Voice;

public class OpenAIVoiceSynthesizer(OpenAIClient openAI, OpenAIConfiguration config) : IVoiceSynthesizer
{
    public string AudioFormat => "ogg";

    public async Task<Stream?> SynthesizeTextToSpeechAsync(string text)
    {
        var result = await openAI.GetAudioClient(config.Model).GenerateSpeechAsync(text, config.Voice, new SpeechGenerationOptions()
        {
            ResponseFormat = GeneratedSpeechFormat.Opus,
            SpeedRatio = 1f
        });

        return result?.Value.ToStream();
    }
}