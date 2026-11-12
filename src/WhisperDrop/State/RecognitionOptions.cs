using WhisperDrop.Settings;
namespace WhisperDrop.State;
public sealed record RecognitionOptions
{
    public ProcessingDevice ProcessingDevice { get; init; } = ProcessingDevice.Auto;
    public int? CpuThreads { get; init; }
}
