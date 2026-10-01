namespace WhisperDrop.Settings;

public enum TranscriptionTask
{
    Transcribe,
    TranslateToEnglish
}

public enum ProcessingDevice
{
    Auto,
    Cpu,
    Gpu
}

public enum AiProviderPreset
{
    Ollama,
    CustomOpenAiCompatible
}

public sealed record AiPostProcessingSettings
{
    public bool Enabled { get; init; }

    public AiProviderPreset Provider { get; init; } = AiProviderPreset.Ollama;

    public string Endpoint { get; init; } = "http://localhost:11434/v1/";

    public string Model { get; init; } = "gpt-oss:20b";

    public string Instructions { get; init; } = string.Empty;

    public int? ContextSize { get; init; }
}

public sealed record UserSettings
{
    public string? ModelsFolder { get; init; }

    public string SelectedModelId { get; init; } = "base";

    public string RecognitionLanguageCode { get; init; } = "auto";

    public TranscriptionTask Task { get; init; } = TranscriptionTask.Transcribe;

    public string VocabularyContext { get; init; } = string.Empty;

    public bool SkipSilence { get; init; }

    public ProcessingDevice ProcessingDevice { get; init; } = ProcessingDevice.Auto;

    public int? CpuThreads { get; init; }

    public AiPostProcessingSettings AiPostProcessing { get; init; } = new();
}
