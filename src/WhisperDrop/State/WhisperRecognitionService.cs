using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Wave;
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
    bool NoContext);

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
            // Require at least three successive occurrences of 3–32 words.
            for (var size = 3; size <= 32 && i + 3 * size <= words.Length; size++)
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


    // Limit native-callback work to recent text; most suspicious loops are local.
    internal static string Tail(string text, int maxCharacters = 4096) =>
        text.Length <= maxCharacters ? text : text[^maxCharacters..];

}

/// <summary>Owns one Whisper model; stops active decoding at obvious loops and repairs only local audio.</summary>
public sealed class WhisperRecognitionService : IRecognitionService
{
    private const float EntropyThreshold = 1.8f;
    private const float LogProbabilityThreshold = -0.8f;
    private const int SamplesPerSecond = 16000; // The media preparation contract is 16 kHz mono WAV.
    private static readonly TimeSpan RepairWindow = TimeSpan.FromSeconds(45);
    private const int PendingSegmentLookbehind = 3;
    private const int MaxLocalRepairs = 5;

    private sealed record RecognizedPart(RecognitionSegment Segment, string? Language);

    private sealed record RecognitionPass(IReadOnlyList<RecognizedPart> Confirmed, bool LoopDetected);


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
                () => TranscribeWithLocalRepairAsync(
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

    private async Task<RecognitionResult> TranscribeWithLocalRepairAsync(
        string modelPath,
        WhisperFactoryOptions factoryOptions,
        ProcessingDevice device,
        RecognitionOptions options,
        string audioPath,
        MonotonicProgressReporter progress,
        IProgress<string>? recognizedSegments,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var recognitionFactory = EnsureRecognitionFactory(modelPath, factoryOptions, device);
        using var stream = File.OpenRead(audioPath);
        // Decode once. Never repeatedly read the whole WAV when recovering a local region.
        var samples = await new WaveParser(stream).GetAvgSamplesAsync(cancellationToken).ConfigureAwait(false);
        var total = TimeSpan.FromSeconds((double)samples.Length / SamplesPerSecond);
        var transcript = new StringBuilder();
        var segments = new List<RecognitionSegment>();
        string? detectedLanguage = null;
        var repairCount = 0;
        var warning = false;
        var cursorSample = 0;

        void Log(string message) => Console.Error.WriteLine($"[WhisperDrop] {message}");
        void Append(RecognizedPart part, int baseSample)
        {
            var shift = TimeSpan.FromSeconds((double)baseSample / SamplesPerSecond);
            var seg = part.Segment with
            {
                Id = segments.Count,
                Start = part.Segment.Start + shift,
                End = part.Segment.End + shift
            };
            segments.Add(seg);
            transcript.Append(seg.Text);
            recognizedSegments?.Report(seg.Text);
            detectedLanguage ??= part.Language;
        }

        void ReportPassProgress(int startSample, int passSamples, int percentage)
        {
            if (samples.Length == 0)
            {
                return;
            }

            var position = (startSample + passSamples * Math.Clamp(percentage, 0, 100) / 100.0) / samples.Length;
            progress.Report(position * 0.95); // Reserve 5% for any recovery/finalization.
        }

        Log($"start duration={total:c} samples={samples.Length} model={Path.GetFileName(modelPath)} " +
            $"device={device} entropy={EntropyThreshold} logprob={LogProbabilityThreshold} " +
            $"repairWindow={RepairWindow.TotalSeconds:0}s");

        while (cursorSample < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var passStart = cursorSample;
            var passSamples = samples.Length - passStart;

            var greedy = await RunPassAsync(recognitionFactory, options,
                samples.AsMemory(passStart), beamSearch: false, detectLoop: true,
                value => ReportPassProgress(passStart, passSamples, value),
                part => Append(part, passStart), cancellationToken).ConfigureAwait(false);

            if (!greedy.LoopDetected)
            {
                Log($"completed after {repairCount} local repair(s), segments={segments.Count} " +
                    $"characters={transcript.Length} elapsed={clock.Elapsed:c}");
                break;
            }

            // These confirmed segments precede the rolling lookbehind; discard the
            // suspicious pending segments and restart exactly at the last safe end.
            var safeRelativeEnd = greedy.Confirmed.Count == 0
                ? TimeSpan.Zero
                : greedy.Confirmed[^1].Segment.End;
            var safeSample = Math.Clamp(passStart + ToSample(safeRelativeEnd),
                passStart, samples.Length);
            if (safeSample >= samples.Length)
            {
                // A pathological timestamp must not hide unfinished speech.
                safeSample = passStart;
                warning = true;
            }

            repairCount++;
            var repairLength = Math.Min(samples.Length - safeSample, ToSample(RepairWindow));
            if (repairLength <= 0)
            {
                warning = true;
                break;
            }

            Log($"loop detected #{repairCount} at ~{(double)safeSample / SamplesPerSecond:0.00}s; " +
                $"kept={greedy.Confirmed.Count} segments; switching to Beam Search for " +
                $"{(double)repairLength / SamplesPerSecond:0.0}s");

            RecognitionPass repaired;
            try
            {
                repaired = await RunPassAsync(recognitionFactory, options,
                    samples.AsMemory(safeSample, repairLength), beamSearch: true, detectLoop: false,
                    value => ReportPassProgress(safeSample, repairLength, value),
                    part => Append(part, safeSample), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log($"Beam Search error near {(double)safeSample / SamplesPerSecond:0.0}s: {exception}");
                warning = true;
                // Still try to preserve this interval using a bounded greedy pass.
                repaired = await RunPassAsync(recognitionFactory, options,
                    samples.AsMemory(safeSample, repairLength), beamSearch: false, detectLoop: false,
                    value => ReportPassProgress(safeSample, repairLength, value),
                    part => Append(part, safeSample), cancellationToken).ConfigureAwait(false);
            }

            var repairText = string.Concat(System.Linq.Enumerable.Select(repaired.Confirmed, part => part.Segment.Text));
            var remainingLoops = WhisperRepetitionGuard.CountRepeatedTokens(repairText);
            if (remainingLoops != 0)
            {
                warning = true;
                Log($"Beam Search returned {remainingLoops} repeated words; retaining its bounded output with warning");
            }

            var advancedBy = ChooseAdvance(repairLength, repaired.Confirmed);
            cursorSample = Math.Min(samples.Length, safeSample + advancedBy);
            Log($"repair #{repairCount}: beamSegments={repaired.Confirmed.Count} " +
                $"resume={(double)cursorSample / SamplesPerSecond:0.00}s elapsed={clock.Elapsed:c}");

            if (repairCount >= MaxLocalRepairs && cursorSample < samples.Length)
            {
                // Avoid infinite repair loops on pathological recordings: a single
                // Beam pass for the remaining suffix, never restarting from file start.
                Log($"repair limit {MaxLocalRepairs} reached; decoding remaining suffix with Beam Search");
                warning = true;
                var tailStart = cursorSample;
                var tail = await RunPassAsync(recognitionFactory, options,
                    samples.AsMemory(tailStart), beamSearch: true, detectLoop: false,
                    value => ReportPassProgress(tailStart, samples.Length - tailStart, value),
                    part => Append(part, tailStart), cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        progress.Report(1);
        Log($"done segments={segments.Count} characters={transcript.Length} " +
            $"repairs={repairCount} warning={warning} elapsed={clock.Elapsed:c}");
        return new RecognitionResult(transcript.ToString(), detectedLanguage, segments)
        {
            QualityWarning = warning
                ? "Whisper recovered from repeated speech, but parts of the transcript may need review."
                : null
        };
    }

    // A native segment callback runs on Whisper's decode thread. We cancel the *internal*
    // token as soon as three repeated word groups appear; public user cancellation is separate.
    // DisposeAsync waits for the native worker before a new processor is created.
    private static async Task<RecognitionPass> RunPassAsync(
        WhisperFactory recognitionFactory,
        RecognitionOptions options,
        ReadOnlyMemory<float> audio,
        bool beamSearch,
        bool detectLoop,
        Action<int> progress,
        Action<RecognizedPart> onConfirmed,
        CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var confirmed = new List<RecognizedPart>();
        var pending = new Queue<RecognizedPart>();
        var recent = new StringBuilder();
        var loopDetected = false;

        void OnSegment(SegmentData segment)
        {
            var part = new RecognizedPart(CreateRecognitionSegment(segment, 0), segment.Language);
            pending.Enqueue(part);
            if (detectLoop)
            {
                recent.Append(segment.Text);
                if (recent.Length > 4096)
                {
                    recent.Remove(0, recent.Length - 4096);
                }
                if (WhisperRepetitionGuard.CountRepeatedTokens(recent.ToString()) > 0)
                {
                    loopDetected = true;
                    stop.Cancel();
                    return;
                }
            }

            if (pending.Count > (detectLoop ? PendingSegmentLookbehind : 0))
            {
                var safe = pending.Dequeue();
                confirmed.Add(safe);
                onConfirmed(safe);
            }
        }

        await using var processor = BuildProcessor(recognitionFactory, options,
            progress, beamSearch, OnSegment);
        try
        {
            // The enumerator is drained for native completion; the callback captures
            // segments immediately (without async buffering latency).
            await foreach (var _ in processor.ProcessAsync(audio, stop.Token).ConfigureAwait(false))
            {
            }
        }
        catch (OperationCanceledException) when (loopDetected && !cancellationToken.IsCancellationRequested)
        {
            // Expected early native abort after a detected repeated sequence.
        }
        catch (WhisperProcessingException) when (loopDetected && !cancellationToken.IsCancellationRequested)
        {
            // Some native backends report an aborted pass as a processing error.
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!loopDetected)
        {
            while (pending.Count > 0)
            {
                var safe = pending.Dequeue();
                confirmed.Add(safe);
                onConfirmed(safe);
            }
        }

        return new RecognitionPass(confirmed, loopDetected);
    }

    internal static int ToSample(TimeSpan offset) =>
        (int)Math.Max(0, Math.Round(offset.TotalSeconds * SamplesPerSecond));

    private static int ChooseAdvance(int windowSamples, IReadOnlyList<RecognizedPart> segments)
    {
        if (segments.Count == 0)
        {
            return windowSamples;
        }

        var lastEnd = ToSample(segments[^1].Segment.End);
        // Use the last recognized segment boundary rather than blindly skipping
        // silent or unfinished speech at the end of the repair window.
        return lastEnd > SamplesPerSecond
            ? Math.Clamp(lastEnd, 1, windowSamples)
            : windowSamples;
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
        bool beamSearch,
        OnSegmentEventHandler onSegment)
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
        builder.WithSegmentEventHandler(onSegment);
        builder.WithProgressHandler(value => progress(value));
        return builder.Build();
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

    internal static WhisperProcessorPlan CreateProcessorPlan(RecognitionOptions options)
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
            options.WhisperNoContext);
    }

    internal static bool RequiresRecognitionFactoryReload(
        string? loadedModelPath,
        ProcessingDevice? loadedDevice,
        string requestedModelPath,
        ProcessingDevice requestedDevice) =>
        !string.Equals(loadedModelPath, requestedModelPath, StringComparison.Ordinal) ||
        loadedDevice != requestedDevice;
}
