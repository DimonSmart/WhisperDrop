using System;
using System.IO;
using System.Threading.Tasks;
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
}
