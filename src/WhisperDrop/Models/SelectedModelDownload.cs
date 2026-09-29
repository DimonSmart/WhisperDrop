using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net.Ggml;

namespace WhisperDrop.Models;

public enum SelectedModelDownloadState
{
    NotDownloaded,
    Downloading,
    Downloaded,
    Error
}

public sealed record ModelDownloadProgress(long BytesReceived, long? TotalBytes);

public interface ISelectedModelDownloader
{
    Task DownloadAsync(RecognitionModel model, Stream destination, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken);
}

public sealed class SelectedModelDownloader : ISelectedModelDownloader
{
    public async Task DownloadAsync(RecognitionModel model, Stream destination, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        await using var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model.GgmlType, cancellationToken: cancellationToken);
        long? totalBytes = source.CanSeek ? source.Length : null;
        var buffer = new byte[81920];
        long received = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            progress?.Report(new ModelDownloadProgress(received, totalBytes));
        }
    }
}

public interface ISelectedModelDownloadManager
{
    Task DownloadAsync(string modelsFolder, string modelId, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken);
}

public sealed class SelectedModelDownloadManager : ISelectedModelDownloadManager
{
    private readonly IWhisperModelCatalog catalog;
    private readonly ISelectedModelAvailability availability;
    private readonly ISelectedModelDownloader downloader;

    public SelectedModelDownloadManager(
        IWhisperModelCatalog catalog,
        ISelectedModelAvailability availability,
        ISelectedModelDownloader downloader)
    {
        this.catalog = catalog;
        this.availability = availability;
        this.downloader = downloader;
    }

    public async Task DownloadAsync(string modelsFolder, string modelId, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (availability.IsAvailable(modelsFolder, modelId))
        {
            return;
        }

        var model = catalog.Get(modelId);
        Directory.CreateDirectory(modelsFolder);
        var finalPath = availability.GetModelPath(modelsFolder, modelId);
        var temporaryPath = $"{finalPath}.{Guid.NewGuid():N}.download";

        try
        {
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await downloader.DownloadAsync(model, destination, progress, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, finalPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
