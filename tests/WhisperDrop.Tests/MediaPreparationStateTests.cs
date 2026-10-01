using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WhisperDrop.Media;
using WhisperDrop.Models;
using WhisperDrop.Settings;
using WhisperDrop.State;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class MediaPreparationStateTests
{
    [Fact]
    public async Task Preparing_precedes_recognition_and_recognition_receives_prepared_audio()
    {
        var media = new FakeMediaPreparationService();
        var recognition = new FakeRecognitionService();
        var state = CreateState(media, recognition);
        state.AddFiles(["meeting.mp4"]);
        var item = state.QueueRows[0];
        media.OnPrepare = () => Assert.Equal("Preparing", item.Status);

        await state.TranscribeAsync(item);

        Assert.Equal("Completed", item.Status);
        Assert.Single(recognition.Paths);
        Assert.EndsWith(".prepared.wav", recognition.Paths[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_during_preparation_returns_item_to_pending()
    {
        var media = new FakeMediaPreparationService { Cancel = true };
        var state = CreateState(media, new FakeRecognitionService());
        state.AddFiles(["meeting.mp3"]);
        var item = state.QueueRows[0];

        await Assert.ThrowsAsync<OperationCanceledException>(() => state.TranscribeAsync(item));

        Assert.Equal("Pending", item.Status);
        Assert.Equal("—", item.Progress);
        Assert.Null(item.ErrorMessage);
        Assert.Null(item.RecognitionResult);
    }

    [Fact]
    public async Task File_media_error_does_not_stop_batch()
    {
        var media = new FakeMediaPreparationService { FileFailure = "broken.mp4" };
        var recognition = new FakeRecognitionService();
        var state = CreateState(media, recognition);
        state.AddFiles(["first.mp3", "broken.mp4", "third.m4a"]);

        await state.TranscribeAllAsync();

        Assert.Equal(["Completed", "Error", "Completed"], state.QueueRows.Select(x => x.Status));
        Assert.Equal(2, recognition.Paths.Count);
    }

    [Fact]
    public async Task Runtime_media_error_stops_batch_and_sets_guidance()
    {
        var media = new FakeMediaPreparationService { RuntimeFailure = true };
        var recognition = new FakeRecognitionService();
        var state = CreateState(media, recognition);
        state.AddFiles(["first.mp3", "second.mp3"]);

        await state.TranscribeAllAsync();

        Assert.Equal(1, media.Calls);
        Assert.Equal("Error", state.QueueRows[0].Status);
        Assert.Equal("Pending", state.QueueRows[1].Status);
        Assert.Equal("The bundled media decoder could not be loaded.", state.TranscriptionGuidance);
        Assert.Empty(recognition.Paths);
    }

    private static InitialApplicationState CreateState(
        IMediaPreparationService media,
        IRecognitionService recognition)
    {
        var models = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "ggml-base.bin"), "model");

        var appRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(appRoot);
        var catalog = new WhisperModelCatalog();
        var store = new JsonUserSettingsStore(paths);
        store.Save(new UserSettings { ModelsFolder = models });

        return new InitialApplicationState(
            store,
            catalog,
            new RecognitionLanguageCatalog(),
            new SelectedModelAvailability(catalog),
            recognitionService: recognition,
            mediaPreparationService: media);
    }

    private sealed class FakeMediaPreparationService : IMediaPreparationService
    {
        public Action? OnPrepare { get; set; }
        public bool Cancel { get; set; }
        public bool RuntimeFailure { get; set; }
        public string? FileFailure { get; set; }
        public int Calls { get; private set; }

        public Task<PreparedAudio> PrepareAsync(
            string sourcePath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            OnPrepare?.Invoke();
            if (Cancel)
                throw new OperationCanceledException(cancellationToken);
            if (RuntimeFailure)
            {
                throw new MediaPreparationException(
                    MediaPreparationErrorKind.RuntimeUnavailable,
                    MediaPreparationException.GetUserMessage(MediaPreparationErrorKind.RuntimeUnavailable));
            }
            if (string.Equals(Path.GetFileName(sourcePath), FileFailure, StringComparison.Ordinal))
            {
                throw new MediaPreparationException(
                    MediaPreparationErrorKind.CorruptedOrIncomplete,
                    MediaPreparationException.GetUserMessage(MediaPreparationErrorKind.CorruptedOrIncomplete));
            }

            progress?.Report(0.5);
            return Task.FromResult(PreparedAudio.Borrowed(sourcePath + ".prepared.wav"));
        }
    }

    private sealed class FakeRecognitionService : IRecognitionService
    {
        public List<string> Paths { get; } = [];

        public Task<RecognitionResult> TranscribeAsync(
            string modelPath,
            RecognitionOptions options,
            string audioPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Paths.Add(audioPath);
            progress?.Report(1);
            return Task.FromResult(new RecognitionResult("ok", "en"));
        }

        public void UnloadModel()
        {
        }

        public void Dispose()
        {
        }
    }
}
