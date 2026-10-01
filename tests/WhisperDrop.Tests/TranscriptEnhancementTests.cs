using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhisperDrop.Models;
using WhisperDrop.State;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class TranscriptEnhancementTests
{
    [Fact]
    public void Chunker_keeps_segments_whole_and_context_read_only()
    {
        var segments = Enumerable.Range(0, 6)
            .Select(id => Segment(id, new string((char)('a' + id), 120)))
            .ToArray();

        var chunks = new TranscriptChunker().CreateChunks(segments, 1024, string.Empty);

        Assert.True(chunks.Count > 1);
        Assert.Equal(segments.Select(segment => segment.Id), chunks.SelectMany(chunk => chunk.Segments).Select(segment => segment.Id));
        Assert.All(chunks, chunk => Assert.False(chunk.IsOversized));
        Assert.DoesNotContain(
            chunks.SelectMany(chunk => chunk.Segments).Select(segment => segment.Id),
            id => chunks.Any(chunk =>
                !chunk.Segments.Any(segment => segment.Id == id) &&
                (chunk.PreviousContext.Any(segment => segment.Id == id) ||
                 chunk.NextContext.Any(segment => segment.Id == id)) &&
                chunk.Segments.Any(segment => segment.Id == id)));
    }

    [Fact]
    public void Token_estimator_is_conservative_for_ascii_cyrillic_and_mixed_text()
    {
        Assert.InRange(TranscriptChunker.EstimateTokens("abcdefghijkl"), 4, 8);
        Assert.True(TranscriptChunker.EstimateTokens("Привет мир") >= 8);
        Assert.True(TranscriptChunker.EstimateTokens("Entity Framework и PostgreSQL") >= 8);
    }

    [Fact]
    public void Oversized_segment_is_never_silently_split()
    {
        var segment = Segment(0, new string('x', 3000));

        var chunk = Assert.Single(new TranscriptChunker().CreateChunks([segment], 1024, string.Empty));

        Assert.True(chunk.IsOversized);
        Assert.Same(segment, Assert.Single(chunk.Segments));
    }

    [Fact]
    public void Structured_validation_rejects_missing_duplicate_unknown_and_context_ids()
    {
        var target = Segment(10, "source");
        var context = Segment(9, "before");
        var chunk = new TranscriptChunk([target], [context], [], false);

        Assert.Throws<TranscriptEnhancementException>(() =>
            TranscriptEnhancementService.ValidateChunkResult(chunk, new TranscriptChunkResult([])));
        Assert.Throws<TranscriptEnhancementException>(() =>
            TranscriptEnhancementService.ValidateChunkResult(
                chunk,
                new TranscriptChunkResult(
                [
                    new CorrectedSegment(10, "one", false),
                    new CorrectedSegment(10, "two", false)
                ])));
        Assert.Throws<TranscriptEnhancementException>(() =>
            TranscriptEnhancementService.ValidateChunkResult(
                chunk,
                new TranscriptChunkResult([new CorrectedSegment(99, "unknown", false)])));
        Assert.Throws<TranscriptEnhancementException>(() =>
            TranscriptEnhancementService.ValidateChunkResult(
                chunk,
                new TranscriptChunkResult([new CorrectedSegment(9, "context", false)])));
    }

    [Fact]
    public void Structured_validation_reorders_by_original_segment_id()
    {
        var chunk = new TranscriptChunk([Segment(1, "one"), Segment(2, "two")], [], [], false);

        var result = TranscriptEnhancementService.ValidateChunkResult(
            chunk,
            new TranscriptChunkResult(
            [
                new CorrectedSegment(2, "Two.", false),
                new CorrectedSegment(1, "One.", false)
            ]));

        Assert.Equal([1, 2], result.Select(segment => segment.Id));
    }

    [Fact]
    public async Task Service_processes_chunks_sequentially_and_merges_paragraph_boundaries()
    {
        var factory = new FakeAgentFactory(chunk => new TranscriptChunkResult(
            chunk.Segments
                .Reverse()
                .Select(segment => new CorrectedSegment(
                    segment.Id,
                    segment.Id == 0 ? "First." : "Second.",
                    segment.Id == 0))
                .ToArray()));
        var service = new TranscriptEnhancementService(factory, new TranscriptChunker());
        var recognition = new RecognitionResult(
            " first second",
            "en",
            [Segment(0, " first"), Segment(1, " second")]);
        var progressValues = new List<double>();

        var result = await service.EnhanceAsync(
            recognition,
            new AiPostProcessingOptions { ContextSize = 4096 },
            new CollectingProgress(progressValues));

        Assert.Equal("First." + Environment.NewLine + Environment.NewLine + "Second.", result.Transcript);
        Assert.Equal([0, 1], result.Segments.Select(segment => segment.Id));
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, factory.AgentCalls);
        Assert.Equal(0d, progressValues[0]);
        Assert.Equal(1d, progressValues[^1]);
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("ftp://localhost/model")]
    public void Invalid_endpoint_is_rejected_before_agent_creation(string endpoint)
    {
        var options = new AiPostProcessingOptions { Endpoint = endpoint };

        Assert.Throws<TranscriptEnhancementException>(() => options.GetValidatedEndpoint());
    }

    [Fact]
    public void Equivalent_endpoint_trailing_slashes_normalize_to_the_same_uri()
    {
        var first = new AiPostProcessingOptions { Endpoint = "http://localhost:11434/v1" }.GetValidatedEndpoint();
        var second = new AiPostProcessingOptions { Endpoint = "http://localhost:11434/v1/" }.GetValidatedEndpoint();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Failure_does_not_produce_a_partial_result()
    {
        var factory = new FakeAgentFactory(_ =>
            throw new TranscriptEnhancementException("invalid"));
        var service = new TranscriptEnhancementService(factory, new TranscriptChunker());
        var recognition = new RecognitionResult("raw", "en", [Segment(0, "raw")]);

        await Assert.ThrowsAsync<TranscriptEnhancementException>(() =>
            service.EnhanceAsync(recognition, new AiPostProcessingOptions()));
        Assert.Equal("raw", recognition.Transcript);
    }

    private static RecognitionSegment Segment(int id, string text) =>
        new(
            id,
            TimeSpan.FromSeconds(id),
            TimeSpan.FromSeconds(id + 1),
            text,
            0.8f,
            0.7f,
            0.9f,
            0.01f);

    private sealed class FakeAgentFactory(Func<TranscriptChunk, TranscriptChunkResult> responseFactory)
        : ITranscriptEnhancementAgentFactory
    {
        public int CreateCount { get; private set; }

        public int AgentCalls { get; private set; }

        public ITranscriptEnhancementAgent Create(AiPostProcessingOptions options)
        {
            CreateCount++;
            return new FakeAgent(this, responseFactory);
        }

        private sealed class FakeAgent(
            FakeAgentFactory owner,
            Func<TranscriptChunk, TranscriptChunkResult> responseFactory)
            : ITranscriptEnhancementAgent
        {
            public Task<TranscriptChunkResult> CorrectAsync(
                TranscriptChunk chunk,
                string userInstructions,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.AgentCalls++;
                return Task.FromResult(responseFactory(chunk));
            }

            public Task TestConnectionAsync(CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }
    }

    private sealed class CollectingProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }
}
