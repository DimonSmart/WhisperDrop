using System;
using System.IO;
using System.Linq;
using WhisperDrop.State;
using WhisperDrop.Models;
using WhisperDrop.Settings;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class InitialApplicationStateTests
{
    [Fact]
    public void Starts_on_the_Settings_tab_when_the_selected_model_is_not_available()
    {
        var state = CreateState();

        Assert.Equal(InitialApplicationState.SettingsTabIndex, state.SelectedTabIndex);
        Assert.True(state.IsTranscribeWorkspaceEmpty);
    }

    [Fact]
    public void Starts_on_the_Transcribe_tab_when_the_selected_model_is_available()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(root);
        var catalog = new WhisperModelCatalog();
        var availability = new SelectedModelAvailability(catalog);
        var settings = new JsonUserSettingsStore(paths);
        settings.Save(new UserSettings { ModelsFolder = root });
        Directory.CreateDirectory(root);
        File.WriteAllText(availability.GetModelPath(root, "base"), "model");

        var state = new InitialApplicationState(settings, catalog, new RecognitionLanguageCatalog(), availability);

        Assert.Equal(InitialApplicationState.TranscribeTabIndex, state.SelectedTabIndex);
        Assert.True(state.IsTranscribeWorkspaceEmpty);
    }

    [Fact]
    public void Communicates_the_local_only_privacy_promise()
    {
        var state = CreateState();

        Assert.Equal("Transcription runs locally. Your audio never leaves this device.", state.PrivacyMessage);
    }

    [Theory]
    [InlineData("recording.wav", true)]
    [InlineData("recording.WAV", true)]
    [InlineData("recording.mp3", false)]
    [InlineData("recording", false)]
    public void Validates_supported_WAV_files(string path, bool expected)
    {
        Assert.Equal(expected, InitialApplicationState.IsSupportedAudioFile(path));
    }

    [Fact]
    public void Adds_supported_files_in_order_without_duplicates()
    {
        var state = CreateState();
        var first = Path.Combine(Path.GetTempPath(), "first.wav");
        var second = Path.Combine(Path.GetTempPath(), "second.wav");

        var result = state.AddFiles([first, second, first]);

        Assert.Equal(2, result.AddedCount);
        Assert.Empty(result.UnsupportedFiles);
        Assert.Equal(["first.wav", "second.wav"], state.QueueRows.Select(row => row.FileName));
        Assert.False(state.IsTranscribeWorkspaceEmpty);
    }

    [Fact]
    public void Rejects_unsupported_files_with_a_clear_message()
    {
        var state = CreateState();

        var result = state.AddFiles(["speech.mp3"]);

        Assert.Equal(0, result.AddedCount);
        Assert.Single(result.UnsupportedFiles);
        Assert.Equal("Only WAV files are supported. Unsupported files were not added.", state.UnsupportedFormatMessage);
        Assert.Empty(state.QueueRows);
    }

    [Fact]
    public void Reorders_and_removes_queued_files()
    {
        var state = CreateState();
        state.AddFiles(["first.wav", "second.wav", "third.wav"]);

        Assert.True(state.MoveQueueItem(2, 0));
        Assert.Equal(["third.wav", "first.wav", "second.wav"], state.QueueRows.Select(row => row.FileName));
        Assert.True(state.RemoveQueueItem(state.QueueRows[1]));
        Assert.Equal(["third.wav", "second.wav"], state.QueueRows.Select(row => row.FileName));

        state.RemoveQueueItem(state.QueueRows[0]);
        state.RemoveQueueItem(state.QueueRows[0]);
        Assert.True(state.IsTranscribeWorkspaceEmpty);
    }

    [Fact]
    public void New_queue_rows_start_pending_with_placeholder_actions()
    {
        var state = CreateState();
        state.AddFiles(["recording.wav"]);

        var row = Assert.Single(state.QueueRows);
        Assert.Equal("Pending", row.Status);
        Assert.Equal("—", row.Progress);
        Assert.Equal("Auto", row.Language);
        Assert.False(row.CanTranscribe);
        Assert.False(row.CanCopy);
    }

    private static InitialApplicationState CreateState()
    {
        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var catalog = new WhisperModelCatalog();
        return new InitialApplicationState(
            new JsonUserSettingsStore(paths),
            catalog,
            new RecognitionLanguageCatalog(),
            new SelectedModelAvailability(catalog));
    }
}
