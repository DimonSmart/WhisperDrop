using WhisperDrop.Settings;

namespace WhisperDrop.Models;

public sealed record TranscriptionTaskOption(TranscriptionTask Value, string DisplayName);

public sealed record ProcessingDeviceOption(ProcessingDevice Value, string DisplayName);

public sealed record CpuThreadsOption(int? Value, string DisplayName);
