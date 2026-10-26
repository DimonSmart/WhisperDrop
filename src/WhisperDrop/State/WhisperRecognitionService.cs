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
// One diagnostic log per transcription. Recording errors must never break recognition.
internal sealed class RecognitionDiagnostics : IDisposable
{
    private readonly StreamWriter? writer;
    internal string? FilePath { get; }

    internal RecognitionDiagnostics()
    {
        try
        {
            var folder = Path.GetDirectoryName(new ApplicationPaths().SettingsFilePath)!;
            Directory.CreateDirectory(folder);
            FilePath = Path.Combine(folder, "recognition-debug.log");
            writer = new StreamWriter(FilePath, append: false) { AutoFlush = true };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[WhisperDrop] Could not create diagnostic file: {exception.Message}");
        }
    }

    internal void Write(string message)
    {
        var line = $"[WhisperDrop] {DateTimeOffset.Now:O} {message}";
        Console.Error.WriteLine(line);
        try
        {
            writer?.WriteLine(line);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"[WhisperDrop] Could not write diagnostic file: {exception.Message}");
        }
    }

    public void Dispose() => writer?.Dispose();
}

internal static class WhisperRepetitionGuard
{
    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex NonSpeechMarker = new(
        @"\[(?:m[uú]sica|music|speaking\s+(?:spanish|english)|hablando\s+en\s+(?:español|ingl[eé]s)|applause|aplausos)\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static int MarkerCount(string text) => NonSpeechMarker.Matches(text).Count;

    internal static int MeaningfulWords(string text) =>
        CountWords(NonSpeechMarker.Replace(text, " "));

    internal static bool IsSuspiciousRepair(string text, double seconds)
    {
        var words = MeaningfulWords(text);
        var markers = MarkerCount(text);
        // Real silence is possible; this is a reason to try a shorter decode,
        // not evidence that actual speech exists.
        return CountRepeatedTokens(text) > 0 ||
            markers >= 2 && words < 8 ||
            seconds >= 10 && words < 6;
    }

    internal static bool HasMarkerLoop(string text) =>
        MarkerCount(text) >= 3 && MeaningfulWords(text) <= 12;

    internal static int RepairScore(string text) =>
        MeaningfulWords(text) - CountRepeatedTokens(text) * 2 - MarkerCount(text) * 3;

    internal static bool PreferShortRecovery(string beam, string shortRecovery, double seconds)
    {
        var beamSuspicious = IsSuspiciousRepair(beam, seconds);
        var shortSuspicious = IsSuspiciousRepair(shortRecovery, seconds);
        if (beamSuspicious != shortSuspicious)
        {
            return !shortSuspicious;
        }

        // Keep Beam Search unless the shorter pass is clearly more useful;
        // do not prefer a shorter result merely because it contains fewer markers.
        return RepairScore(shortRecovery) > RepairScore(beam) + 4 &&
            MeaningfulWords(shortRecovery) >= MeaningfulWords(beam) * 0.85;
    }


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
    private static readonly TimeSpan RepairWindow = TimeSpan.FromSeconds(25);
    private const int PendingSegmentLookbehind = 3;
    private const int MaxLocalRepairs = 5;


    private sealed record RecognizedPart(RecognitionSegment Segment, string? Language);

    private sealed record RecognitionPass(
        IReadOnlyList<RecognizedPart> Confirmed,
        bool LoopDetected,
        TimeSpan? DetectedAt = null,
        string? LoopExcerpt = null);


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

        using var diagnostics = new RecognitionDiagnostics();
        void Log(string message) => diagnostics.Write(message);
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

        Log($"start duration={total:c} samples={samples.Length} logFile={diagnostics.FilePath ?? "unavailable"} model={Path.GetFileName(modelPath)} " +
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

            Log($"loop detected #{repairCount} at native audio " +
                $"{(passStart / (double)SamplesPerSecond + (greedy.DetectedAt?.TotalSeconds ?? 0)):0.00}s; " +
                $"rewind to safe {(double)safeSample / SamplesPerSecond:0.00}s; " +
                $"kept={greedy.Confirmed.Count} segments; Beam Search window " +
                $"{(double)repairLength / SamplesPerSecond:0.0}s; " +
                $"excerpt=\"{greedy.LoopExcerpt}\"");

            var audioWindow = samples.AsMemory(safeSample, repairLength);
            var windowSeconds = (double)repairLength / SamplesPerSecond;
            var audioDb = AudioRmsDb(audioWindow.Span);

            void LogCandidate(string decoder, RecognitionPass candidate)
            {
                var text = ExtractText(candidate);
                var firstStart = candidate.Confirmed.Count > 0
                    ? candidate.Confirmed[0].Segment.Start.TotalSeconds.ToString("0.00") : "none";
                var lastEnd = candidate.Confirmed.Count > 0
                    ? candidate.Confirmed[^1].Segment.End.TotalSeconds.ToString("0.00") : "none";
                Log($"repair #{repairCount} candidate={decoder} start={safeSample / (double)SamplesPerSecond:0.00}s " +
                    $"window={windowSeconds:0.00}s audioRms={audioDb:0.0}dB " +
                    $"segments={candidate.Confirmed.Count} first={firstStart}s last={lastEnd}s " +
                    $"chars={text.Length} words={WhisperRepetitionGuard.CountWords(text)} " +
                    $"meaningful={WhisperRepetitionGuard.MeaningfulWords(text)} " +
                    $"markers={WhisperRepetitionGuard.MarkerCount(text)} " +
                    $"repeated={WhisperRepetitionGuard.CountRepeatedTokens(text)} " +
                    $"score={WhisperRepetitionGuard.RepairScore(text)} " +
                    $"suspicious={WhisperRepetitionGuard.IsSuspiciousRepair(text, windowSeconds)}");
            }

            // Decode with Beam first; never publish a candidate before validation.
            // If its output contains mostly markers or little speech, retry the *same*
            // audio as two shorter greedy slices to avoid losing an entire window.
            RecognitionPass? beam = null;
            try
            {
                beam = await RunPassAsync(recognitionFactory, options, audioWindow,
                    beamSearch: true, detectLoop: false,
                    value => ReportPassProgress(safeSample, repairLength, value),
                    _ => { }, cancellationToken).ConfigureAwait(false);
                LogCandidate("beam", beam);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                warning = true;
                Log($"repair #{repairCount} Beam Search error: {exception}");
            }

            var beamText = beam is null ? "" : ExtractText(beam);
            var tryShort = beam is null ||
                WhisperRepetitionGuard.IsSuspiciousRepair(beamText, windowSeconds);
            RecognitionPass? shortRecovery = null;
            if (tryShort)
            {
                Log($"repair #{repairCount} Beam Search output suspicious; trying two " +
                    $"~{windowSeconds / 2:0.0}s greedy slices on exactly the same audio");
                try
                {
                    shortRecovery = await RunShortRecoveryAsync(recognitionFactory, options,
                        audioWindow, value => ReportPassProgress(safeSample, repairLength, value),
                        cancellationToken).ConfigureAwait(false);
                    LogCandidate("short-greedy", shortRecovery);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    warning = true;
                    Log($"repair #{repairCount} short-window greedy error: {exception}");
                }
            }

            var selectShort = shortRecovery is not null &&
                (beam is null ||
                 WhisperRepetitionGuard.PreferShortRecovery(beamText,
                     ExtractText(shortRecovery), windowSeconds));
            var repaired = selectShort ? shortRecovery! : beam ?? shortRecovery;
            var selectedKind = selectShort || beam is null ? "short-greedy" : "beam";

            var selectedText = repaired is null ? "" : ExtractText(repaired);
            var suspicious = repaired is null ||
                WhisperRepetitionGuard.IsSuspiciousRepair(selectedText, windowSeconds);

            if (suspicious)
            {
                warning = true;
                // Do NOT append 25 seconds of candidate text and then resume 8 seconds
                // later: that would duplicate overlapping audio. The two short passes
                // already cover this interval; accept only segments from its first half
                // and rerun the rest using the regular decoder.
                var shortParts = shortRecovery?.Confirmed;
                var cautiousAdvance = Math.Max(1, repairLength / 2);
                var retainedParts = 0;
                if (shortParts is not null)
                {
                    var limit = TimeSpan.FromSeconds((double)cautiousAdvance / SamplesPerSecond);
                    foreach (var part in shortParts)
                    {
                        if (part.Segment.End <= limit &&
                            WhisperRepetitionGuard.MeaningfulWords(part.Segment.Text) > 0)
                        {
                            Append(part, safeSample);
                            retainedParts++;
                        }
                    }
                }
                cursorSample = Math.Min(samples.Length, safeSample + cautiousAdvance);
                Log($"repair #{repairCount} UNRESOLVED: beam={beam is not null} " +
                    $"short={shortRecovery is not null} selected={selectedKind} " +
                    $"beamWords={WhisperRepetitionGuard.MeaningfulWords(beamText)} " +
                    $"shortWords={WhisperRepetitionGuard.MeaningfulWords(shortRecovery is null ? "" : ExtractText(shortRecovery))} " +
                    $"retainedEarlySegments={retainedParts} " +
                    $"POSSIBLE MISSING AUDIO [{safeSample / (double)SamplesPerSecond:0.00}," +
                    $"{cursorSample / (double)SamplesPerSecond:0.00}]s " +
                    $"resuming remainder from {cursorSample / (double)SamplesPerSecond:0.00}s");
            }
            else
            {
                foreach (var part in repaired!.Confirmed)
                {
                    Append(part, safeSample);
                }

                var advancedBy = ChooseAdvance(repairLength,
                    repaired.Confirmed.Count > 0 ? repaired.Confirmed[^1].Segment.End : null);
                cursorSample = Math.Min(samples.Length, safeSample + advancedBy);
                Log($"repair #{repairCount} SELECTED={selectedKind} reason=plausible-transcript " +
                    $"chars={selectedText.Length} meaningful={WhisperRepetitionGuard.MeaningfulWords(selectedText)} " +
                    $"markers={WhisperRepetitionGuard.MarkerCount(selectedText)} " +
                    $"resume={cursorSample / (double)SamplesPerSecond:0.00}s elapsed={clock.Elapsed:c}");
            }

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
        TimeSpan? detectedAt = null;
        string? loopExcerpt = null;

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
                if (WhisperRepetitionGuard.CountRepeatedTokens(recent.ToString()) > 0 ||
                    WhisperRepetitionGuard.HasMarkerLoop(recent.ToString()))
                {
                    loopDetected = true;
                    detectedAt = segment.End;
                    loopExcerpt = Regex.Replace(
                        WhisperRepetitionGuard.Tail(recent.ToString(), 150), @"\s+", " ").Trim();
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

        return new RecognitionPass(confirmed, loopDetected, detectedAt, loopExcerpt);
    }

    internal static int ToSample(TimeSpan offset) =>
        (int)Math.Max(0, Math.Round(offset.TotalSeconds * SamplesPerSecond));

    internal static int ChooseAdvance(int windowSamples, TimeSpan? lastSegmentEnd)
    {
        if (lastSegmentEnd is null)
        {
            return windowSamples;
        }

        var lastEnd = ToSample(lastSegmentEnd.Value);
        // Use the last recognized segment boundary rather than blindly skipping
        // unfinished speech at the end of the repair window.
        return lastEnd > SamplesPerSecond
            ? Math.Clamp(lastEnd, 1, windowSamples)
            : windowSamples;
    }

    private static double AudioRmsDb(ReadOnlySpan<float> audio)
    {
        if (audio.IsEmpty)
        {
            return -120;
        }

        double sum = 0;
        var count = 0;
        for (var i = 0; i < audio.Length; i += 64)
        {
            sum += (double)audio[i] * audio[i];
            count++;
        }

        return Math.Max(-120, 10 * Math.Log10(Math.Max(sum / count, 1e-12)));
    }

    private static string ExtractText(RecognitionPass pass)
    {
        var result = new StringBuilder();
        foreach (var part in pass.Confirmed)
        {
            result.Append(part.Segment.Text);
        }
        return result.ToString();
    }

    private static async Task<RecognitionPass> RunShortRecoveryAsync(
        WhisperFactory recognitionFactory,
        RecognitionOptions options,
        ReadOnlyMemory<float> audio,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        var firstLength = audio.Length / 2;
        if (firstLength == 0)
        {
            return await RunPassAsync(recognitionFactory, options, audio,
                beamSearch: false, detectLoop: false, progress, _ => { }, cancellationToken)
                .ConfigureAwait(false);
        }

        var first = await RunPassAsync(recognitionFactory, options,
            audio[..firstLength], beamSearch: false, detectLoop: false,
            value => progress(value / 2), _ => { }, cancellationToken).ConfigureAwait(false);
        var second = await RunPassAsync(recognitionFactory, options,
            audio[firstLength..], beamSearch: false, detectLoop: false,
            value => progress(50 + value / 2), _ => { }, cancellationToken).ConfigureAwait(false);

        var parts = new List<RecognizedPart>(first.Confirmed.Count + second.Confirmed.Count);
        parts.AddRange(first.Confirmed);
        var shift = TimeSpan.FromSeconds((double)firstLength / SamplesPerSecond);
        foreach (var part in second.Confirmed)
        {
            parts.Add(part with
            {
                Segment = part.Segment with
                {
                    Start = part.Segment.Start + shift,
                    End = part.Segment.End + shift
                }
            });
        }

        return new RecognitionPass(parts, LoopDetected: false);
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
