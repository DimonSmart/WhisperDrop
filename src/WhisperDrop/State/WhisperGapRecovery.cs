using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using WhisperDrop.Models;

namespace WhisperDrop.State;

// Conservative, bounded post-pass recovery. Energy only indicates sound, not necessarily speech;
// agreement of independent decoders is required before changing a transcript.
internal enum GapProbeKind { MissingGap, SparseSegment }

internal sealed record GapProbe(int SegmentIndex, GapProbeKind Kind, TimeSpan Start, TimeSpan End)
{
    internal double DurationSeconds => (End - Start).TotalSeconds;
}

internal sealed record GapSignal(double RmsDb, double ActiveFraction, double PeakDb)
{
    // Not voice activity detection: music and noise can also pass this gate.
    internal bool HasSubstantialSound => ActiveFraction >= 0.25 && PeakDb >= -41;
}

internal sealed record GapDecodeCandidate(
    string Decoder,
    IReadOnlyList<RecognitionSegment> Segments,
    string Text,
    int Words,
    int RepeatedWords,
    int Markers,
    double WordsPerSecond,
    bool Plausible);

internal sealed record GapRecoverySelection(
    GapDecodeCandidate? Candidate,
    double Agreement,
    string Reason);

internal static class WhisperGapRecovery
{
    private const int SampleRate = 16000;
    private static readonly Regex Words = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    internal static IReadOnlyList<GapProbe> FindProbes(
        IReadOnlyList<RecognitionSegment> segments,
        double audioSeconds,
        int maxProbes = 8)
    {
        var result = new List<GapProbe>();
        for (var i = 0; i < segments.Count && result.Count < maxProbes; i++)
        {
            var current = segments[i];
            if (i > 0)
            {
                var previous = segments[i - 1];
                var gapStart = previous.End;
                var gapEnd = current.Start;
                var seconds = (gapEnd - gapStart).TotalSeconds;
                if (seconds >= 5 && seconds <= 35 &&
                    gapStart.TotalSeconds >= 0 && gapEnd.TotalSeconds <= audioSeconds)
                {
                    result.Add(new GapProbe(i, GapProbeKind.MissingGap, gapStart, gapEnd));
                    if (result.Count >= maxProbes)
                    {
                        break;
                    }
                }
            }

            var duration = (current.End - current.Start).TotalSeconds;
            var words = WhisperRepetitionGuard.MeaningfulWords(current.Text);
            if (duration >= 15 && duration <= 35 &&
                words / duration < 0.8 &&
                current.Start.TotalSeconds >= 0 && current.End.TotalSeconds <= audioSeconds)
            {
                result.Add(new GapProbe(i, GapProbeKind.SparseSegment, current.Start, current.End));
            }
        }
        return result;
    }

    internal static GapSignal MeasureSignal(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return new(-120, 0, -120);
        }

        const int frameSamples = SampleRate / 5; // 200 ms; no external VAD/model.
        var active = 0;
        var frames = 0;
        double energySum = 0;
        double maxFrame = 0;
        for (var offset = 0; offset < samples.Length; offset += frameSamples)
        {
            var end = Math.Min(samples.Length, offset + frameSamples);
            double frameEnergy = 0;
            var n = 0;
            // Subsample to keep a one-hour recording inexpensive.
            for (var j = offset; j < end; j += 8)
            {
                frameEnergy += (double)samples[j] * samples[j];
                n++;
            }
            var mean = frameEnergy / Math.Max(1, n);
            energySum += mean;
            maxFrame = Math.Max(maxFrame, mean);
            if (10 * Math.Log10(Math.Max(mean, 1e-12)) > -41)
            {
                active++;
            }
            frames++;
        }

        return new(
            ToDb(energySum / frames),
            (double)active / frames,
            ToDb(maxFrame));
    }

    private static double ToDb(double power) => Math.Max(-120, 10 * Math.Log10(Math.Max(power, 1e-12)));

    internal static GapDecodeCandidate Assess(
        string decoder,
        IReadOnlyList<RecognitionSegment> segments,
        double seconds)
    {
        var text = new StringBuilder();
        foreach (var segment in segments)
        {
            text.Append(segment.Text);
            text.Append(' ');
        }

        var allText = text.ToString().Trim();
        var words = WhisperRepetitionGuard.MeaningfulWords(allText);
        var repeated = WhisperRepetitionGuard.CountRepeatedTokens(allText);
        var markers = WhisperRepetitionGuard.MarkerCount(allText);
        var rate = words / Math.Max(0.1, seconds);
        var plausible = words >= Math.Max(6, seconds * 0.6) &&
            rate <= 5.0 &&
            repeated == 0 && markers == 0 &&
            !WhisperRepetitionGuard.HasMarkerLoop(allText);
        return new(decoder, segments, allText, words, repeated, markers, rate, plausible);
    }

    // Token-overlap Dice coefficient, case- and punctuation-insensitive. Text agreement
    // is evidence for the same speech, not a guarantee of accurate transcription.
    internal static double Agreement(string first, string second)
    {
        var a = Words.Matches(first);
        var b = Words.Matches(second);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in a)
        {
            counts[match.Value] = counts.TryGetValue(match.Value, out var n) ? n + 1 : 1;
        }
        var overlap = 0;
        foreach (Match match in b)
        {
            if (counts.TryGetValue(match.Value, out var count) && count > 0)
            {
                overlap++;
                counts[match.Value] = count - 1;
            }
        }
        return 2.0 * overlap / (a.Count + b.Count);
    }

    internal static GapRecoverySelection Choose(
        IReadOnlyList<GapDecodeCandidate> candidates,
        GapProbeKind kind,
        string existingText,
        string? previousText,
        string? nextText)
    {
        var threshold = kind == GapProbeKind.SparseSegment ? 0.65 : 0.58;
        GapDecodeCandidate? best = null;
        double bestAgreement = 0;
        var bestScore = double.NegativeInfinity;
        for (var i = 0; i < candidates.Count; i++)
        {
            var a = candidates[i];
            if (!a.Plausible)
            {
                continue;
            }
            for (var j = i + 1; j < candidates.Count; j++)
            {
                var b = candidates[j];
                if (!b.Plausible)
                {
                    continue;
                }
                var agreement = Agreement(a.Text, b.Text);
                if (agreement < threshold)
                {
                    continue;
                }
                // Among agreeing alternatives, prefer more recovered content,
                // but never select a candidate based on length without agreement.
                var choice = a.Words >= b.Words ? a : b;
                var score = agreement * 50 + choice.Words;
                if (score > bestScore)
                {
                    best = choice;
                    bestAgreement = agreement;
                    bestScore = score;
                }
            }
        }

        if (best is null)
        {
            return new(null, bestAgreement, "no-two-plausible-decoders-agree");
        }
        if (kind == GapProbeKind.SparseSegment)
        {
            var originalWords = WhisperRepetitionGuard.MeaningfulWords(existingText);
            if (best.Words < Math.Max(originalWords * 1.6, originalWords + 12))
            {
                return new(null, bestAgreement, "insufficient-improvement-over-original");
            }
        }
        else if (IsNearbyDuplicate(best.Text, previousText) ||
                 IsNearbyDuplicate(best.Text, nextText))
        {
            return new(null, bestAgreement, "candidate-duplicates-adjacent-speech");
        }

        return new(best, bestAgreement, "two-decoders-agree");
    }

    private static bool IsNearbyDuplicate(string candidate, string? neighbor) =>
        !string.IsNullOrWhiteSpace(neighbor) &&
        WhisperRepetitionGuard.MeaningfulWords(neighbor) >= 8 &&
        Agreement(candidate, neighbor) >= 0.8;

    internal static IReadOnlyList<RecognitionSegment> NormalizeWindowSegments(
        IReadOnlyList<RecognitionSegment> relativeSegments,
        GapProbe probe)
    {
        var result = new List<RecognitionSegment>();
        var duration = probe.DurationSeconds;
        foreach (var segment in relativeSegments)
        {
            var start = segment.Start.TotalSeconds;
            var end = segment.End.TotalSeconds;
            // Strictly exclude text substantially outside the observed gap.
            if (end <= 0 || start >= duration ||
                start < -0.5 || end > duration + 0.75 || end < start)
            {
                continue;
            }
            var clampedStart = Math.Clamp(start, 0, duration);
            var clampedEnd = Math.Clamp(end, 0, duration);
            result.Add(segment with
            {
                Start = probe.Start + TimeSpan.FromSeconds(clampedStart),
                End = probe.Start + TimeSpan.FromSeconds(clampedEnd)
            });
        }
        return result;
    }
}
