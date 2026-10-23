using System;
using System.Linq;
using WhisperDrop.Models;
using WhisperDrop.State;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class WhisperExperimentTests
{
    [Fact]
    public void Suite_uses_independent_variants_without_requiring_new_settings()
    {
        var options = new RecognitionOptions { LanguageCode = "es" };
        var experiments = WhisperRecognitionService.CreateExperimentPlan(options, vadAvailable: false);

        Assert.Equal(6, experiments.Count);
        Assert.Equal(
            new[]
            {
                WhisperDecoderExperiment.Original,
                WhisperDecoderExperiment.StrictFallback,
                WhisperDecoderExperiment.BeamSearch,
                WhisperDecoderExperiment.NoPromptTemperature,
                WhisperDecoderExperiment.SilenceFiltering,
                WhisperDecoderExperiment.Original
            },
            experiments.Select(experiment => experiment.Decoder));
        Assert.Equal(Enumerable.Range(1, 6), experiments.Select(experiment => experiment.Number));
        Assert.All(experiments.Take(5), experiment => Assert.False(experiment.SkipSilence));
        Assert.NotNull(experiments[5].SkipReason);
        Assert.False(experiments[3].UsePrompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Last_experiment_reverses_selected_VAD_state_when_available(bool skipSilence)
    {
        var experiments = WhisperRecognitionService.CreateExperimentPlan(
            new RecognitionOptions { SkipSilence = skipSilence }, vadAvailable: true);

        Assert.All(experiments.Take(5), experiment => Assert.Equal(skipSilence, experiment.SkipSilence));
        Assert.Equal(!skipSilence, experiments[5].SkipSilence);
        Assert.Null(experiments[5].SkipReason);
    }

    [Fact]
    public void Heading_contains_variant_and_context_information_for_copying()
    {
        var options = new RecognitionOptions { LanguageCode = "es", Prompt = "WhisperDrop" };
        var experiments = WhisperRecognitionService.CreateExperimentPlan(options, vadAvailable: true);

        var baseline = WhisperRecognitionService.FormatExperimentHeader(experiments[0], 6, options, "/models/ggml-small.bin");
        var fourth = WhisperRecognitionService.FormatExperimentHeader(experiments[3], 6, options, "/models/ggml-small.bin");

        Assert.Contains("EXPERIMENT 1/6", baseline);
        Assert.Contains("ggml-small.bin", baseline);
        Assert.Contains("prompt: on", baseline);
        Assert.Contains("Native previous-text context: off", baseline);
        Assert.Contains("EXPERIMENT 4/6", fourth);
        Assert.Contains("temperature=0.2", fourth);
        Assert.Contains("prompt: off", fourth);
    }

    [Fact]
    public void Diagnostics_expose_both_long_segment_and_cross_segment_repetition()
    {
        const string repeated = "Habitualmente suele ser entre las 8 y 10, 8 y 20. ";
        var longSegment = string.Concat(Enumerable.Repeat(repeated, 12));
        Assert.True(WhisperRecognitionService.GetGzipCompressionRatio(longSegment) > 2.4);
        Assert.True(WhisperRecognitionService.GetGzipCompressionRatio(repeated) < 2.4);

        var segments = Enumerable.Range(0, 3)
            .Select(id => new RecognitionSegment(
                id, TimeSpan.FromSeconds(id), TimeSpan.FromSeconds(id + 1),
                repeated, null, null, null, null))
            .Append(new RecognitionSegment(
                3, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4),
                longSegment, null, null, null, null))
            .ToArray();
        var result = new RecognitionResult("sample", "es", segments);

        var diagnostics = WhisperRecognitionService.FormatExperimentDiagnostics(result, TimeSpan.FromSeconds(4));
        Assert.Contains("segments=4", diagnostics);
        Assert.Contains("longest identical adjacent segment run=3", diagnostics);
        Assert.Contains("max segment gzip ratio=", diagnostics);
        Assert.Contains("elapsed=4.0s", diagnostics);
    }
}
