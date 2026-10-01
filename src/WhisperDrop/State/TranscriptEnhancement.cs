using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WhisperDrop.Models;

namespace WhisperDrop.State;

public sealed record AiPostProcessingOptions
{
    public bool Enabled { get; init; }

    public string Endpoint { get; init; } = "http://localhost:11434/v1/";

    public string Model { get; init; } = "gpt-oss:20b";

    public string Instructions { get; init; } = string.Empty;

    public int? ContextSize { get; init; }

    public string ApiKey { get; init; } = string.Empty;

    public Uri GetValidatedEndpoint()
    {
        if (!Uri.TryCreate(Endpoint?.Trim(), UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new TranscriptEnhancementException("The AI endpoint must be an absolute HTTP or HTTPS address.");
        }

        return new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public static bool IsLoopbackEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback;
}

public sealed record TranscriptChunk(
    IReadOnlyList<RecognitionSegment> Segments,
    IReadOnlyList<RecognitionSegment> PreviousContext,
    IReadOnlyList<RecognitionSegment> NextContext,
    bool IsOversized);

public sealed record CorrectedSegment(
    int Id,
    string? Text,
    bool ParagraphBreakAfter);

public sealed record TranscriptChunkResult(
    IReadOnlyList<CorrectedSegment> Segments);

public sealed record TranscriptEnhancementResult(
    string Transcript,
    IReadOnlyList<CorrectedSegment> Segments);

public sealed record AiConnectionTestResult(bool Ok);

public sealed class TranscriptEnhancementException : Exception
{
    public TranscriptEnhancementException(string message)
        : base(message)
    {
    }

    public TranscriptEnhancementException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface ITranscriptEnhancementAgent
{
    Task<TranscriptChunkResult> CorrectAsync(
        TranscriptChunk chunk,
        string userInstructions,
        CancellationToken cancellationToken);

    Task TestConnectionAsync(CancellationToken cancellationToken);
}

public interface ITranscriptEnhancementAgentFactory
{
    ITranscriptEnhancementAgent Create(AiPostProcessingOptions options);
}

public interface ITranscriptEnhancementService
{
    Task<TranscriptEnhancementResult> EnhanceAsync(
        RecognitionResult recognition,
        AiPostProcessingOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task TestConnectionAsync(
        AiPostProcessingOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class TranscriptChunker
{
    internal const int DefaultContextSize = 8192;
    private const double TargetInputFraction = 0.40;
    private const int ProtocolReserveTokens = 320;
    private const int ContextOverlapSegments = 2;

    public IReadOnlyList<TranscriptChunk> CreateChunks(
        IReadOnlyList<RecognitionSegment> segments,
        int? contextSize,
        string? userInstructions)
    {
        if (segments.Count == 0)
        {
            return [];
        }

        var effectiveContext = contextSize is > 0 ? contextSize.Value : DefaultContextSize;
        var instructionTokens = EstimateTokens(userInstructions ?? string.Empty);
        var targetBudget = Math.Max(
            64,
            (int)Math.Floor(effectiveContext * TargetInputFraction) -
            ProtocolReserveTokens -
            instructionTokens);

        var ranges = new List<(int Start, int Count, bool Oversized)>();
        var start = 0;
        while (start < segments.Count)
        {
            var firstCost = EstimateSegmentTokens(segments[start]);
            if (firstCost > targetBudget)
            {
                ranges.Add((start, 1, true));
                start++;
                continue;
            }

            var count = 0;
            var cost = 0;
            while (start + count < segments.Count)
            {
                var next = EstimateSegmentTokens(segments[start + count]);
                if (count > 0 && cost + next > targetBudget)
                {
                    break;
                }

                cost += next;
                count++;
            }

            ranges.Add((start, count, false));
            start += count;
        }

        var result = new List<TranscriptChunk>(ranges.Count);
        foreach (var range in ranges)
        {
            var previousStart = Math.Max(0, range.Start - ContextOverlapSegments);
            var previousCount = range.Start - previousStart;
            var nextStart = range.Start + range.Count;
            var nextCount = Math.Min(ContextOverlapSegments, segments.Count - nextStart);

            result.Add(new TranscriptChunk(
                segments.Skip(range.Start).Take(range.Count).ToArray(),
                segments.Skip(previousStart).Take(previousCount).ToArray(),
                segments.Skip(nextStart).Take(nextCount).ToArray(),
                range.Oversized));
        }

        return result;
    }

    internal static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var tokens = 0;
        var asciiRun = 0;

        void FlushAsciiRun()
        {
            if (asciiRun == 0)
            {
                return;
            }

            tokens += (asciiRun + 2) / 3;
            asciiRun = 0;
        }

        foreach (var character in text)
        {
            if (character <= 0x7f && char.IsLetterOrDigit(character))
            {
                asciiRun++;
                continue;
            }

            FlushAsciiRun();

            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            // Non-ASCII scripts are deliberately estimated conservatively.
            // Punctuation and symbols also receive their own budget.
            tokens++;
        }

        FlushAsciiRun();
        return tokens;
    }

    private static int EstimateSegmentTokens(RecognitionSegment segment) =>
        Math.Max(1, EstimateTokens(segment.Text) + 12);
}

public sealed class TranscriptEnhancementService : ITranscriptEnhancementService
{
    private readonly ITranscriptEnhancementAgentFactory agentFactory;
    private readonly TranscriptChunker chunker;

    public TranscriptEnhancementService(
        ITranscriptEnhancementAgentFactory agentFactory,
        TranscriptChunker chunker)
    {
        this.agentFactory = agentFactory;
        this.chunker = chunker;
    }

    public async Task<TranscriptEnhancementResult> EnhanceAsync(
        RecognitionResult recognition,
        AiPostProcessingOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recognition);
        ArgumentNullException.ThrowIfNull(options);

        _ = options.GetValidatedEndpoint();

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new TranscriptEnhancementException("An AI model must be configured.");
        }

        if (recognition.Segments.Count == 0)
        {
            progress?.Report(1);
            return new TranscriptEnhancementResult(recognition.Transcript, []);
        }

        var chunks = chunker.CreateChunks(recognition.Segments, options.ContextSize, options.Instructions);
        if (chunks.Any(chunk => chunk.IsOversized))
        {
            throw new TranscriptEnhancementException(
                "A transcript segment exceeds the configured AI context size.");
        }

        var agent = agentFactory.Create(options);
        var corrected = new Dictionary<int, CorrectedSegment>(recognition.Segments.Count);
        progress?.Report(0);

        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunks[index];
            var response = await agent.CorrectAsync(
                chunk,
                options.Instructions,
                cancellationToken).ConfigureAwait(false);

            foreach (var segment in ValidateChunkResult(chunk, response))
            {
                corrected.Add(segment.Id, segment);
            }

            progress?.Report((double)(index + 1) / chunks.Count);
        }

        if (corrected.Count != recognition.Segments.Count)
        {
            throw new TranscriptEnhancementException(
                "The AI model returned an incomplete structured response.");
        }

        var ordered = recognition.Segments
            .Select(segment => corrected[segment.Id])
            .ToArray();

        return new TranscriptEnhancementResult(
            Merge(recognition.Segments, corrected),
            ordered);
    }

    public async Task TestConnectionAsync(
        AiPostProcessingOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = options.GetValidatedEndpoint();

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new TranscriptEnhancementException("An AI model must be configured.");
        }

        var agent = agentFactory.Create(options);
        await agent.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<CorrectedSegment> ValidateChunkResult(
        TranscriptChunk chunk,
        TranscriptChunkResult? response)
    {
        if (response?.Segments is null)
        {
            throw new TranscriptEnhancementException(
                "The AI model returned an invalid structured response.");
        }

        var expected = chunk.Segments.ToDictionary(segment => segment.Id);
        var seen = new HashSet<int>();
        var validated = new List<CorrectedSegment>(response.Segments.Count);

        foreach (var corrected in response.Segments)
        {
            if (!expected.TryGetValue(corrected.Id, out var source) || !seen.Add(corrected.Id))
            {
                throw new TranscriptEnhancementException(
                    "The AI model returned an invalid structured response.");
            }

            if (corrected.Text is null ||
                (!string.IsNullOrWhiteSpace(source.Text) && string.IsNullOrWhiteSpace(corrected.Text)))
            {
                throw new TranscriptEnhancementException(
                    "The AI model returned an invalid structured response.");
            }

            validated.Add(corrected);
        }

        if (seen.Count != expected.Count)
        {
            throw new TranscriptEnhancementException(
                "The AI model returned an incomplete structured response.");
        }

        return chunk.Segments.Select(segment =>
            validated.Single(corrected => corrected.Id == segment.Id)).ToArray();
    }

    internal static string Merge(
        IReadOnlyList<RecognitionSegment> source,
        IReadOnlyDictionary<int, CorrectedSegment> corrected)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < source.Count; index++)
        {
            var segment = corrected[source[index].Id];
            var text = segment.Text!.Trim();

            if (text.Length > 0)
            {
                builder.Append(text);
            }

            if (index == source.Count - 1)
            {
                continue;
            }

            builder.Append(segment.ParagraphBreakAfter ? Environment.NewLine + Environment.NewLine : " ");
        }

        return builder.ToString();
    }
}

internal sealed class UnavailableTranscriptEnhancementService : ITranscriptEnhancementService
{
    private static TranscriptEnhancementException CreateException() =>
        new("AI post-processing is not available in this application configuration.");

    public Task<TranscriptEnhancementResult> EnhanceAsync(
        RecognitionResult recognition,
        AiPostProcessingOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<TranscriptEnhancementResult>(CreateException());

    public Task TestConnectionAsync(
        AiPostProcessingOptions options,
        CancellationToken cancellationToken = default) =>
        Task.FromException(CreateException());
}
