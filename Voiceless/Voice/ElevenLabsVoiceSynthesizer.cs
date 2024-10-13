using ElevenLabs;
using ElevenLabs.Models;
using ElevenLabs.Voices;
using Voiceless.Configuration;

namespace Voiceless.Voice;

public class ElevenLabsVoiceSynthesizer(ElevenLabsConfiguration config) : IVoiceSynthesizer
{
    private readonly ElevenLabsClient _client = new(new ElevenLabsAuthentication(config.Token));
    private ElevenLabs.Voices.Voice _voice = null!;
    private Model _model = null!;

    public async Task ConfigureClient()
    {
        _voice = await GetConfiguredVoiceAsync();
        _model = await GetModelAsync();
    }

    public string AudioFormat => "mp3";

    public async Task<Stream?> SynthesizeTextToSpeechAsync(string text)
    {
        var result = await _client.TextToSpeechEndpoint.TextToSpeechAsync(text, _voice, model: _model, voiceSettings: new VoiceSettings(config.Stability, config.Similarity));
        if (result == null)
            return null;
        var toReturn = new MemoryStream();
        await toReturn.WriteAsync(result.ClipData);
        toReturn.Seek(0, SeekOrigin.Begin);
        return toReturn;
    }

    private async Task<ElevenLabs.Voices.Voice> GetConfiguredVoiceAsync()
    {
        var voice = await _client.VoicesEndpoint.GetVoiceAsync(config.VoiceID);
        if (voice == null)
            throw new Exception($"Failed to get ElevenLabs Voice with configured id ('{config.VoiceID}')");
        return voice;
    }

    private async Task<Model> GetModelAsync()
    {
        var model = (await _client.ModelsEndpoint.GetModelsAsync()).FirstOrDefault(x => x.Id == "eleven_turbo_v2_5");
        if (model == null)
            throw new Exception($"Failed to get ElevenLabs Model with preferred id ('eleven_turbo_v2_5')");
        return model;
    }
}