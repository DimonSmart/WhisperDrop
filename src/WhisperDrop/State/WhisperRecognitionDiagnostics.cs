using System;
using System.Collections.Generic;
using System.IO;
using WhisperDrop.Models;
using WhisperDrop.Settings;
namespace WhisperDrop.State;

// Operational logging and timing metrics; no transcript excerpts are written.
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
