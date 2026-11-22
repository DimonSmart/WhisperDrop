using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperDrop.Media;

public sealed class MediaPreparationService : IMediaPreparationService, IDisposable
{
    private readonly IFfmpegRuntime runtime;
    private readonly string temporaryDirectory;

    public MediaPreparationService(IFfmpegRuntime runtime)
        : this(runtime, Path.GetTempPath())
    {
    }

    // Each instance owns a private directory; startup never sweeps other sessions.
    internal MediaPreparationService(IFfmpegRuntime runtime, string temporaryRoot)
    {
        this.runtime = runtime;
        temporaryDirectory = Path.Combine(temporaryRoot, "ParakeetDrop", "media", Guid.NewGuid().ToString("N"));
    }

    internal string TemporaryDirectory => temporaryDirectory;

    public async Task<PreparedAudio> PrepareAsync(
        string sourcePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath))
        {
            throw MediaPreparationException.Create(
                MediaPreparationErrorKind.CannotOpenFile,
                new FileNotFoundException("Source media file does not exist.", sourcePath));
        }

        Directory.CreateDirectory(temporaryDirectory);
        var outputPath = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.wav");

        try
        {
            runtime.EnsureInitialized();
            var duration = await Task.Run(
                () => new FfmpegMediaDecoder().DecodeToWave(sourcePath, outputPath, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedAudio(outputPath, duration, deleteOnDispose: true);
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
