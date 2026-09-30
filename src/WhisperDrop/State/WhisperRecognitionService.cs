using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace WhisperDrop.State;

public sealed record RecognitionResult(string Transcript, string? DetectedLanguage);

public interface IRecognitionService : IDisposable
{
    Task<RecognitionResult> TranscribeAsync(string modelPath, string languageCode, string audioPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    void UnloadModel();
}

/// <summary>Owns exactly one loaded Whisper factory for the current selected model.</summary>
public sealed class WhisperRecognitionService : IRecognitionService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private WhisperFactory? factory;
    private string? factoryModelPath;

    public async Task<RecognitionResult> TranscribeAsync(string modelPath, string languageCode, string audioPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(factoryModelPath, modelPath, StringComparison.Ordinal))
            {
                UnloadModel();
                factory = await Task.Run(() => WhisperFactory.FromPath(modelPath), cancellationToken).ConfigureAwait(false);
                factoryModelPath = modelPath;
            }

            progress?.Report(0);
            return await Task.Run(async () =>
            {
                using var stream = File.OpenRead(audioPath);
                using var processor = factory!.CreateBuilder().WithLanguage(languageCode).Build();
                var transcript = new StringBuilder();
                string? detectedLanguage = null;
                await foreach (var segment in processor.ProcessAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    transcript.Append(segment.Text);
                    detectedLanguage ??= segment.Language;
                    progress?.Report(0.5);
                }
                progress?.Report(1);
                return new RecognitionResult(transcript.ToString(), detectedLanguage);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public void UnloadModel()
    {
        factory?.Dispose();
        factory = null;
        factoryModelPath = null;
    }

    public void Dispose()
    {
        UnloadModel();
        gate.Dispose();
    }
}
