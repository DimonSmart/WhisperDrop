using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperDrop.Media;

public sealed class MediaPreparationService : IMediaPreparationService
{
    private readonly IFfmpegRuntime runtime;
    private readonly string temporaryDirectory;

    public MediaPreparationService(IFfmpegRuntime runtime)
    {
        this.runtime = runtime;
        temporaryDirectory = Path.Combine(Path.GetTempPath(), "WhisperDrop", "media");
        TryCleanStaleFiles();
    }

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

    private void TryCleanStaleFiles()
    {
        try
        {
            if (!Directory.Exists(temporaryDirectory))
                return;

            foreach (var file in Directory.EnumerateFiles(temporaryDirectory, "*.wav", SearchOption.TopDirectoryOnly))
                TryDelete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
