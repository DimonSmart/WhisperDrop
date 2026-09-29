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

public sealed class TranscriptionWorkspaceTests
{
    [Fact]
    public async Task Batch_uses_visual_order_and_continues_after_an_error()
    {
        var recognition = new FakeRecognitionService("second.wav");
        var state = CreateAvailableState(recognition);
        state.AddFiles(["first.wav", "second.wav", "third.wav"]);
        state.MoveQueueItem(2, 0);

        await state.TranscribeAllAsync();

        Assert.Equal(["third.wav", "first.wav", "second.wav"], recognition.Paths.Select(Path.GetFileName));
        Assert.Equal(["Completed", "Completed", "Error"], state.QueueRows.Select(row => row.Status));
        Assert.True(state.CanReorderQueue);
    }

    [Fact]
    public async Task Retranscription_replaces_result_and_auto_displays_detected_language()
    {
        var recognition = new FakeRecognitionService();
        var state = CreateAvailableState(recognition);
        state.AddFiles(["recording.wav"]);
        var item = Assert.Single(state.QueueRows);

        await state.TranscribeAsync(item);
        recognition.Text = "replacement";
        await state.TranscribeAsync(item);

        Assert.Equal("replacement", item.Transcript);
        Assert.Equal("Russian", item.Language);
        Assert.Equal("Completed", item.Status);
        Assert.Equal("100%", item.Progress);
        Assert.True(item.CanCopy);
    }

    [Fact]
    public async Task Unavailable_model_opens_Settings_without_recognition()
    {
        var recognition = new FakeRecognitionService();
        var state = CreateState(recognition, false);
        state.AddFiles(["recording.wav"]);

        await state.TranscribeAsync(state.QueueRows[0]);

        Assert.Equal(InitialApplicationState.SettingsTabIndex, state.SelectedTabIndex);
        Assert.NotNull(state.TranscriptionGuidance);
        Assert.Empty(recognition.Paths);
    }

    [Fact]
    public async Task Copy_all_filters_completed_rows_in_visual_order()
    {
        var recognition = new FakeRecognitionService("second.wav");
        var state = CreateAvailableState(recognition);
        state.AddFiles(["first.wav", "second.wav", "third.wav"]);
        state.MoveQueueItem(2, 0);
        await state.TranscribeAllAsync();

        Assert.Equal("third.wav transcript" + Environment.NewLine + Environment.NewLine + "first.wav transcript", state.GetCompletedTranscriptsForCopy());
    }

    private static InitialApplicationState CreateAvailableState(FakeRecognitionService service) => CreateState(service, true);

    private static InitialApplicationState CreateState(FakeRecognitionService service, bool available)
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if (available) File.WriteAllText(Path.Combine(folder, "ggml-base.bin"), "model");
        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var catalog = new WhisperModelCatalog();
        var store = new JsonUserSettingsStore(paths);
        store.Save(new UserSettings { ModelsFolder = folder });
        return new InitialApplicationState(store, catalog, new RecognitionLanguageCatalog(), new SelectedModelAvailability(catalog), recognitionService: service);
    }

    private sealed class FakeRecognitionService(params string[] failures) : IRecognitionService
    {
        private readonly HashSet<string> failed = [.. failures];
        public List<string> Paths { get; } = [];
        public string Text { get; set; } = "initial";
        public Task<RecognitionResult> TranscribeAsync(string modelPath, string languageCode, string audioPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            Paths.Add(audioPath);
            progress?.Report(0.5);
            if (failed.Contains(Path.GetFileName(audioPath))) throw new InvalidOperationException("broken WAV");
            progress?.Report(1);
            var transcript = Text == "initial" ? $"{Path.GetFileName(audioPath)} transcript" : Text;
            return Task.FromResult(new RecognitionResult(transcript, "ru"));
        }
        public void Dispose() { }
    }
}
