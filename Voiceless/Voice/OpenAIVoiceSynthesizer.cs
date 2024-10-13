using OpenAI;
using OpenAI.Audio;
using Voiceless.Configuration;

namespace Voiceless.Voice;

public class OpenAIVoiceSynthesizer(OpenAIClient openAI, OpenAIConfiguration config) : IVoiceSynthesizer
{
    public string AudioFormat => "ogg";

    public async Task<Stream?> SynthesizeTextToSpeechAsync(string text, string voice)
    {
        var result = await openAI.GetAudioClient(config.Model).GenerateSpeechAsync(text, voice, new SpeechGenerationOptions()
        {
            ResponseFormat = GeneratedSpeechFormat.Opus,
            SpeedRatio = 1f
        });

        return result?.Value.ToStream();
    }
}