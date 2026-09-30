using WhisperDrop.Settings;

namespace WhisperDrop.State;

public sealed record RecognitionOptions
{
    public string LanguageCode { get; init; } = "auto";

    public TranscriptionTask Task { get; init; } = TranscriptionTask.Transcribe;

    public string? Prompt { get; init; }

    public bool SkipSilence { get; init; }

    public ProcessingDevice ProcessingDevice { get; init; } = ProcessingDevice.Auto;

    public int? CpuThreads { get; init; }
}
