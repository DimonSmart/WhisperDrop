using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhisperDrop.State;

/// <summary>Explicit local acceptance instrumentation. Never serializes domain objects or content.</summary>
public static class ApplicationAcceptanceProbe
{
    public static void Record(string action, object metrics)
    {
        var destination = Environment.GetEnvironmentVariable("PARAKEETDROP_ACCEPTANCE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(destination)) return;
        try
        {
            File.AppendAllText(destination, JsonSerializer.Serialize(new
            {
                action, utc = DateTimeOffset.UtcNow, metrics
            }) + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void RecordRun(InitialApplicationState state, TimeSpan elapsed)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PARAKEETDROP_ACCEPTANCE_EVIDENCE"))) return;
        using var process = Process.GetCurrentProcess();
        Record("run", new
        {
            elapsedSeconds = elapsed.TotalSeconds,
            peakWorkingSetBytes = process.PeakWorkingSet64,
            aiEnabled = state.AiPostProcessingEnabled,
            backend = state.ProcessingBackendStatus,
            rows = state.QueueRows.Select(row => new
            {
                status = row.Status, canCopy = row.CanCopy, canPreview = row.CanPreviewTranscript,
                previewMatchesResult = row.GetPreviewTranscript() == row.EffectiveTranscript,
                characters = row.RawTranscript?.Length ?? 0,
                segments = row.RecognitionResult?.Segments.Count ?? 0,
                backend = row.RecognitionResult?.Backend,
                complete = row.RecognitionResult?.IsComplete,
                lastSegmentSeconds = row.RecognitionResult?.Segments.LastOrDefault()?.End.TotalSeconds,
                segmentTimesOrdered = row.RecognitionResult?.Segments.Zip(row.RecognitionResult.Segments.Skip(1))
                    .All(pair => pair.First.Start <= pair.Second.Start) ?? true,
                segmentTimesValid = row.RecognitionResult?.Segments.All(segment =>
                    segment.Start >= TimeSpan.Zero && segment.Start <= segment.End) ?? true,
                segmentsMatchFullText = row.RecognitionResult is { } result &&
                    Regex.Replace(string.Concat(result.Segments.Select(segment => segment.Text)), @"\s", "") ==
                    Regex.Replace(result.Transcript, @"\s", ""),
                aiState = row.AiEnhancementState.ToString()
            }).ToArray()
        });
    }
}
