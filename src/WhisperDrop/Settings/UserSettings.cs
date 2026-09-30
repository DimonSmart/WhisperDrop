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
}
