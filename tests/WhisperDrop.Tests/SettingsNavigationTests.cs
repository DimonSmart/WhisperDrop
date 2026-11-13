using WhisperDrop.Settings;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class SettingsNavigationTests
{
    [Theory]
    [InlineData("tray", SettingsSection.General)]
    [InlineData("close", SettingsSection.General)]
    [InlineData("hide to tray", SettingsSection.General)]
    [InlineData("GPU", SettingsSection.Recognition)]
    [InlineData("threads", SettingsSection.Recognition)]
    [InlineData("CPU threads", SettingsSection.Recognition)]
    [InlineData("Recognition model", SettingsSection.Recognition)]
    [InlineData("download", SettingsSection.Models)]
    [InlineData("models folder", SettingsSection.Models)]
    [InlineData("Downloaded models", SettingsSection.Models)]
    [InlineData("shortcut", SettingsSection.Dictation)]
    [InlineData("Hold shortcut to record", SettingsSection.Dictation)]
    [InlineData("Ollama", SettingsSection.Ai)]
    [InlineData("AI model", SettingsSection.Ai)]
    [InlineData("Custom correction instructions", SettingsSection.Ai)]
    [InlineData("API key", SettingsSection.Ai)]
    public void Search_opens_the_relevant_section(string query, SettingsSection expected) =>
        Assert.Equal(expected, SettingsNavigationCatalog.FirstMatch(query));

    [Fact]
    public void Search_preserves_every_category_and_dims_only_non_matches()
    {
        Assert.Equal(5, SettingsNavigationCatalog.Order.Count);
        Assert.True(SettingsNavigationCatalog.Matches(SettingsSection.Recognition, "model"));
        Assert.True(SettingsNavigationCatalog.Matches(SettingsSection.Ai, "model"));
        Assert.False(SettingsNavigationCatalog.Matches(SettingsSection.Dictation, "model"));
        Assert.Null(SettingsNavigationCatalog.FirstMatch("not-a-setting"));
    }
}
