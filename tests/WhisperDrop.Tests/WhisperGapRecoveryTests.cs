using System;
using System.Collections.Generic;
using WhisperDrop.Models;
using WhisperDrop.State;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class WhisperGapRecoveryTests
{
    private static RecognitionSegment Segment(double start, double end, string text) =>
        new(0, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text,
            null, null, null, null);

    [Fact]
    public void Finds_large_gap_and_sparse_long_segment_but_not_normal_short_segments()
    {
        var timeline = new[]
        {
            Segment(0, 4, "We are going to talk about the first day and all of our activities."),
            Segment(27.72, 54.28, "Y también, que tienen un poquito de todo este tipo de cosillas."),
            Segment(54.28, 57, "Después podemos ir al río.")
        };

        var probes = WhisperGapRecovery.FindProbes(timeline, 100);
        Assert.Equal(2, probes.Count);
        Assert.Equal(GapProbeKind.MissingGap, probes[0].Kind);
        Assert.Equal(4, probes[0].Start.TotalSeconds);
        Assert.Equal(27.72, probes[0].End.TotalSeconds, 2);
        Assert.Equal(GapProbeKind.SparseSegment, probes[1].Kind);
        Assert.Equal(27.72, probes[1].Start.TotalSeconds, 2);
        Assert.Equal(54.28, probes[1].End.TotalSeconds, 2);
    }

    [Fact]
    public void Gap_probes_are_bounded_and_not_triggered_by_normal_pauses()
    {
        var timeline = new[]
        {
            Segment(0, 3, "First"),
            Segment(6, 8, "Second"),
            Segment(55, 58, "Third"),
            Segment(68, 71, "Fourth")
        };
        var probes = WhisperGapRecovery.FindProbes(timeline, 100);
        Assert.Single(probes);
        Assert.Equal(58, probes[0].Start.TotalSeconds);
        Assert.Equal(68, probes[0].End.TotalSeconds);
    }

    [Fact]
    public void Sound_detection_is_not_triggered_by_silence_but_detects_substantial_sound()
    {
        var silence = new float[16000 * 6];
        var speechLikeSound = new float[16000 * 6];
        for (var i = 0; i < speechLikeSound.Length; i++)
        {
            speechLikeSound[i] = 0.1f;
        }

        Assert.False(WhisperGapRecovery.MeasureSignal(silence).HasSubstantialSound);
        Assert.True(WhisperGapRecovery.MeasureSignal(speechLikeSound).HasSubstantialSound);
    }

    [Fact]
    public void Two_independently_agreeing_candidates_may_be_selected_for_a_real_gap()
    {
        const string spoken =
            "Los niños van a comer paella, carne de ciervo, macarrones y también comida saludable.";
        var greedy = WhisperGapRecovery.Assess("greedy",
            [Segment(0, 10, spoken)], 10);
        var beam = WhisperGapRecovery.Assess("beam",
            [Segment(0, 10,
                "Los niños van a comer paella, carne de ciervo, macarrones y comida saludable.")], 10);
        var split = WhisperGapRecovery.Assess("split-greedy",
            [Segment(0, 10, "Buenas tardes, todos.")], 10);

        var selected = WhisperGapRecovery.Choose(
            [greedy, beam, split], GapProbeKind.MissingGap, "",
            "Viajaremos en autobús.", "Después iremos de excursión.");

        Assert.NotNull(selected.Candidate);
        Assert.Equal("two-decoders-agree", selected.Reason);
        Assert.True(selected.Agreement > 0.58);
    }

    [Fact]
    public void Single_long_hallucination_does_not_replace_short_agreeing_candidates()
    {
        var greedy = WhisperGapRecovery.Assess("greedy",
            [Segment(0, 18, "This is an imaginary discussion about the stars and the moon and the universe.")], 18);
        var beam = WhisperGapRecovery.Assess("beam",
            [Segment(0, 18, "There are children playing at the camp today in the summer.")], 18);
        var split = WhisperGapRecovery.Assess("split-greedy",
            [Segment(0, 18, "Tomorrow we will go to the river and have a great time.")], 18);
        Assert.Null(WhisperGapRecovery.Choose(
            [greedy, beam, split], GapProbeKind.MissingGap, "", null, null).Candidate);
    }

    [Fact]
    public void Short_sparse_segment_is_preserved_without_substantial_agreed_improvement()
    {
        const string baseline = "Después vamos todos caminando por el río, que es muy bonito y tiene muchas piedras pequeñas y algunas plantas.";
        var same = WhisperGapRecovery.Assess("greedy",
            [Segment(0, 22, baseline)], 22);
        var second = WhisperGapRecovery.Assess("beam",
            [Segment(0, 22, baseline)], 22);

        var choice = WhisperGapRecovery.Choose(
            [same, second], GapProbeKind.SparseSegment, baseline, null, null);
        Assert.Null(choice.Candidate);
        Assert.Equal("insufficient-improvement-over-original", choice.Reason);
    }

    [Fact]
    public void Independent_agreement_can_replace_an_obviously_sparse_segment()
    {
        const string original = "Y después veremos las actividades.";
        const string detailed =
            "Los niños comerán paella y carne de ciervo, y también podrán elegir macarrones con tomate. " +
            "Si alguien tiene problemas de estómago conviene llevar un medicamento adecuado y consultar a sus padres.";
        var greedy = WhisperGapRecovery.Assess("greedy", [Segment(0, 24, detailed)], 24);
        var beam = WhisperGapRecovery.Assess("beam", [Segment(0, 24, detailed)], 24);
        var chosen = WhisperGapRecovery.Choose(
            [greedy, beam], GapProbeKind.SparseSegment, original, null, null);

        Assert.True(greedy.Plausible);
        Assert.NotNull(chosen.Candidate);
        Assert.Equal("two-decoders-agree", chosen.Reason);
        Assert.Equal(1, chosen.Agreement);
    }

    [Fact]
    public void Non_speech_markers_are_not_a_valid_recovery_even_when_decoders_agree()
    {
        var first = WhisperGapRecovery.Assess("greedy",
            [Segment(0, 20, "[MÚSICA] [MÚSICA] [MÚSICA]")], 20);
        var second = WhisperGapRecovery.Assess("beam",
            [Segment(0, 20, "[MÚSICA] [MÚSICA] [MÚSICA]")], 20);

        var chosen = WhisperGapRecovery.Choose(
            [first, second], GapProbeKind.MissingGap, "", null, null);

        Assert.False(first.Plausible);
        Assert.Null(chosen.Candidate);
    }

    [Fact]
    public void Duplicate_of_adjacent_speech_is_not_inserted_into_gap()
    {
        const string neighbor =
            "La actividad de senderismo se hace por el río, todos los chicos llevan zapatillas deportivas.";
        var a = WhisperGapRecovery.Assess("greedy", [Segment(0, 12, neighbor)], 12);
        var b = WhisperGapRecovery.Assess("beam", [Segment(0, 12, neighbor)], 12);
        var result = WhisperGapRecovery.Choose(
            [a, b], GapProbeKind.MissingGap, "", neighbor, "Después volvemos al colegio.");

        Assert.Null(result.Candidate);
        Assert.Equal("candidate-duplicates-adjacent-speech", result.Reason);
    }

    [Fact]
    public void Window_normalization_rejects_text_outside_gap_and_clips_small_overhangs()
    {
        var probe = new GapProbe(1, GapProbeKind.MissingGap,
            TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(110));
        var normalized = WhisperGapRecovery.NormalizeWindowSegments(
            [Segment(0, 4, "First"), Segment(4, 10.25, "Second"),
             Segment(11, 13, "Outside")], probe);

        Assert.Equal(2, normalized.Count);
        Assert.Equal(100, normalized[0].Start.TotalSeconds);
        Assert.Equal(110, normalized[1].End.TotalSeconds);
    }
}
