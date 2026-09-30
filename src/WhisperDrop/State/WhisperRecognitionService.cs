using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using WhisperDrop.Settings;

namespace WhisperDrop.State;

public sealed record RecognitionResult(string Transcript, string? DetectedLanguage);

internal sealed record WhisperProcessorPlan(
    bool DetectLanguage,
    string? LanguageCode,
    bool Translate,
    string? Prompt,
    int? Threads,
    TimeSpan? Offset,
    TimeSpan? Duration);

internal sealed record VadRegionPlan(
    TimeSpan Start,
    TimeSpan Duration,
    double ProgressOffset,
    double ProgressWeight);

internal sealed class MonotonicProgressReporter
{
    private readonly IProgress<double>? target;
    private readonly object sync = new();
    private double lastValue;

    public MonotonicProgressReporter(IProgress<double>? target)
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
            var reporter = new MonotonicProgressReporter(progress);
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
        MonotonicProgressReporter progress,
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
        MonotonicProgressReporter progress,
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

        var regionPlans = CreateVadRegionPlan(speechRegions);
        if (regionPlans.Count == 0)
        {
            progress.Report(1);
            return new RecognitionResult(string.Empty, null);
        }

        var recognitionFactory = EnsureRecognitionFactory(modelPath, factoryOptions, device);
        var transcript = new StringBuilder();
        string? detectedLanguage = null;

        foreach (var region in regionPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var processor = BuildProcessor(
                recognitionFactory,
                options,
                nativeProgress =>
                {
                    var chunkProgress = Math.Clamp(nativeProgress / 100d, 0, 1);
                    progress.Report(0.1 + (0.9 * (region.ProgressOffset + (region.ProgressWeight * chunkProgress))));
                },
                region.Start,
                region.Duration);

            var chunk = await ProcessAsync(processor, audioPath, cancellationToken).ConfigureAwait(false);
            transcript.Append(chunk.Transcript);
            detectedLanguage ??= chunk.DetectedLanguage;
            progress.Report(0.1 + (0.9 * (region.ProgressOffset + region.ProgressWeight)));
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
            !RequiresRecognitionFactoryReload(factoryModelPath, factoryDevice, modelPath, device))
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
        var plan = CreateProcessorPlan(options, offset, duration);
        var builder = recognitionFactory.CreateBuilder();

        if (plan.DetectLanguage)
        {
            builder.WithLanguageDetection();
        }
        else
        {
            builder.WithLanguage(plan.LanguageCode!);
        }

        if (plan.Translate)
        {
            builder.WithTranslate();
        }

        if (plan.Prompt is not null)
        {
            builder.WithPrompt(plan.Prompt);
        }

        if (plan.Threads is int threads)
        {
            builder.WithThreads(threads);
        }

        if (plan.Offset is TimeSpan start)
        {
            builder.WithOffset(start);
        }

        if (plan.Duration is TimeSpan length)
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

    internal static WhisperProcessorPlan CreateProcessorPlan(
        RecognitionOptions options,
        TimeSpan? offset = null,
        TimeSpan? duration = null)
    {
        var detectLanguage = string.Equals(options.LanguageCode, "auto", StringComparison.OrdinalIgnoreCase);
        return new WhisperProcessorPlan(
            detectLanguage,
            detectLanguage ? null : options.LanguageCode,
            options.Task == TranscriptionTask.TranslateToEnglish,
            string.IsNullOrWhiteSpace(options.Prompt) ? null : options.Prompt,
            options.CpuThreads,
            offset,
            duration);
    }

    internal static IReadOnlyList<VadRegionPlan> CreateVadRegionPlan(IReadOnlyList<VadSegmentData> regions)
    {
        var valid = new System.Collections.Generic.List<(TimeSpan Start, TimeSpan Duration)>();
        double totalSeconds = 0;
        foreach (var region in regions)
        {
            var duration = region.End - region.Start;
            if (duration <= TimeSpan.Zero)
            {
                continue;
            }

            valid.Add((region.Start, duration));
            totalSeconds += duration.TotalSeconds;
        }

        if (totalSeconds <= 0)
        {
            return [];
        }

        var plans = new VadRegionPlan[valid.Count];
        double completedSeconds = 0;
        for (var i = 0; i < valid.Count; i++)
        {
            var region = valid[i];
            plans[i] = new VadRegionPlan(
                region.Start,
                region.Duration,
                completedSeconds / totalSeconds,
                region.Duration.TotalSeconds / totalSeconds);
            completedSeconds += region.Duration.TotalSeconds;
        }

        return plans;
    }

    internal static bool RequiresRecognitionFactoryReload(
        string? loadedModelPath,
        ProcessingDevice? loadedDevice,
        string requestedModelPath,
        ProcessingDevice requestedDevice) =>
        !string.Equals(loadedModelPath, requestedModelPath, StringComparison.Ordinal) ||
        loadedDevice != requestedDevice;
}
