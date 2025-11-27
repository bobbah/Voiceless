using ElevenLabs;
using ElevenLabs.Models;
using ElevenLabs.Voices;
using Serilog;
using Voiceless.Configuration;

namespace Voiceless.Voice;

public class ElevenLabsVoiceSynthesizer(ElevenLabsConfiguration config) : IVoiceSynthesizer
{
    private readonly ElevenLabsClient _client = new(new ElevenLabsAuthentication(config.Token));
    private ElevenLabs.Voices.Voice _voice = null!;
    private Model _model = null!;

    public async Task ConfigureClient()
    {
        try
        {
            Log.Debug("ElevenLabs: Configuring client...");
            _voice = await GetConfiguredVoiceAsync();
            _model = await GetModelAsync();
            Log.Information("ElevenLabs: Client configured successfully with voice '{VoiceID}' and model '{ModelID}'", 
                _voice.Id, _model.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ElevenLabs: Failed to configure client");
            throw;
        }
    }

    public string AudioFormat => "mp3";

    public async Task<Stream?> SynthesizeTextToSpeechAsync(string text, string voice, string? instructions = null)
    {
        try
        {
            Log.Debug("ElevenLabs: Synthesizing TTS for text of length {Length}", text.Length);
            if (instructions != null)
            {
                Log.Debug("ElevenLabs: Instructions '{Instructions}' provided but not supported by ElevenLabs, ignoring", instructions);
            }
            var result = await _client.TextToSpeechEndpoint.TextToSpeechAsync(text, _voice, model: _model, voiceSettings: new VoiceSettings(config.Stability, config.Similarity));
            if (result == null)
            {
                Log.Warning("ElevenLabs: TTS synthesis returned null result");
                return null;
            }
            var toReturn = new MemoryStream();
            await toReturn.WriteAsync(result.ClipData);
            toReturn.Seek(0, SeekOrigin.Begin);
            Log.Debug("ElevenLabs: TTS synthesis completed, audio size: {Size} bytes", toReturn.Length);
            return toReturn;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ElevenLabs: Failed to synthesize TTS for text of length {Length}", text.Length);
            throw;
        }
    }

    private async Task<ElevenLabs.Voices.Voice> GetConfiguredVoiceAsync()
    {
        Log.Debug("ElevenLabs: Getting voice with ID '{VoiceID}'", config.VoiceID);
        var voice = await _client.VoicesEndpoint.GetVoiceAsync(config.VoiceID);
        if (voice == null)
        {
            Log.Error("ElevenLabs: Failed to get voice with configured ID '{VoiceID}'", config.VoiceID);
            throw new Exception($"Failed to get ElevenLabs Voice with configured id ('{config.VoiceID}')");
        }
        Log.Debug("ElevenLabs: Successfully retrieved voice '{VoiceName}'", voice.Name);
        return voice;
    }

    private async Task<Model> GetModelAsync()
    {
        Log.Debug("ElevenLabs: Getting model 'eleven_turbo_v2_5'");
        var model = (await _client.ModelsEndpoint.GetModelsAsync()).FirstOrDefault(x => x.Id == "eleven_turbo_v2_5");
        if (model == null)
        {
            Log.Error("ElevenLabs: Failed to get model 'eleven_turbo_v2_5'");
            throw new Exception($"Failed to get ElevenLabs Model with preferred id ('eleven_turbo_v2_5')");
        }
        Log.Debug("ElevenLabs: Successfully retrieved model '{ModelName}'", model.Name);
        return model;
    }
}