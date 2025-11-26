using OpenAI;
using OpenAI.Audio;
using Serilog;
using Voiceless.Configuration;

namespace Voiceless.Voice;

public class OpenAIVoiceSynthesizer(OpenAIClient openAI, OpenAIConfiguration config) : IVoiceSynthesizer
{
    public string AudioFormat => "ogg";

    public async Task<Stream?> SynthesizeTextToSpeechAsync(string text, string voice)
    {
        try
        {
            Log.Debug("OpenAI: Synthesizing TTS for text of length {Length} with voice '{Voice}'", text.Length, voice);
            var result = await openAI.GetAudioClient(config.Model).GenerateSpeechAsync(text, voice, new SpeechGenerationOptions()
            {
                ResponseFormat = GeneratedSpeechFormat.Opus,
                SpeedRatio = 1f
            });

            if (result?.Value == null)
            {
                Log.Warning("OpenAI: TTS synthesis returned null result");
                return null;
            }
            
            var stream = result.Value.ToStream();
            Log.Debug("OpenAI: TTS synthesis completed successfully");
            return stream;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OpenAI: Failed to synthesize TTS for text of length {Length} with voice '{Voice}'", text.Length, voice);
            throw;
        }
    }
}