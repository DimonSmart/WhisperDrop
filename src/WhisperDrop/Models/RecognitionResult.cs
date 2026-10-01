using System;
using System.Collections.Generic;

namespace WhisperDrop.Models;

public sealed record RecognitionSegment(
    int Id,
    TimeSpan Start,
    TimeSpan End,
    string Text,
    float? Probability,
    float? MinProbability,
    float? MaxProbability,
    float? NoSpeechProbability);

public sealed record RecognitionResult(
    string Transcript,
    string? DetectedLanguage,
    IReadOnlyList<RecognitionSegment> Segments)
{
    public RecognitionResult(string transcript, string? detectedLanguage)
        : this(transcript, detectedLanguage, [])
    {
    }
}
