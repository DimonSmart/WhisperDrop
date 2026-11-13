using WhisperDrop.Settings;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class SettingsNavigationTests
{
    [Theory]
    [InlineData("GPU", SettingsSection.Recognition)]
    [InlineData("threads", SettingsSection.Recognition)]
    [InlineData("download", SettingsSection.Models)]
    [InlineData("models folder", SettingsSection.Models)]
    [InlineData("shortcut", SettingsSection.Dictation)]
    [InlineData("Ollama", SettingsSection.Ai)]
    [InlineData("API key", SettingsSection.Ai)]
    public void Search_opens_the_relevant_section(string query, SettingsSection expected) =>
        Assert.Equal(expected, SettingsNavigationCatalog.FirstMatch(query));

    [Fact]
    public void Search_preserves_every_category_and_dims_only_non_matches()
    {
        Assert.Equal(4, SettingsNavigationCatalog.Order.Count);
        Assert.True(SettingsNavigationCatalog.Matches(SettingsSection.Recognition, "model"));
        Assert.True(SettingsNavigationCatalog.Matches(SettingsSection.Ai, "model"));
        Assert.False(SettingsNavigationCatalog.Matches(SettingsSection.Dictation, "model"));
        Assert.Null(SettingsNavigationCatalog.FirstMatch("not-a-setting"));
    }
}
