using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;

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
    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client;
    public SelectedModelDownloader() : this(SharedClient) { }
    public SelectedModelDownloader(HttpClient client) => this.client = client;

    public async Task DownloadAsync(RecognitionModel model, Stream destination, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new IOException("The pinned model artifact is missing. Check for an application update.");
            if ((int)response.StatusCode == 429)
                throw new IOException("The model host is rate limiting downloads. Wait and retry.");
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) break;
                received += read;
                if (received > model.SizeBytes) throw new IOException("The model download exceeds the pinned size. Retry the download.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress?.Report(new ModelDownloadProgress(received, model.SizeBytes));
            }
        }
        catch (HttpRequestException)
        {
            throw new IOException("The model download failed due to TLS or network connectivity. Check your connection and retry.");
        }
    }
}

public interface ISelectedModelDownloadManager
{
    Task DownloadAsync(string modelsFolder, string modelId, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken);
}

public sealed class SelectedModelDownloadManager : ISelectedModelDownloadManager
{
    private readonly SemaphoreSlim downloadGate = new(1, 1);
    private readonly IRecognitionModelCatalog catalog;
    private readonly ISelectedModelAvailability availability;
    private readonly ISelectedModelDownloader downloader;

    public SelectedModelDownloadManager(
        IRecognitionModelCatalog catalog,
        ISelectedModelAvailability availability,
        ISelectedModelDownloader downloader)
    {
        this.catalog = catalog;
        this.availability = availability;
        this.downloader = downloader;
    }

    public async Task DownloadAsync(string modelsFolder, string modelId, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        await downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await DownloadCoreAsync(modelsFolder, modelId, progress, cancellationToken).ConfigureAwait(false); }
        finally { downloadGate.Release(); }
    }

    private async Task DownloadCoreAsync(string modelsFolder, string modelId, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await Task.Run(() => availability.IsAvailable(modelsFolder, modelId), cancellationToken).ConfigureAwait(false))
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

            if (!await ModelIntegrity.VerifyAsync(temporaryPath, model, cancellationToken))
                throw new IOException("Model integrity verification failed (size or SHA-256). Retry the download.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath, overwrite: true);
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
