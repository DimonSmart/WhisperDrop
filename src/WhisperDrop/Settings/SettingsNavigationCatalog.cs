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
                ["recognition", "recognition model", "recognition performance", "transcription", "model selection", "parakeet", "processing device", "cpu threads", "cpu", "gpu", "threads", "metal", "vulkan", "performance"],
            [SettingsSection.Models] =
                ["models", "models & storage", "storage", "models folder", "download", "download model", "downloaded models", "delete", "delete all", "installed models", "disk usage", "browse", "location", "files"],
            [SettingsSection.Dictation] =
                ["dictation", "push-to-talk", "push-to-talk dictation", "microphone", "hotkey", "shortcut", "hold shortcut to record", "record", "background", "enable background dictation", "insert text", "insert text into the original field after recognition", "windows"],
            [SettingsSection.Ai] =
                ["ai", "ai post-processing", "post-processing", "improve transcript", "improve transcript with ai", "custom correction instructions", "technical settings", "correction", "instructions", "ollama", "provider", "model", "ai model", "endpoint", "api key", "context size", "test connection"]
        };

    public static bool Matches(SettingsSection section, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return SearchTerms[section].Any(term =>
            term.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static SettingsSection? FirstMatch(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        foreach (var section in Order)
        {
            if (Matches(section, query)) return section;
        }
        return null;
    }
}
