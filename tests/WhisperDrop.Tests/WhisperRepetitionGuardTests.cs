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
        Assert.Equal(720000, WhisperRecognitionService.ChooseAdvance(720000, System.TimeSpan.FromSeconds(0)));
        Assert.Equal(720000, WhisperRecognitionService.ChooseAdvance(720000, null));
    }
}
