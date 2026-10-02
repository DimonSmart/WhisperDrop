using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhisperDrop.Models;
using WhisperDrop.Settings;
using WhisperDrop.State;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class AiApplicationStateTests
{
    [Fact]
    public async Task Disabled_AI_never_calls_enhancement_service()
    {
        var recognition = new FakeRecognitionService();
        var enhancement = new FakeEnhancementService();
        var state = CreateState(recognition, enhancement);
        state.AddFiles(["recording.wav"]);

        await state.TranscribeAsync(state.QueueRows[0]);

        Assert.Equal(0, enhancement.CallCount);
        Assert.Equal("recording.wav raw", state.QueueRows[0].RawTranscript);
        Assert.Null(state.QueueRows[0].ProcessedTranscript);
    }

    [Fact]
    public async Task Successful_AI_preserves_raw_and_Copy_uses_processed_text()
    {
        var recognition = new FakeRecognitionService();
        var enhancement = new FakeEnhancementService { ResultText = "Improved transcript." };
        var state = CreateState(recognition, enhancement);
        state.AiPostProcessingEnabled = true;
        state.AddFiles(["recording.wav"]);

        await state.TranscribeAsync(state.QueueRows[0]);

        var item = state.QueueRows[0];
        Assert.Equal(TranscriptionState.Completed, item.TranscriptionState);
        Assert.Equal(AiEnhancementState.Completed, item.AiEnhancementState);
        Assert.Equal("recording.wav raw", item.RawTranscript);
        Assert.Equal("Improved transcript.", item.ProcessedTranscript);
        Assert.Equal("Improved transcript.", item.EffectiveTranscript);
        Assert.True(item.CanCopyRaw);
        Assert.Equal("recording.wav raw", enhancement.Inputs.Single().Transcript);
    }

    [Fact]
    public async Task AI_failure_keeps_successful_recognition_copyable()
    {
        var recognition = new FakeRecognitionService();
        var enhancement = new FakeEnhancementService
        {
            Exception = new TranscriptEnhancementException("AI server is not available.")
        };
        var state = CreateState(recognition, enhancement);
        state.AiPostProcessingEnabled = true;
        state.AddFiles(["recording.wav"]);

        await state.TranscribeAsync(state.QueueRows[0]);

        var item = state.QueueRows[0];
        Assert.Equal(TranscriptionState.Completed, item.TranscriptionState);
        Assert.Equal(AiEnhancementState.Failed, item.AiEnhancementState);
        Assert.Equal("recording.wav raw", item.EffectiveTranscript);
        Assert.True(item.CanCopy);
        Assert.Equal("AI server is not available.", item.AiEnhancementError);
    }

    [Fact]
    public async Task Improve_again_always_starts_from_original_recognition_result()
    {
        var recognition = new FakeRecognitionService();
        var enhancement = new FakeEnhancementService { ResultText = "First improvement." };
        var state = CreateState(recognition, enhancement);
        state.AddFiles(["recording.wav"]);
        var item = state.QueueRows[0];

        await state.TranscribeAsync(item);
        await state.RunAiPostProcessingAsync(item);
        enhancement.ResultText = "Second improvement.";
        state.AiInstructions = "Use different terminology.";
        await state.RunAiPostProcessingAsync(item);

        Assert.Equal(1, recognition.CallCount);
        Assert.Equal(2, enhancement.CallCount);
        Assert.All(enhancement.Inputs, input => Assert.Equal("recording.wav raw", input.Transcript));
        Assert.Equal("Second improvement.", item.ProcessedTranscript);
    }

    [Fact]
    public async Task Batch_unloads_Whisper_before_AI_and_continues_after_AI_failure()
    {
        var recognition = new FakeRecognitionService();
        var enhancement = new FakeEnhancementService
        {
            ResultFactory = recognitionResult =>
                recognitionResult.Transcript.StartsWith("second", StringComparison.Ordinal)
                    ? throw new TranscriptEnhancementException("bad AI result")
                    : recognitionResult.Transcript + " improved"
        };
        var state = CreateState(recognition, enhancement);
        state.AiPostProcessingEnabled = true;
        state.AddFiles(["first.wav", "second.wav", "third.wav"]);

        await state.TranscribeAllAsync();

        Assert.Equal(3, recognition.CallCount);
        Assert.Equal(1, recognition.UnloadCount);
        Assert.Equal(3, enhancement.CallCount);
        Assert.Equal(
            [AiEnhancementState.Completed, AiEnhancementState.Failed, AiEnhancementState.Completed],
            state.QueueRows.Select(item => item.AiEnhancementState));
        Assert.Equal(TranscriptionState.Completed, state.QueueRows[1].TranscriptionState);
        Assert.Equal("second.wav raw", state.QueueRows[1].EffectiveTranscript);
    }

    [Fact]
    public async Task AI_input_copy_contains_actual_prompt_guidance_and_raw_segment_data()
    {
        var recognition = new FakeRecognitionService();
        var state = CreateState(recognition, new FakeEnhancementService());
        state.AiInstructions = "Prefer the product spelling AcmeDB.";
        state.AddFiles(["recording.wav"]);

        await state.TranscribeAsync(state.QueueRows[0]);

        var input = state.GetAiInputForCopy(state.QueueRows[0]);

        Assert.Contains("===== SYSTEM INSTRUCTIONS =====", input);
        Assert.Contains("===== USER REQUEST 1/1 =====", input);
        Assert.Contains("Prefer the product spelling AcmeDB.", input);
        Assert.Contains("recording.wav raw", input);
        Assert.Contains("\"segmentsToCorrect\"", input);
        Assert.Contains("видна", input);
        Assert.Contains("probability", input);
    }

    [Fact]
    public void Remote_endpoint_warning_is_shown_only_for_non_loopback_addresses()
    {
        var state = CreateState(new FakeRecognitionService(), new FakeEnhancementService());

        state.AiEndpoint = "http://127.0.0.1:11434/v1/";
        Assert.Null(state.AiRemoteEndpointWarning);

        state.AiEndpoint = "https://example.com/v1/";
        Assert.Contains("https://example.com/v1/", state.AiRemoteEndpointWarning);
    }

    private static InitialApplicationState CreateState(
        FakeRecognitionService recognition,
        FakeEnhancementService enhancement)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var models = Path.Combine(root, "models");
        Directory.CreateDirectory(models);

        var paths = new ApplicationPaths(root);
        var catalog = new WhisperModelCatalog();
        var availability = new SelectedModelAvailability(catalog);
        File.WriteAllText(availability.GetModelPath(models, "base"), "model");

        var store = new JsonUserSettingsStore(paths);
        store.Save(new UserSettings { ModelsFolder = models });

        return new InitialApplicationState(
            store,
            catalog,
            new RecognitionLanguageCatalog(),
            availability,
            recognitionService: recognition,
            transcriptEnhancementService: enhancement);
    }

    private sealed class FakeRecognitionService : IRecognitionService
    {
        public int CallCount { get; private set; }

        public int UnloadCount { get; private set; }

        public Task<RecognitionResult> TranscribeAsync(
            string modelPath,
            RecognitionOptions options,
            string audioPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var text = $"{Path.GetFileName(audioPath)} raw";
            var segment = new RecognitionSegment(
                0,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                text,
                0.8f,
                0.7f,
                0.9f,
                0.01f);
            return Task.FromResult(new RecognitionResult(text, "en", [segment]));
        }

        public void UnloadModel() => UnloadCount++;

        public void Dispose()
        {
        }
    }

    private sealed class FakeEnhancementService : ITranscriptEnhancementService
    {
        public int CallCount { get; private set; }

        public List<RecognitionResult> Inputs { get; } = [];

        public string ResultText { get; set; } = "Improved.";

        public Func<RecognitionResult, string>? ResultFactory { get; set; }

        public Exception? Exception { get; set; }

        public Task<TranscriptEnhancementResult> EnhanceAsync(
            RecognitionResult recognition,
            AiPostProcessingOptions options,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Inputs.Add(recognition);
            if (Exception is not null)
            {
                return Task.FromException<TranscriptEnhancementResult>(Exception);
            }

            try
            {
                var text = ResultFactory?.Invoke(recognition) ?? ResultText;
                return Task.FromResult(new TranscriptEnhancementResult(text, []));
            }
            catch (Exception exception)
            {
                return Task.FromException<TranscriptEnhancementResult>(exception);
            }
        }

        public Task TestConnectionAsync(
            AiPostProcessingOptions options,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
