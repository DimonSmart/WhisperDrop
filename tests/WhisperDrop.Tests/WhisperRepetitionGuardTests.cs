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

    [Fact]
    public void Tail_limits_work_to_recent_text_only()
    {
        var text = new string('x', 6000) + " Repetir tres palabras. Repetir tres palabras. Repetir tres palabras.";
        var tail = WhisperRepetitionGuard.Tail(text);
        Assert.Equal(4096, tail.Length);
        Assert.True(WhisperRepetitionGuard.CountRepeatedTokens(tail) > 0);
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

}
