namespace WhisperDrop.Settings;

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

    public string Model { get; init; } = "gpt-oss:latest";

    public string Instructions { get; init; } = string.Empty;

    public int? ContextSize { get; init; }
}

public sealed record DictationSettings
{
    public bool Enabled { get; init; } = true;
    public string Hotkey { get; init; } = "Ctrl+Shift+Space";
    public bool InsertAfterRecognition { get; init; } = true;
}

public sealed record UserSettings
{
    public string? ModelsFolder { get; init; }

    public string SelectedModelId { get; init; } = "parakeet-ultra-q8_0";

    public ProcessingDevice ProcessingDevice { get; init; } = ProcessingDevice.Auto;

    public int? CpuThreads { get; init; }

    public AiPostProcessingSettings AiPostProcessing { get; init; } = new();

    public DictationSettings Dictation { get; init; } = new();
}
