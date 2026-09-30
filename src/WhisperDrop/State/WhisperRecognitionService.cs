using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using WhisperDrop.Settings;

namespace WhisperDrop.State;

public sealed record RecognitionResult(string Transcript, string? DetectedLanguage);

public interface IRecognitionService : IDisposable
{
    Task<RecognitionResult> TranscribeAsync(
        string modelPath,
        RecognitionOptions options,
        string audioPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    void UnloadModel();
}

/// <summary>Owns one loaded recognition model and a separate reusable VAD model.</summary>
public sealed class WhisperRecognitionService : IRecognitionService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IWhisperRuntimeSelector runtimeSelector;
    private readonly IVadModelManager vadModelManager;
    private WhisperFactory? factory;
    private string? factoryModelPath;
    private ProcessingDevice? factoryDevice;
    private WhisperVadFactory? vadFactory;
    private string? vadFactoryModelPath;

    public WhisperRecognitionService()
        : this(
            new WhisperRuntimeSelector(),
            new VadModelManager(new ApplicationPaths(), new VadModelDownloader()))
    {
    }

    public WhisperRecognitionService(
        IWhisperRuntimeSelector runtimeSelector,
        IVadModelManager vadModelManager)
    {
        this.runtimeSelector = runtimeSelector;
        this.vadModelManager = vadModelManager;
    }

    public async Task<RecognitionResult> TranscribeAsync(
        string modelPath,
        RecognitionOptions options,
        string audioPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(modelPath))
            {
                throw new RecognitionConfigurationException("The selected Whisper model is not installed.");
            }

            if (options.SkipSilence && !vadModelManager.IsAvailable)
            {
                throw new RecognitionConfigurationException("Voice detection model is missing.");
            }

            var factoryOptions = runtimeSelector.ConfigureBeforeFirstUse(options.ProcessingDevice);
            var effectiveDevice = runtimeSelector.ActiveDevice;
            var reporter = new MonotonicProgress(progress);
            reporter.Report(0);

            return await Task.Run(
                async () => options.SkipSilence
                    ? await TranscribeWithVadAsync(
                        modelPath,
                        factoryOptions,
                        effectiveDevice,
                        options,
                        audioPath,
                        reporter,
                        cancellationToken).ConfigureAwait(false)
                    : await TranscribeWholeFileAsync(
                        modelPath,
                        factoryOptions,
                        effectiveDevice,
                        options,
                        audioPath,
                        reporter,
                        cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (RecognitionConfigurationException)
        {
            throw;
        }
        catch (DllNotFoundException exception)
        {
            throw new RecognitionConfigurationException(
                "The bundled Whisper runtime could not be loaded.",
                exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new RecognitionConfigurationException(
                "The bundled Whisper runtime is incompatible with this application build.",
                exception);
        }
        catch (WhisperModelLoadException exception)
        {
            throw new RecognitionConfigurationException(
                "The selected Whisper model could not be loaded.",
                exception);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RecognitionResult> TranscribeWholeFileAsync(
        string modelPath,
        WhisperFactoryOptions factoryOptions,
        ProcessingDevice device,
        RecognitionOptions options,
        string audioPath,
        MonotonicProgress progress,
        CancellationToken cancellationToken)
    {
        var recognitionFactory = EnsureRecognitionFactory(modelPath, factoryOptions, device);
        using var processor = BuildProcessor(
            recognitionFactory,
            options,
            nativeProgress => progress.Report(nativeProgress / 100d));

        var result = await ProcessAsync(processor, audioPath, cancellationToken).ConfigureAwait(false);
        progress.Report(1);
        return result;
    }

    private async Task<RecognitionResult> TranscribeWithVadAsync(
        string modelPath,
        WhisperFactoryOptions factoryOptions,
        ProcessingDevice device,
        RecognitionOptions options,
        string audioPath,
        MonotonicProgress progress,
        CancellationToken cancellationToken)
    {
        var voiceFactory = EnsureVadFactory(factoryOptions, device);
        var vadBuilder = voiceFactory.CreateBuilder()
            .WithUseGpu(device != ProcessingDevice.Cpu);
        using var vadProcessor = vadBuilder.Build();
        using var vadStream = File.OpenRead(audioPath);
        var speechRegions = await vadProcessor.DetectSpeechAsync(vadStream, cancellationToken).ConfigureAwait(false);
        progress.Report(0.1);

        if (speechRegions.Count == 0)
        {
            progress.Report(1);
            return new RecognitionResult(string.Empty, null);
        }

        var validRegions = new System.Collections.Generic.List<VadSegmentData>(speechRegions.Count);
        double totalSpeechSeconds = 0;
        foreach (var region in speechRegions)
        {
            var seconds = (region.End - region.Start).TotalSeconds;
            if (seconds <= 0)
            {
                continue;
            }

            validRegions.Add(region);
            totalSpeechSeconds += seconds;
        }

        if (validRegions.Count == 0 || totalSpeechSeconds <= 0)
        {
            progress.Report(1);
            return new RecognitionResult(string.Empty, null);
        }

        var recognitionFactory = EnsureRecognitionFactory(modelPath, factoryOptions, device);
        var transcript = new StringBuilder();
        string? detectedLanguage = null;
        double completedSeconds = 0;

        foreach (var region in validRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = region.End - region.Start;
            var regionSeconds = duration.TotalSeconds;
            var regionBase = completedSeconds / totalSpeechSeconds;
            var regionWeight = regionSeconds / totalSpeechSeconds;

            using var processor = BuildProcessor(
                recognitionFactory,
                options,
                nativeProgress =>
                {
                    var chunkProgress = Math.Clamp(nativeProgress / 100d, 0, 1);
                    progress.Report(0.1 + (0.9 * (regionBase + (regionWeight * chunkProgress))));
                },
                region.Start,
                duration);

            var chunk = await ProcessAsync(processor, audioPath, cancellationToken).ConfigureAwait(false);
            transcript.Append(chunk.Transcript);
            detectedLanguage ??= chunk.DetectedLanguage;
            completedSeconds += regionSeconds;
            progress.Report(0.1 + (0.9 * (completedSeconds / totalSpeechSeconds)));
        }

        progress.Report(1);
        return new RecognitionResult(transcript.ToString(), detectedLanguage);
    }

    private WhisperFactory EnsureRecognitionFactory(
        string modelPath,
        WhisperFactoryOptions factoryOptions,
        ProcessingDevice device)
    {
        if (factory is not null &&
            string.Equals(factoryModelPath, modelPath, StringComparison.Ordinal) &&
            factoryDevice == device)
        {
            return factory;
        }

        UnloadModel();
        factory = WhisperFactory.FromPath(modelPath, factoryOptions);
        factoryModelPath = modelPath;
        factoryDevice = device;
        runtimeSelector.MarkInitialized();
        return factory;
    }

    private WhisperVadFactory EnsureVadFactory(
        WhisperFactoryOptions factoryOptions,
        ProcessingDevice device)
    {
        if (vadFactory is not null &&
            string.Equals(vadFactoryModelPath, vadModelManager.ModelPath, StringComparison.Ordinal))
        {
            return vadFactory;
        }

        vadFactory?.Dispose();
        vadFactory = WhisperVadFactory.FromPath(vadModelManager.ModelPath, factoryOptions);
        vadFactoryModelPath = vadModelManager.ModelPath;
        runtimeSelector.MarkInitialized();
        return vadFactory;
    }

    private static WhisperProcessor BuildProcessor(
        WhisperFactory recognitionFactory,
        RecognitionOptions options,
        Action<int> progress,
        TimeSpan? offset = null,
        TimeSpan? duration = null)
    {
        var builder = recognitionFactory.CreateBuilder();

        if (string.Equals(options.LanguageCode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            builder.WithLanguageDetection();
        }
        else
        {
            builder.WithLanguage(options.LanguageCode);
        }

        if (options.Task == TranscriptionTask.TranslateToEnglish)
        {
            builder.WithTranslate();
        }

        if (!string.IsNullOrWhiteSpace(options.Prompt))
        {
            builder.WithPrompt(options.Prompt);
        }

        if (options.CpuThreads is int threads)
        {
            builder.WithThreads(threads);
        }

        if (offset is TimeSpan start)
        {
            builder.WithOffset(start);
        }

        if (duration is TimeSpan length)
        {
            builder.WithDuration(length);
        }

        builder.WithProgressHandler(nativeProgress => progress(nativeProgress));
        return builder.Build();
    }

    private static async Task<RecognitionResult> ProcessAsync(
        WhisperProcessor processor,
        string audioPath,
        CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(audioPath);
        var transcript = new StringBuilder();
        string? detectedLanguage = null;

        await foreach (var segment in processor.ProcessAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            transcript.Append(segment.Text);
            detectedLanguage ??= segment.Language;
        }

        return new RecognitionResult(transcript.ToString(), detectedLanguage);
    }

    public void UnloadModel()
    {
        factory?.Dispose();
        factory = null;
        factoryModelPath = null;
        factoryDevice = null;
    }

    public void Dispose()
    {
        UnloadModel();
        vadFactory?.Dispose();
        vadFactory = null;
        vadFactoryModelPath = null;
        gate.Dispose();
    }

    private sealed class MonotonicProgress
    {
        private readonly IProgress<double>? target;
        private readonly object sync = new();
        private double lastValue;

        public MonotonicProgress(IProgress<double>? target)
        {
            this.target = target;
        }

        public void Report(double value)
        {
            value = Math.Clamp(value, 0, 1);
            lock (sync)
            {
                if (value < lastValue)
                {
                    return;
                }

                lastValue = value;
                target?.Report(value);
            }
        }
    }
}
