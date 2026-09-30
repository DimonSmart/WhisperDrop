using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net.Ggml;
using WhisperDrop.Settings;

namespace WhisperDrop.State;

public interface IVadModelDownloader
{
    Task<Stream> OpenModelStreamAsync(CancellationToken cancellationToken);
}

public sealed class VadModelDownloader : IVadModelDownloader
{
    public Task<Stream> OpenModelStreamAsync(CancellationToken cancellationToken) =>
        WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync(
            SileroVadType.V6_2_0,
            cancellationToken);
}

public interface IVadModelManager
{
    string ModelPath { get; }

    bool IsAvailable { get; }

    Task DownloadAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class VadModelManager : IVadModelManager
{
    private readonly IApplicationPaths paths;
    private readonly IVadModelDownloader downloader;

    public VadModelManager(IApplicationPaths paths, IVadModelDownloader downloader)
    {
        this.paths = paths;
        this.downloader = downloader;
    }

    public string ModelPath => paths.VadModelPath;

    public bool IsAvailable => File.Exists(ModelPath);

    public async Task DownloadAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsAvailable)
        {
            progress?.Report(1);
            return;
        }

        Directory.CreateDirectory(paths.VadModelsFolder);
        var temporaryPath = ModelPath + ".download";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        progress?.Report(0);
        try
        {
            using var source = await downloader.OpenModelStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var target = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await source.CopyToAsync(target, 81920, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, ModelPath, true);
            progress?.Report(1);
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
