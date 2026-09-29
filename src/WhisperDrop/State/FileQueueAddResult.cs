using System.Collections.Generic;

namespace WhisperDrop.State;

public sealed record FileQueueAddResult(int AddedCount, IReadOnlyList<string> UnsupportedFiles);
