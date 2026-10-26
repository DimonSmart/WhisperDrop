using WhisperDrop.State;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class WhisperRepetitionGuardTests
{
    [Theory]
    [InlineData("[HABLANDO EN INGLÉS] [HABLANDO EN INGLÉS] [HABLANDO EN INGLÉS]")]
    [InlineData("Habitualmente suele ser entre las ocho. Habitualmente suele ser entre las ocho. Habitualmente suele ser entre las ocho.")]
    [InlineData("Vamos a Cazorla en junio. Vamos a Cazorla en junio. Vamos a Cazorla en junio.")]
    [InlineData("Y ahora mismo voy a traducirlo en inglés pero creo que es un poco difícil. Y ahora mismo voy a traducirlo en inglés pero creo que es un poco difícil. Y ahora mismo voy a traducirlo en inglés pero creo que es un poco difícil.")]
    public void Three_identical_word_groups_trigger_retry(string text)
    {
        Assert.True(WhisperRepetitionGuard.CountRepeatedTokens(text) >= 6);
    }

    [Theory]
    [InlineData("No, no, no. Se trata de una explicación normal.")]
    [InlineData("Vamos mañana. Vamos mañana. Después regresamos.")]
    [InlineData("Hoy tenemos clase y mañana tenemos más actividades distintas.")]
    [InlineData("")]
    public void Short_or_nonconsecutive_repetition_does_not_trigger_retry(string text)
    {
        Assert.Equal(0, WhisperRepetitionGuard.CountRepeatedTokens(text));
    }

    [Fact]
    public void Detection_works_incrementally_across_short_native_segments()
    {
        var partial = "";
        partial += "Tenemos actividades y luego la tirolina.";
        Assert.Equal(0, WhisperRepetitionGuard.CountRepeatedTokens(partial));
        partial += " Es una tirolina.";
        Assert.Equal(0, WhisperRepetitionGuard.CountRepeatedTokens(partial));
        partial += " Es una tirolina.";
        Assert.Equal(0, WhisperRepetitionGuard.CountRepeatedTokens(partial));
        partial += " Es una tirolina.";
        Assert.True(WhisperRepetitionGuard.CountRepeatedTokens(partial) > 0);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 16000)]
    [InlineData(2.5, 40000)]
    public void Audio_offsets_are_converted_to_samples(double seconds, int expected) =>
        Assert.Equal(expected, WhisperRecognitionService.ToSample(System.TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Local_repair_continues_from_last_recognized_segment_boundary()
    {
        Assert.Equal(560000, WhisperRecognitionService.ChooseAdvance(720000, System.TimeSpan.FromSeconds(35)));
        Assert.Equal(128000, WhisperRecognitionService.ChooseAdvance(720000, System.TimeSpan.FromSeconds(0)));
        Assert.Equal(128000, WhisperRecognitionService.ChooseAdvance(720000, null));
        Assert.Equal(8000, WhisperRecognitionService.ChooseAdvance(720000, System.TimeSpan.FromSeconds(0.5)));
    }
    [Theory]
    [InlineData("[MÚSICA] [MÚSICA]")]
    [InlineData("[SPEAKING SPANISH] [SPEAKING SPANISH]")]
    [InlineData("[MUSIC] [MUSIC]")]
    public void Consecutive_nonspeech_markers_are_an_early_loop_signal(string text)
    {
        Assert.True(WhisperRepetitionGuard.HasMarkerLoop(text));
        Assert.True(WhisperRepetitionGuard.IsSuspiciousRepair(text, 25));
        Assert.Equal(0, WhisperRepetitionGuard.MeaningfulWords(text));
    }

    [Fact]
    public void Valid_repair_is_preferred_to_marker_only_beam_output()
    {
        var markers = "[MÚSICA] [MÚSICA] [MÚSICA]";
        var recovered = "Al llegar al campamento vamos a repartir las cabañas y después explicaremos todas las actividades.";
        Assert.True(WhisperRepetitionGuard.PreferShortRecovery(markers, recovered, 25));
        Assert.False(WhisperRepetitionGuard.IsSuspiciousRepair(recovered, 25));
    }

    [Fact]
    public void Normal_speech_does_not_trigger_sparse_repair()
    {
        const string speech = "El autobús sale a las ocho de la mañana, después paramos en Granada y finalmente llegamos al camping.";
        Assert.False(WhisperRepetitionGuard.IsSuspiciousRepair(speech, 25));
        Assert.False(WhisperRepetitionGuard.HasMarkerLoop("[MÚSICA] Después volvemos a hablar [MÚSICA]"));
    }

    [Fact]
    public void Too_little_recognized_speech_is_suspicious_but_not_proof_of_loss()
    {
        Assert.True(WhisperRepetitionGuard.IsSuspiciousRepair("Hola.", 25));
        Assert.True(WhisperRepetitionGuard.IsSuspiciousRepair("", 25));
        Assert.False(WhisperRepetitionGuard.IsSuspiciousRepair("Hola.", 2));
    }

    [Fact]
    public void Coverage_reports_22_second_tail_after_only_three_seconds_of_beam_output()
    {
        var segments = new[]
        {
            new WhisperDrop.Models.RecognitionSegment(0,
                System.TimeSpan.Zero, System.TimeSpan.FromSeconds(3),
                "The first sentence.", null, null, null, null)
        };

        var coverage = WhisperTimingDiagnostics.AnalyzeCoverage(segments, 25);

        Assert.Equal(3, coverage.CoveredSeconds);
        Assert.Equal(22, coverage.TrailingGapSeconds);
        Assert.Equal(0, coverage.OutsideWindowSeconds);
        Assert.Equal(0, coverage.LeadingGapSeconds);
    }

    [Fact]
    public void Coverage_reports_native_segment_end_beyond_repair_window()
    {
        var segments = new[]
        {
            new WhisperDrop.Models.RecognitionSegment(0,
                System.TimeSpan.Zero, System.TimeSpan.FromSeconds(26.96),
                "One unusually long native segment.", null, null, null, null)
        };

        var coverage = WhisperTimingDiagnostics.AnalyzeCoverage(segments, 25);

        Assert.Equal(25, coverage.CoveredSeconds);
        Assert.Equal(26.96, coverage.LastEndSeconds, 2);
        Assert.Equal(1.96, coverage.OutsideWindowSeconds, 2);
    }

    [Fact]
    public void Coverage_merges_overlapping_segment_timestamps_without_double_counting()
    {
        var segments = new[]
        {
            new WhisperDrop.Models.RecognitionSegment(0,
                System.TimeSpan.FromSeconds(1), System.TimeSpan.FromSeconds(6),
                "First.", null, null, null, null),
            new WhisperDrop.Models.RecognitionSegment(1,
                System.TimeSpan.FromSeconds(4), System.TimeSpan.FromSeconds(8),
                "Second.", null, null, null, null),
            new WhisperDrop.Models.RecognitionSegment(2,
                System.TimeSpan.FromSeconds(12), System.TimeSpan.FromSeconds(15),
                "Third.", null, null, null, null)
        };

        var coverage = WhisperTimingDiagnostics.AnalyzeCoverage(segments, 20);

        Assert.Equal(10, coverage.CoveredSeconds);
        Assert.Equal(4, coverage.LargestInternalGapSeconds);
        Assert.Equal(1, coverage.LeadingGapSeconds);
        Assert.Equal(5, coverage.TrailingGapSeconds);
    }

    [Fact]
    public void Accepted_segment_diagnostics_report_long_segments_and_large_timestamp_gaps()
    {
        var earlier = new WhisperDrop.Models.RecognitionSegment(0,
            System.TimeSpan.FromSeconds(0), System.TimeSpan.FromSeconds(4),
            "Short.", null, null, null, null);
        var later = new WhisperDrop.Models.RecognitionSegment(1,
            System.TimeSpan.FromSeconds(14), System.TimeSpan.FromSeconds(35),
            "Long.", null, null, null, null);

        var diagnostic = WhisperTimingDiagnostics.ExplainAcceptedSegment(earlier, later, 100);

        Assert.Contains("large-gap duration=10.00s", diagnostic);
        Assert.Contains("long-segment duration=21.00s", diagnostic);
        Assert.Null(WhisperTimingDiagnostics.ExplainAcceptedSegment(
            earlier, earlier with
            {
                Id = 1,
                Start = System.TimeSpan.FromSeconds(4),
                End = System.TimeSpan.FromSeconds(6)
            }, 100));
    }

}
