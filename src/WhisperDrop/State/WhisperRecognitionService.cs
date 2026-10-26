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

    public void Dispose()
    {
        try
        {
            writer?.Dispose();
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"[WhisperDrop] Could not finalize diagnostic file: {exception.Message}");
        }
    }
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
            HasMarkerLoop(text) ||
            markers > 0 && words < 8 ||
            seconds >= 10 && words < Math.Max(6, seconds * 0.5);
    }

    internal static bool HasMarkerLoop(string text)
    {
        var matches = NonSpeechMarker.Matches(text);
        for (var i = 1; i < matches.Count; i++)
        {
            var previousEnd = matches[i - 1].Index + matches[i - 1].Length;
            if (string.IsNullOrWhiteSpace(text.Substring(previousEnd, matches[i].Index - previousEnd)))
            {
                return true;
            }
        }
        return false;
    }

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

// Diagnostic-only timestamp analysis. Long spans and gaps are indicators, not proof of lost speech.
internal sealed record RecognitionTimingCoverage(
    double FirstStartSeconds,
    double LastEndSeconds,
    double CoveredSeconds,
    double LargestInternalGapSeconds,
    double LeadingGapSeconds,
    double TrailingGapSeconds,
    double OutsideWindowSeconds);

internal static class WhisperTimingDiagnostics
{
    private const double LongSegmentSeconds = 15;
    private const double LargeGapSeconds = 5;
    private const double SignificantOverlapSeconds = 3;
    private const double TimestampToleranceSeconds = 0.1;

    internal static string? ExplainAcceptedSegment(
        RecognitionSegment? previous,
        RecognitionSegment segment,
        double audioDurationSeconds)
    {
        var details = new List<string>();
        var start = segment.Start.TotalSeconds;
        var end = segment.End.TotalSeconds;
        var duration = end - start;
        if (duration >= LongSegmentSeconds)
        {
            details.Add($"long-segment duration={duration:0.00}s");
        }
        if (duration < -TimestampToleranceSeconds)
        {
            details.Add($"invalid-duration duration={duration:0.00}s");
        }
        if (previous is not null)
        {
            var gap = start - previous.End.TotalSeconds;
            if (gap >= LargeGapSeconds)
            {
                details.Add($"large-gap duration={gap:0.00}s previousEnd={previous.End.TotalSeconds:0.00}s");
            }
            else if (gap <= -SignificantOverlapSeconds)
            {
                details.Add($"large-overlap duration={-gap:0.00}s previousEnd={previous.End.TotalSeconds:0.00}s");
            }
        }
        if (start < -TimestampToleranceSeconds || end > audioDurationSeconds + TimestampToleranceSeconds)
        {
            details.Add($"outside-audio start={start:0.00}s end={end:0.00}s audioEnd={audioDurationSeconds:0.00}s");
        }
        return details.Count == 0 ? null :
            $"segment={segment.Id} start={start:0.00}s end={end:0.00}s chars={segment.Text.Length} " +
            string.Join(" ", details);
    }

    internal static RecognitionTimingCoverage AnalyzeCoverage(
        IReadOnlyList<RecognitionSegment> segments,
        double windowSeconds)
    {
        windowSeconds = Math.Max(0, windowSeconds);
        if (segments.Count == 0)
        {
            return new(0, 0, 0, 0, windowSeconds, windowSeconds, 0);
        }

        var intervals = new List<(double Start, double End)>(segments.Count);
        var first = double.PositiveInfinity;
        var last = double.NegativeInfinity;
        double beyond = 0;
        foreach (var segment in segments)
        {
            var start = segment.Start.TotalSeconds;
            var end = segment.End.TotalSeconds;
            first = Math.Min(first, start);
            last = Math.Max(last, end);
            if (end < start)
            {
                continue;
            }

            beyond += Math.Max(0, -start) + Math.Max(0, end - windowSeconds);
            var clippedStart = Math.Clamp(start, 0, windowSeconds);
            var clippedEnd = Math.Clamp(end, 0, windowSeconds);
            if (clippedEnd > clippedStart)
            {
                intervals.Add((clippedStart, clippedEnd));
            }
        }

        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        var covered = 0d;
        var largestGap = 0d;
        var previousEnd = 0d;
        var firstCoveredStart = intervals.Count > 0 ? intervals[0].Start : windowSeconds;
        foreach (var (start, end) in intervals)
        {
            if (start > previousEnd)
            {
                if (previousEnd > 0)
                {
                    largestGap = Math.Max(largestGap, start - previousEnd);
                }
                covered += end - start;
                previousEnd = end;
            }
            else if (end > previousEnd)
            {
                covered += end - previousEnd;
                previousEnd = end;
            }
        }

        return new(
            first, last, covered, largestGap,
            firstCoveredStart,
            windowSeconds - previousEnd,
            beyond);
    }
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
            var earlier = segments.Count > 0 ? segments[^1] : null;
            var anomaly = WhisperTimingDiagnostics.ExplainAcceptedSegment(
                earlier, seg, total.TotalSeconds);
            if (anomaly is not null)
            {
                Log($"timing accepted {anomaly}");
            }
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
                var timestamps = new List<RecognitionSegment>(candidate.Confirmed.Count);
                foreach (var part in candidate.Confirmed)
                {
                    timestamps.Add(part.Segment);
                }
                var coverage = WhisperTimingDiagnostics.AnalyzeCoverage(timestamps, windowSeconds);
                Log($"repair #{repairCount} candidate={decoder} start={safeSample / (double)SamplesPerSecond:0.00}s " +
                    $"window={windowSeconds:0.00}s audioRms={audioDb:0.0}dB " +
                    $"segments={candidate.Confirmed.Count} first={firstStart}s last={lastEnd}s " +
                    $"chars={text.Length} words={WhisperRepetitionGuard.CountWords(text)} " +
                    $"meaningful={WhisperRepetitionGuard.MeaningfulWords(text)} " +
                    $"markers={WhisperRepetitionGuard.MarkerCount(text)} " +
                    $"repeated={WhisperRepetitionGuard.CountRepeatedTokens(text)} " +
                    $"score={WhisperRepetitionGuard.RepairScore(text)} " +
                    $"suspicious={WhisperRepetitionGuard.IsSuspiciousRepair(text, windowSeconds)} " +
                    $"coverage={coverage.CoveredSeconds:0.00}s " +
                    $"leadingGap={coverage.LeadingGapSeconds:0.00}s " +
                    $"largestInternalGap={coverage.LargestInternalGapSeconds:0.00}s " +
                    $"trailingGap={coverage.TrailingGapSeconds:0.00}s " +
                    $"rawTimeRange=[{coverage.FirstStartSeconds:0.00},{coverage.LastEndSeconds:0.00}]s " +
                    $"outsideWindow={coverage.OutsideWindowSeconds:0.00}s");
                if (coverage.OutsideWindowSeconds > 0.1 ||
                    coverage.LargestInternalGapSeconds >= 5 ||
                    coverage.LeadingGapSeconds >= 5 ||
                    coverage.TrailingGapSeconds >= 5)
                {
                    Log($"repair #{repairCount} TIMING-ANOMALY candidate={decoder} " +
                        $"start={safeSample / (double)SamplesPerSecond:0.00}s " +
                        $"end={(safeSample + repairLength) / (double)SamplesPerSecond:0.00}s " +
                        $"covered={coverage.CoveredSeconds:0.00}/{windowSeconds:0.00}s " +
                        $"outside={coverage.OutsideWindowSeconds:0.00}s " +
                        $"leadingGap={coverage.LeadingGapSeconds:0.00}s " +
                        $"internalGap={coverage.LargestInternalGapSeconds:0.00}s " +
                        $"trailingGap={coverage.TrailingGapSeconds:0.00}s");
                }
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

        // Examine gaps and unusually sparse long segments only after the primary
        // decoder is finished. This keeps streaming behavior and loop repair unchanged.
        var gapReview = await ReviewPossibleMissingSpeechAsync(
            recognitionFactory, options, samples, segments, progress, Log, cancellationToken)
            .ConfigureAwait(false);
        if (gapReview.Recovered > 0)
        {
            segments.Clear();
            segments.AddRange(gapReview.Segments);
            transcript.Clear();
            foreach (var segment in segments)
            {
                transcript.Append(segment.Text);
            }
            // Do not append out-of-order candidate text to the incremental preview;
            // RecognitionResult below contains the rebuilt chronologically sorted result.
        }
        if (gapReview.Unresolved > 0)
        {
            warning = true;
        }

        progress.Report(1);
        var overallCoverage = WhisperTimingDiagnostics.AnalyzeCoverage(segments, total.TotalSeconds);
        Log($"timeline-summary audio={total.TotalSeconds:0.00}s " +
            $"firstSegment={overallCoverage.FirstStartSeconds:0.00}s " +
            $"lastSegment={overallCoverage.LastEndSeconds:0.00}s " +
            $"timestampCoverage={overallCoverage.CoveredSeconds:0.00}s " +
            $"leadingGap={overallCoverage.LeadingGapSeconds:0.00}s " +
            $"largestInternalGap={overallCoverage.LargestInternalGapSeconds:0.00}s " +
            $"trailingGap={overallCoverage.TrailingGapSeconds:0.00}s " +
            $"outsideAudio={overallCoverage.OutsideWindowSeconds:0.00}s");
        Log($"done segments={segments.Count} characters={transcript.Length} " +
            $"repairs={repairCount} warning={warning} elapsed={clock.Elapsed:c}");
        return new RecognitionResult(transcript.ToString(), detectedLanguage, segments)
        {
            QualityWarning = warning
                ? "Whisper detected uncertain speech or repeated phrases. Some content may be missing; please review the transcript."
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
        if (windowSamples <= 0)
        {
            return 0;
        }

        // A tiny/absent timestamp is not evidence that the remaining window
        // contained no speech. Never skip the entire window on that basis.
        var lastEnd = lastSegmentEnd is null ? 0 : ToSample(lastSegmentEnd.Value);
        return lastEnd > 0
            ? Math.Clamp(lastEnd, 1, windowSamples)
            : Math.Min(windowSamples, SamplesPerSecond * 8);
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


    private sealed record GapReviewResult(
        IReadOnlyList<RecognitionSegment> Segments,
        int Recovered,
        int Unresolved,
        int Skipped);

    // A second, bounded phase after normal streaming completes. Never cancels normal
    // speech or rewinds the main decoder: it only studies suspicious timestamp gaps.
    private static async Task<GapReviewResult> ReviewPossibleMissingSpeechAsync(
        WhisperFactory recognitionFactory,
        RecognitionOptions options,
        ReadOnlyMemory<float> samples,
        IReadOnlyList<RecognitionSegment> original,
        MonotonicProgressReporter progress,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var probes = WhisperGapRecovery.FindProbes(
            original, samples.Length / (double)SamplesPerSecond);
        log($"gap-review start probes={probes.Count} max=8 decoders=greedy,beam,split-greedy");
        if (probes.Count == 0)
        {
            return new(original, 0, 0, 0);
        }

        var recovered = 0;
        var unresolved = 0;
        var skipped = 0;
        var additions = new Dictionary<int, IReadOnlyList<RecognitionSegment>>();
        var replacements = new Dictionary<int, IReadOnlyList<RecognitionSegment>>();

        for (var p = 0; p < probes.Count; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = probes[p];
            var first = Math.Clamp(ToSample(probe.Start), 0, samples.Length);
            var last = Math.Clamp(ToSample(probe.End), first, samples.Length);
            var length = last - first;
            if (length < SamplesPerSecond)
            {
                skipped++;
                log($"gap-review #{p + 1} SKIPPED reason=invalid-window kind={probe.Kind}");
                continue;
            }

            var audio = samples.Slice(first, length);
            var signal = WhisperGapRecovery.MeasureSignal(audio.Span);
            log($"gap-review #{p + 1} kind={probe.Kind} audio=[{probe.Start.TotalSeconds:0.00}," +
                $"{probe.End.TotalSeconds:0.00}]s duration={probe.DurationSeconds:0.00}s " +
                $"rms={signal.RmsDb:0.0}dB peak={signal.PeakDb:0.0}dB " +
                $"soundFraction={signal.ActiveFraction:0.00} signalPresent={signal.HasSubstantialSound}");

            if (!signal.HasSubstantialSound)
            {
                skipped++;
                log($"gap-review #{p + 1} SKIPPED reason=low-acoustic-energy " +
                    "(sound detection is not speech verification)");
                continue;
            }

            // Candidate passes never report text to the UI: insertion/replacement is
            // provisional until independently decoded transcripts agree.
            var candidates = new List<GapDecodeCandidate>(3);
            foreach (var decoder in new[] { "greedy", "beam", "split-greedy" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var clock = Stopwatch.StartNew();
                    RecognitionPass pass;
                    if (decoder == "split-greedy")
                    {
                        pass = await RunShortRecoveryAsync(recognitionFactory, options, audio,
                            _ => { }, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        pass = await RunPassAsync(recognitionFactory, options, audio,
                            beamSearch: decoder == "beam", detectLoop: false,
                            _ => { }, _ => { }, cancellationToken).ConfigureAwait(false);
                    }

                    var relative = new List<RecognitionSegment>(pass.Confirmed.Count);
                    foreach (var part in pass.Confirmed)
                    {
                        relative.Add(part.Segment);
                    }
                    var coverage = WhisperTimingDiagnostics.AnalyzeCoverage(relative, probe.DurationSeconds);
                    var bounded = WhisperGapRecovery.NormalizeWindowSegments(relative, probe);
                    var candidate = WhisperGapRecovery.Assess(decoder, bounded, probe.DurationSeconds);
                    candidates.Add(candidate);
                    log($"gap-review #{p + 1} candidate={decoder} " +
                        $"segmentsRaw={relative.Count} segmentsInside={bounded.Count} " +
                        $"words={candidate.Words} rate={candidate.WordsPerSecond:0.00}/s " +
                        $"repeated={candidate.RepeatedWords} markers={candidate.Markers} " +
                        $"plausible={candidate.Plausible} coverage={coverage.CoveredSeconds:0.00}s " +
                        $"leadingGap={coverage.LeadingGapSeconds:0.00}s " +
                        $"trailingGap={coverage.TrailingGapSeconds:0.00}s " +
                        $"outsideWindow={coverage.OutsideWindowSeconds:0.00}s " +
                        $"elapsed={clock.Elapsed.TotalSeconds:0.00}s " +
                        $"excerpt=\"{WhisperRepetitionGuard.Tail(candidate.Text, 160).Replace('\n', ' ')}\"");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    log($"gap-review #{p + 1} decoder={decoder} ERROR {exception}");
                }
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                for (var j = i + 1; j < candidates.Count; j++)
                {
                    var a = candidates[i];
                    var b = candidates[j];
                    log($"gap-review #{p + 1} AGREEMENT {a.Decoder}~{b.Decoder}=" +
                        $"{WhisperGapRecovery.Agreement(a.Text, b.Text):0.000} " +
                        $"plausibleA={a.Plausible} plausibleB={b.Plausible}");
                }
            }

            var existingText = probe.Kind == GapProbeKind.SparseSegment
                ? original[probe.SegmentIndex].Text : "";
            var left = probe.SegmentIndex > 0 ? original[probe.SegmentIndex - 1].Text : null;
            var right = probe.Kind == GapProbeKind.MissingGap
                ? original[probe.SegmentIndex].Text
                : probe.SegmentIndex + 1 < original.Count
                    ? original[probe.SegmentIndex + 1].Text : null;
            var selected = WhisperGapRecovery.Choose(
                candidates, probe.Kind, existingText, left, right);
            if (selected.Candidate is not null)
            {
                if (probe.Kind == GapProbeKind.MissingGap)
                {
                    additions[probe.SegmentIndex] = selected.Candidate.Segments;
                }
                else
                {
                    replacements[probe.SegmentIndex] = selected.Candidate.Segments;
                }
                recovered++;
                log($"gap-review #{p + 1} APPLIED decoder={selected.Candidate.Decoder} " +
                    $"words={selected.Candidate.Words} segments={selected.Candidate.Segments.Count} " +
                    $"agreement={selected.Agreement:0.000} reason={selected.Reason}");
            }
            else
            {
                unresolved++;
                log($"gap-review #{p + 1} UNRESOLVED agreement={selected.Agreement:0.000} " +
                    $"reason={selected.Reason}; original transcript preserved");
            }

            progress.Report(0.95 + 0.04 * (p + 1) / probes.Count);
        }

        if (recovered == 0)
        {
            log($"gap-review done recovered=0 unresolved={unresolved} skipped={skipped} " +
                "original transcript unchanged");
            return new(original, 0, unresolved, skipped);
        }

        var merged = new List<RecognitionSegment>(original.Count + recovered * 15);
        for (var i = 0; i < original.Count; i++)
        {
            if (additions.TryGetValue(i, out var inserted))
            {
                merged.AddRange(inserted);
            }
            if (replacements.TryGetValue(i, out var replaced))
            {
                merged.AddRange(replaced);
            }
            else
            {
                merged.Add(original[i]);
            }
        }

        for (var i = 0; i < merged.Count; i++)
        {
            merged[i] = merged[i] with { Id = i };
        }

        log($"gap-review done recovered={recovered} unresolved={unresolved} " +
            $"skipped={skipped} segmentsBefore={original.Count} segmentsAfter={merged.Count}");
        return new(merged, recovered, unresolved, skipped);
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
