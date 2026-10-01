using System;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperDrop.Media;

public interface IMediaPreparationService
{
    Task<PreparedAudio> PrepareAsync(
        string sourcePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

internal sealed class DirectMediaPreparationService : IMediaPreparationService
{
    public Task<PreparedAudio> PrepareAsync(
        string sourcePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(1);
        return Task.FromResult(PreparedAudio.Borrowed(sourcePath));
    }
}
