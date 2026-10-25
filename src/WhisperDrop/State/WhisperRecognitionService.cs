using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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


// Small quality guard, not an attempt to correct recognition errors. We look only for
// obviously repeated contiguous word groups, including short markers such as
// "[HABLANDO EN INGLÉS]" which the old per-segment detector missed.
internal static class WhisperRepetitionGuard
{
    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    internal static int CountWords(string text) => WordPattern.Matches(text).Count;

    internal static int CountRepeatedTokens(string text)
    {
        var matches = WordPattern.Matches(text);
        var words = new string[matches.Count];
        for (var index = 0; index < matches.Count; index++)
        {
            words[index] = matches[index].Value.ToLowerInvariant();
        }

        var repeated = 0;
        for (var i = 0; i < words.Length;)
        {
            var blockSize = 0;
            // Require at least three successive occurrences of 3–10 words.
            for (var size = 3; size <= 10 && i + 3 * size <= words.Length; size++)
            {
                var identical = true;
                for (var j = 0; j < size && identical; j++)
                {
                    identical = words[i + j] == words[i + size + j] &&
                        words[i + j] == words[i + 2 * size + j];
                }

                if (identical)
                {
                    blockSize = size;
                    break;
                }
            }

            if (blockSize == 0)
            {
                i++;
                continue;
            }

            var copies = 3;
            while (i + (copies + 1) * blockSize <= words.Length)
            {
                var identical = true;
                for (var j = 0; j < blockSize; j++)
                {
                    if (words[i + j] != words[i + copies * blockSize + j])
                    {
                        identical = false;
                        break;
                    }
                }

                if (!identical)
                {
                    break;
                }

                copies++;
            }

            repeated += (copies - 1) * blockSize;
            i += copies * blockSize;
        }

        return repeated;
    }

    internal static bool PreferRetry(string original, string retry)
    {
        var originalRepeated = CountRepeatedTokens(original);
        var retryRepeated = CountRepeatedTokens(retry);
        if (originalRepeated == 0 || retryRepeated >= originalRepeated)
        {
            return false;
        }

        // Never choose a short/empty retry simply because it contains no repetition.
        var originalUsefulWords = CountWords(original) - originalRepeated;
        var retryUsefulWords = CountWords(retry) - retryRepeated;
        return retryUsefulWords >= Math.Max(1, originalUsefulWords * 0.7);
    }
}

/// <summary>Owns one loaded Whisper model; transcribes once, retrying only obvious loops.</summary>
public sealed class WhisperRecognitionService : IRecognitionService
{
    private const float EntropyThreshold = 1.8f;
    private const float LogProbabilityThreshold = -0.8f;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IWhisperRuntimeSelector runtimeSelector;
    private WhisperFactory? factory;
    private string? factoryModelPath;
    private ProcessingDevice? factoryDevice;

    public WhisperRecognitionService()
        : this(new WhisperRuntimeSelector())
    {
    }

    public WhisperRecognitionService(IWhisperRuntimeSelector runtimeSelector)
    {
        this.runtimeSelector = runtimeSelector;
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

            var factoryOptions = runtimeSelector.ConfigureBeforeFirstUse(options.ProcessingDevice);
            var device = runtimeSelector.ActiveDevice;
            var reporter = new MonotonicProgressReporter(progress);
            reporter.Report(0);

            return await Task.Run(
                () => TranscribeWithFallbackAsync(
                    modelPath, factoryOptions, device, options, audioPath,
                    reporter, recognizedSegments, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (RecognitionConfigurationException)
        {
            throw;
        }
        catch (DllNotFoundException exception)
        {
            throw new RecognitionConfigurationException(
                "The bundled Whisper runtime could not be loaded.", exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new RecognitionConfigurationException(
                "The bundled Whisper runtime is incompatible with this application build.", exception);
        }
        catch (WhisperModelLoadException exception)
        {
            throw new RecognitionConfigurationException(
                "The selected Whisper model could not be loaded.", exception);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RecognitionResult> TranscribeWithFallbackAsync(
        string modelPath,
        WhisperFactoryOptions factoryOptions,
        ProcessingDevice device,
        RecognitionOptions options,
        string audioPath,
        MonotonicProgressReporter progress,
        IProgress<string>? recognizedSegments,
        CancellationToken cancellationToken)
    {
        var recognitionFactory = EnsureRecognitionFactory(modelPath, factoryOptions, device);
        using var primary = BuildProcessor(recognitionFactory, options,
            value => progress.Report(0.9 * value / 100d), beamSearch: false);
        var original = await ProcessAsync(primary, audioPath, cancellationToken, recognizedSegments).ConfigureAwait(false);
        var originalRepeated = WhisperRepetitionGuard.CountRepeatedTokens(original.Transcript);
        if (originalRepeated == 0)
        {
            progress.Report(1);
            return original;
        }

        // Retry at most once. If native Beam Search fails, preserve the primary result.
        // Do not stream the second attempt into the preview until it is selected:
        // otherwise the live preview would contain both unrelated transcripts.
        RecognitionResult? retry = null;
        try
        {
            using var alternate = BuildProcessor(recognitionFactory, options,
                value => progress.Report(0.9 + 0.1 * value / 100d), beamSearch: true);
            retry = await ProcessAsync(alternate, audioPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Decoding failure should not discard an otherwise usable first transcript.
            System.Diagnostics.Debug.WriteLine($"Whisper Beam Search retry failed: {exception}");
        }

        var selected = retry is not null &&
            WhisperRepetitionGuard.PreferRetry(original.Transcript, retry.Transcript)
                ? retry : original;
        var remaining = WhisperRepetitionGuard.CountRepeatedTokens(selected.Transcript);
        if (remaining > 0)
        {
            selected = selected with
            {
                QualityWarning = "Whisper detected repeated phrases that could not be resolved automatically. Please check the transcript."
            };
        }

        progress.Report(1);
        return selected;
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

    private static WhisperProcessor BuildProcessor(
        WhisperFactory recognitionFactory,
        RecognitionOptions options,
        Action<int> progress,
        bool beamSearch)
    {
        var plan = CreateProcessorPlan(options);
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

        if (plan.NoContext)
        {
            builder.WithNoContext();
        }
        else if (plan.MaxContextTokens is int tokens)
        {
            builder.WithMaxLastTextTokens(tokens);
        }

        builder.WithEntropyThreshold(EntropyThreshold);
        builder.WithLogProbThreshold(LogProbabilityThreshold);
        if (beamSearch)
        {
            builder.WithBeamSearchSamplingStrategy();
        }

        builder.WithProbabilities();
        builder.WithProgressHandler(value => progress(value));
        return builder.Build();
    }

    private static async Task<RecognitionResult> ProcessAsync(
        WhisperProcessor processor,
        string audioPath,
        CancellationToken cancellationToken,
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
            segments.Add(CreateRecognitionSegment(segment, segments.Count));
            detectedLanguage ??= segment.Language;
        }

        return new RecognitionResult(transcript.ToString(), detectedLanguage, segments);
    }

    internal static RecognitionSegment CreateRecognitionSegment(SegmentData segment, int id) =>
        new(id, segment.Start, segment.End, segment.Text, segment.Probability,
            segment.MinProbability, segment.MaxProbability, segment.NoSpeechProbability);

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

    internal static bool RequiresRecognitionFactoryReload(
        string? loadedModelPath,
        ProcessingDevice? loadedDevice,
        string requestedModelPath,
        ProcessingDevice requestedDevice) =>
        !string.Equals(loadedModelPath, requestedModelPath, StringComparison.Ordinal) ||
        loadedDevice != requestedDevice;
}
