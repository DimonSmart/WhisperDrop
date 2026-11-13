using System;
using System.Collections.Generic;
using System.Linq;

namespace WhisperDrop.Settings;

public enum SettingsSection
{
    Recognition,
    Models,
    Dictation,
    Ai
}

public static class SettingsNavigationCatalog
{
    public static IReadOnlyList<SettingsSection> Order { get; } =
        [SettingsSection.Recognition, SettingsSection.Models, SettingsSection.Dictation, SettingsSection.Ai];

    private static readonly IReadOnlyDictionary<SettingsSection, string[]> SearchTerms =
        new Dictionary<SettingsSection, string[]>
        {
            [SettingsSection.Recognition] =
                ["recognition", "transcription", "model selection", "parakeet", "processing device", "cpu", "gpu", "threads", "metal", "vulkan", "performance"],
            [SettingsSection.Models] =
                ["models", "storage", "models folder", "download", "delete", "installed models", "disk usage", "browse", "location", "files"],
            [SettingsSection.Dictation] =
                ["dictation", "push-to-talk", "microphone", "hotkey", "shortcut", "record", "background", "insert text", "windows"],
            [SettingsSection.Ai] =
                ["ai", "post-processing", "improve transcript", "correction", "instructions", "ollama", "provider", "model", "endpoint", "api key", "context size", "test connection"]
        };

    public static bool Matches(SettingsSection section, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return SearchTerms[section].Any(term =>
            term.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static SettingsSection? FirstMatch(string? query) =>
        Order.FirstOrDefault(section => Matches(section, query)) is var first &&
        (!string.IsNullOrWhiteSpace(query) && Matches(first, query))
            ? first
            : null;
}
