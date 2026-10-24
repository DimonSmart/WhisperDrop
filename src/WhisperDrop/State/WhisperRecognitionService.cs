using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using WhisperDrop.Models;
using WhisperDrop.Settings;

namespace WhisperDrop.State;

internal sealed record WhisperProcessorPlan(
    bool DetectLanguage,
    string? LanguageCode,
    bool Translate,
    string? Prompt,
    int? Threads,
    int? MaxContextTokens,
    bool NoContext,
    TimeSpan? Offset,
    TimeSpan? Duration);

internal enum WhisperDecoderExperiment
{
    Original,
    StrictFallback,
    BeamSearch,
    NoPromptTemperature,
    SilenceFiltering
}

// Temporary, fixed diagnostic suite: no UI controls and no settings migration.
// Keep the original result intact so failures can be compared with the alternatives.
internal sealed record WhisperExperiment(
    int Number,
    string Name,
    WhisperDecoderExperiment Decoder,
    bool SkipSilence,
    bool UsePrompt = true,
    string? SkipReason = null);

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
        CancellationToken cancellationToken = default,
        IProgress<string>? recognizedSegments = null);

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
        CancellationToken cancellationToken = default,
        IProgress<string>? recognizedSegments = null)
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
                () => TranscribeExperimentsAsync(
                    modelPath,
                    factoryOptions,
                    effectiveDevice,
                    options,
                    audioPath,
                    reporter,
                    recognizedSegments,
                    cancellationToken),
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

    internal static IReadOnlyList<WhisperExperiment> CreateExperimentPlan(
        RecognitionOptions options,
        bool vadAvailable)
    {
        // These are complete independent decodes of the same prepared audio.
        // The installed whisper.cpp v1.8.5 has no_context=true by default;
        // changing only MaxLastTextTokens would not test actual context carryover.
        return
        [
            new(1, "Baseline (current recognition settings)",
                WhisperDecoderExperiment.Original, options.SkipSilence),
            new(2, "Stricter fallback (entropy=1.8, logprob=-0.8)",
                WhisperDecoderExperiment.StrictFallback, options.SkipSilence),
            new(3, "Beam search (beam size=5)",
                WhisperDecoderExperiment.BeamSearch, options.SkipSilence),
            new(4, "Temperature=0.2, no initial prompt",
                WhisperDecoderExperiment.NoPromptTemperature, options.SkipSilence, UsePrompt: false),
            new(5, "Stronger silence filtering (no-speech=0.4)",
                WhisperDecoderExperiment.SilenceFiltering, options.SkipSilence),
            new(6, options.SkipSilence ? "Without VAD (whole file)" : "With VAD (speech regions)",
                WhisperDecoderExperiment.Original, !options.SkipSilence,
                SkipReason: vadAvailable ? null : "VAD model is not installed")
        ];
    }

    internal static string FormatExperimentHeader(
        WhisperExperiment experiment,
        int count,
        RecognitionOptions options,
        string modelPath)
    {
        var promptPresent = experiment.UsePrompt && !string.IsNullOrWhiteSpace(options.Prompt);
        var mode = experiment.Decoder switch
        {
            WhisperDecoderExperiment.StrictFallback => "greedy; temperature=0.0; increment=0.2; entropy=1.8; logprob=-0.8",
            WhisperDecoderExperiment.BeamSearch => "beam search; beam size=5; temperature=0.0; increment=0.2",
            WhisperDecoderExperiment.NoPromptTemperature => "greedy; temperature=0.2; increment=0.2",
            WhisperDecoderExperiment.SilenceFiltering => "greedy; temperature=0.0; increment=0.2; no-speech=0.4",
            _ => "greedy; temperature=0.0; increment=0.2 (native defaults)"
        };

        return $"=== EXPERIMENT {experiment.Number}/{count}: {experiment.Name} ===\n" +
            $"Model: {Path.GetFileName(modelPath)}; language: {options.LanguageCode}; task: {options.Task}; " +
            $"VAD: {(experiment.SkipSilence ? "on" : "off")}; prompt: {(promptPresent ? "on" : "off")}; " +
            $"decoder: {mode}\n" +
            "Native previous-text context: off (Whisper.net 1.9.1 / whisper.cpp 1.8.5 default)\n" +
            "Transcript:\n";
    }

    private async Task<RecognitionResult> TranscribeExperimentsAsync(
        string modelPath,
        WhisperFactoryOptions factoryOptions,
        ProcessingDevice device,
        RecognitionOptions options,
        string audioPath,
        MonotonicProgressReporter progress,
        IProgress<string>? recognizedSegments,
        CancellationToken cancellationToken)
    {
        var experiments = CreateExperimentPlan(options, vadModelManager.IsAvailable);
        var output = new StringBuilder();
        string? detectedLanguage = null;

        void Append(string text)
        {
            output.Append(text);
            recognizedSegments?.Report(text);
        }

        Append($"WhisperDrop transcription diagnostics | Audio: {Path.GetFileName(audioPath)} | " +
            $"Model: {Path.GetFileName(modelPath)} | Device: {device} | " +
            $"Whisper.net: 1.9.1 | Experiments: {experiments.Count}\n\n");

        for (var index = 0; index < experiments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var experiment = experiments[index];
            Append(FormatExperimentHeader(experiment, experiments.Count, options, modelPath));

            if (experiment.SkipReason is { } reason)
            {
                Append($"[SKIPPED: {reason}]\n\n");
                progress.Report((index + 1d) / experiments.Count);
                continue;
            }

            var experimentIndex = index;
            var experimentProgress = new MonotonicProgressReporter(
                new Progress<double>(value =>
                    progress.Report((experimentIndex + Math.Clamp(value, 0, 1)) / experiments.Count)));
            var timer = Stopwatch.StartNew();

            try
            {
                var result = experiment.SkipSilence
                    ? await TranscribeWithVadAsync(
                        modelPath, factoryOptions, device, options, audioPath,
                        experimentProgress, recognizedSegments, cancellationToken,
                        experiment).ConfigureAwait(false)
                    : await TranscribeWholeFileAsync(
                        modelPath, factoryOptions, device, options, audioPath,
                        experimentProgress, recognizedSegments, cancellationToken,
                        experiment).ConfigureAwait(false);

                timer.Stop();
                // Native streaming already reported the body; append only to the final result.
                output.Append(result.Transcript);
                if (result.Transcript.Length == 0)
                {
                    Append("[EMPTY TRANSCRIPT]");
                }

                detectedLanguage ??= result.DetectedLanguage;
                Append($"\n\n{FormatExperimentDiagnostics(result, timer.Elapsed)}\n\n");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One failing decoder configuration must not hide later experiments.
                Append($"\n[ERROR: {exception.GetType().Name}: {exception.Message}]\n\n");
            }

            progress.Report((index + 1d) / experiments.Count);
        }

        progress.Report(1);
        // Segment timestamps from independent decodes cannot be merged into a single timeline.
        return new RecognitionResult(output.ToString(), detectedLanguage);
    }

    internal static string FormatExperimentDiagnostics(RecognitionResult result, TimeSpan elapsed)
    {
        double? maxCompression = null;
        var longestRun = 0;
        var currentRun = 0;
        string? previous = null;

        foreach (var segment in result.Segments)
        {
            var normalized = string.Join(' ', segment.Text.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .TrimEnd('.', ',', '!', '?', '。', '！', '？')
                .ToLowerInvariant();

            if (normalized.Length >= 25)
            {
                currentRun = string.Equals(previous, normalized, StringComparison.Ordinal)
                    ? currentRun + 1 : 1;
                longestRun = Math.Max(longestRun, currentRun);
                previous = normalized;
            }
            else
            {
                previous = null;
                currentRun = 0;
            }

            if (Encoding.UTF8.GetByteCount(segment.Text) >= 100)
            {
                var ratio = GetGzipCompressionRatio(segment.Text);
                maxCompression = Math.Max(maxCompression ?? 0, ratio);
            }
        }

        var gzip = maxCompression?.ToString("F2", CultureInfo.InvariantCulture) ?? "n/a";
        var seconds = elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
        return $"Diagnostics: segments={result.Segments.Count}; " +
            $"max segment gzip ratio={gzip}; " +
            $"longest identical adjacent segment run={longestRun}; " +
            $"elapsed={seconds}s; detected language={result.DetectedLanguage ?? "unknown"}";
    }

    internal static double GetGzipCompressionRatio(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length == 0)
        {
            return 0;
        }

        using var buffer = new MemoryStream();
        using (var compressor = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(bytes);
        }

        return bytes.Length / (double)Math.Max(1, buffer.Length);
    }

    private async Task<RecognitionResult> TranscribeWholeFileAsync(
        string modelPath,
        WhisperFactoryOptions factoryOptions,
        ProcessingDevice device,
        RecognitionOptions options,
        string audioPath,
        MonotonicProgressReporter progress,
        IProgress<string>? recognizedSegments,
        CancellationToken cancellationToken,
        WhisperExperiment experiment)
    {
        var recognitionFactory = EnsureRecognitionFactory(modelPath, factoryOptions, device);
        using var processor = BuildProcessor(
            recognitionFactory,
            options,
            nativeProgress => progress.Report(nativeProgress / 100d),
            experiment: experiment);

        var result = await ProcessAsync(processor, audioPath, cancellationToken, recognizedSegments: recognizedSegments).ConfigureAwait(false);
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
        IProgress<string>? recognizedSegments,
        CancellationToken cancellationToken,
        WhisperExperiment experiment)
    {
        var voiceFactory = EnsureVadFactory();
        // Silero VAD is small; keep its native tensors on CPU even when Whisper uses Metal.
        // On whisper.cpp 1.8.5 forcing VAD onto Metal can abort the entire process.
        using var vadProcessor = BuildCpuVadProcessor(voiceFactory);
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
        var segments = new List<RecognitionSegment>();
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
                region.Duration,
                experiment);

            var chunk = await ProcessAsync(processor, audioPath, cancellationToken, segments.Count, recognizedSegments).ConfigureAwait(false);
            transcript.Append(chunk.Transcript);
            segments.AddRange(chunk.Segments);
            detectedLanguage ??= chunk.DetectedLanguage;
            progress.Report(0.1 + (0.9 * (region.ProgressOffset + region.ProgressWeight)));
        }

        progress.Report(1);
        return new RecognitionResult(transcript.ToString(), detectedLanguage, segments);
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

    internal static WhisperVadProcessor BuildCpuVadProcessor(WhisperVadFactory voiceFactory) =>
        voiceFactory.CreateBuilder()
            .WithUseGpu(false)
            .Build();

    private WhisperVadFactory EnsureVadFactory()
    {
        if (vadFactory is not null &&
            string.Equals(vadFactoryModelPath, vadModelManager.ModelPath, StringComparison.Ordinal))
        {
            return vadFactory;
        }

        vadFactory?.Dispose();
        // VAD has its own model/context. Never inherit the main ASR model's GPU options.
        // In Whisper.net 1.9.1 the builder controls native VAD GPU selection;
        // set both factory and builder options explicitly for forward compatibility.
        var vadOptions = WhisperFactoryOptions.Default;
        vadOptions.UseGpu = false;
        vadFactory = WhisperVadFactory.FromPath(vadModelManager.ModelPath, vadOptions);
        vadFactoryModelPath = vadModelManager.ModelPath;
        runtimeSelector.MarkInitialized();
        return vadFactory;
    }

    private static WhisperProcessor BuildProcessor(
        WhisperFactory recognitionFactory,
        RecognitionOptions options,
        Action<int> progress,
        TimeSpan? offset = null,
        TimeSpan? duration = null,
        WhisperExperiment? experiment = null)
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

        if (plan.Prompt is not null && experiment?.UsePrompt != false)
        {
            builder.WithPrompt(plan.Prompt);
        }

        if (plan.Threads is int threads)
        {
            builder.WithThreads(threads);
        }

        if (plan.NoContext || experiment is { Decoder: not WhisperDecoderExperiment.Original })
        {
            builder.WithNoContext();
        }
        else if (plan.MaxContextTokens is int tokens)
        {
            builder.WithMaxLastTextTokens(tokens);
        }

        switch (experiment?.Decoder)
        {
            case WhisperDecoderExperiment.StrictFallback:
                builder.WithEntropyThreshold(1.8f);
                builder.WithLogProbThreshold(-0.8f);
                break;
            case WhisperDecoderExperiment.BeamSearch:
                builder.WithBeamSearchSamplingStrategy();
                break;
            case WhisperDecoderExperiment.NoPromptTemperature:
                builder.WithTemperature(0.2f);
                break;
            case WhisperDecoderExperiment.SilenceFiltering:
                builder.WithNoSpeechThreshold(0.4f);
                break;
        }

        if (plan.Offset is TimeSpan start)
        {
            builder.WithOffset(start);
        }

        if (plan.Duration is TimeSpan length)
        {
            builder.WithDuration(length);
        }

        builder.WithProbabilities();
        builder.WithProgressHandler(nativeProgress => progress(nativeProgress));
        return builder.Build();
    }

    private static async Task<RecognitionResult> ProcessAsync(
        WhisperProcessor processor,
        string audioPath,
        CancellationToken cancellationToken,
        int firstSegmentId = 0,
        IProgress<string>? recognizedSegments = null)
    {
        using var stream = File.OpenRead(audioPath);
        var transcript = new StringBuilder();
        var segments = new List<RecognitionSegment>();
        string? detectedLanguage = null;

        await foreach (var segment in processor.ProcessAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            transcript.Append(segment.Text);
            recognizedSegments?.Report(segment.Text);
            segments.Add(CreateRecognitionSegment(segment, firstSegmentId + segments.Count));
            detectedLanguage ??= segment.Language;
        }

        return new RecognitionResult(transcript.ToString(), detectedLanguage, segments);
    }

    internal static RecognitionSegment CreateRecognitionSegment(SegmentData segment, int id) =>
        new(
            id,
            segment.Start,
            segment.End,
            segment.Text,
            segment.Probability,
            segment.MinProbability,
            segment.MaxProbability,
            segment.NoSpeechProbability);

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
            options.WhisperNoContext
                ? null
                : Math.Clamp(options.WhisperMaxContextTokens, WhisperContextLimits.MinTokens, WhisperContextLimits.MaxTokens),
            options.WhisperNoContext,
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
