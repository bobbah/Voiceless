using OpenAI.Managers;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using Voiceless.Configuration;

namespace Voiceless.Voice;

public class OpenAIVoiceSynthesizer(OpenAIService openAI, OpenAIConfiguration config) : IVoiceSynthesizer
{
    public string AudioFormat => "ogg";

    public async Task<Stream?> SynthesizeTextToSpeechAsync(string text)
    {
        var result = await openAI.Audio.CreateSpeech<Stream>(new AudioCreateSpeechRequest
        {
            Model = config.Model,
            Input = text,
            Voice = config.Voice,
            ResponseFormat = StaticValues.AudioStatics.CreateSpeechResponseFormat.Opus,
            Speed = 1f
        });

        return result.Successful ? result.Data : null;
    }
}