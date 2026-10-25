using WhisperDrop.State;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class WhisperRepetitionGuardTests
{
    [Theory]
    [InlineData("[HABLANDO EN INGLÉS] [HABLANDO EN INGLÉS] [HABLANDO EN INGLÉS]")]
    [InlineData("Habitualmente suele ser entre las ocho. Habitualmente suele ser entre las ocho. Habitualmente suele ser entre las ocho.")]
    [InlineData("Vamos a Cazorla en junio. Vamos a Cazorla en junio. Vamos a Cazorla en junio.")]
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
    public void Retry_is_preferred_when_it_fixes_a_loop_and_keeps_meaningful_text()
    {
        var original = "Comenzamos a las ocho y llegamos a Cazorla. " +
            string.Concat(System.Linq.Enumerable.Repeat("[HABLANDO EN INGLÉS] ", 50)) +
            "Por la tarde visitaremos el río.";
        var retry = "Comenzamos a las ocho y llegamos a Cazorla. " +
            "Now we will talk about the trip. Por la tarde visitaremos el río.";

        Assert.True(WhisperRepetitionGuard.PreferRetry(original, retry));
    }

    [Fact]
    public void Empty_or_truncated_retry_is_not_selected_just_for_lack_of_loops()
    {
        var original = "Una explicación completa sobre todas las actividades previstas. " +
            "Luego hablaremos de los horarios y del presupuesto. " +
            "Y seguiremos contando todos los detalles. " +
            "Una frase repetida tres veces. Una frase repetida tres veces. Una frase repetida tres veces.";
        Assert.False(WhisperRepetitionGuard.PreferRetry(original, "Una explicación completa."));
        Assert.False(WhisperRepetitionGuard.PreferRetry(original, ""));
    }

    [Fact]
    public void Equal_or_worse_retry_does_not_replace_original()
    {
        var original = "Una frase repetida varias veces. Una frase repetida varias veces. Una frase repetida varias veces.";
        Assert.False(WhisperRepetitionGuard.PreferRetry(original, original));
    }
}
