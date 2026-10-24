using System;
using System.IO;
using System.Threading.Tasks;
using Whisper.net;
using WhisperDrop.State;
using Xunit;

namespace WhisperDrop.Tests;

[Trait("Category", "LocalWhisper")]
public sealed class LocalWhisperIntegrationTests
{
    [Fact]
    public async Task Transcribes_when_explicitly_configured_local_model_is_present()
    {
        var model = Environment.GetEnvironmentVariable("WHISPERDROP_LOCAL_MODEL");
        var wav = Environment.GetEnvironmentVariable("WHISPERDROP_LOCAL_WAV");
        // This test is selected with Category=LocalWhisper; without explicit local paths it performs no recognition.
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(wav) || !File.Exists(model) || !File.Exists(wav)) return;

        using var service = new WhisperRecognitionService();
        var result = await service.TranscribeAsync(
            model,
            new RecognitionOptions { LanguageCode = "auto" },
            wav);
        Assert.NotNull(result.Transcript);
    }

    [Fact]
    public async Task CpuVad_initializes_and_detects_speech_on_the_installed_native_backend()
    {
        // Supply WHISPERDROP_LOCAL_VAD_MODEL to exercise the actual native VAD initialization
        // (including on macOS with Metal available). Never use GPU for this context.
        var vadModel = Environment.GetEnvironmentVariable("WHISPERDROP_LOCAL_VAD_MODEL");
        if (string.IsNullOrWhiteSpace(vadModel) || !File.Exists(vadModel))
        {
            return;
        }

        var wav = Environment.GetEnvironmentVariable("WHISPERDROP_LOCAL_WAV");
        if (string.IsNullOrWhiteSpace(wav) || !File.Exists(wav))
        {
            wav = Path.Combine(AppContext.BaseDirectory, "Fixtures", "test.wav");
        }

        using var factory = WhisperVadFactory.FromPath(vadModel);
        using var processor = WhisperRecognitionService.BuildCpuVadProcessor(factory);
        using var stream = File.OpenRead(wav);
        var segments = await processor.DetectSpeechAsync(stream);

        Assert.NotNull(segments);
    }
}
