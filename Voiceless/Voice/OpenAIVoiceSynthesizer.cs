using OpenAI;
using OpenAI.Audio;
using Serilog;
using Voiceless.Configuration;

namespace Voiceless.Voice;

public class OpenAIVoiceSynthesizer(OpenAIClient openAI, OpenAIConfiguration config) : IVoiceSynthesizer
{
    public string AudioFormat => "ogg";

    public async Task<Stream?> SynthesizeTextToSpeechAsync(string text, string voice, string? instructions = null)
    {
        try
        {
            Log.Debug("OpenAI: Synthesizing TTS for text of length {Length} with voice '{Voice}'{Instructions}", 
                text.Length, voice, instructions != null ? $" and instructions '{instructions}'" : "");
            var options = new SpeechGenerationOptions()
            {
                ResponseFormat = GeneratedSpeechFormat.Opus,
                SpeedRatio = 1f
            };
            
            if (!string.IsNullOrEmpty(instructions))
            {
#pragma warning disable OPENAI001 // Instructions is experimental
                options.Instructions = instructions;
#pragma warning restore OPENAI001
            }
            
            var result = await openAI.GetAudioClient(config.Model).GenerateSpeechAsync(text, voice, options);

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