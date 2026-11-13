using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhisperDrop.Dictation;
using WhisperDrop.Models;
using WhisperDrop.Media;
using WhisperDrop.Settings;
using WhisperDrop.State;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class TranscriptionWorkspaceTests
{
    [Fact]
    public async Task Microphone_start_is_serialized_and_stop_queues_audio_for_explicit_transcription()
    {
        var recognition = new FakeRecognitionService();
        var microphone = new FakeMicrophone();
        var state = CreateState(recognition, true, microphone: microphone);
        var starting = state.ToggleMicrophoneRecordingAsync();
        Assert.False(state.CanUseMicrophone);
        await state.ToggleMicrophoneRecordingAsync();
        Assert.Equal(1, microphone.StartCount);
        microphone.Started.SetResult("microphone.wav");
        await starting;
        Assert.True(state.IsMicrophoneRecording);
        Assert.Empty(state.QueueRows);
        await state.ToggleMicrophoneRecordingAsync();
        Assert.False(state.IsMicrophoneRecording);
        var row = Assert.Single(state.QueueRows);
        Assert.Equal("Pending", row.Status);
        Assert.Empty(recognition.Paths);
        Assert.True(row.CanTranscribe);
        await state.TranscribeAsync(row);
        Assert.Equal("Completed", row.Status);
        Assert.Single(recognition.Paths);
    }

    [Fact]
    public void Partial_transcript_preview_is_available_after_first_segment_and_captured_as_a_snapshot()
    {
        var item = new TranscriptionQueueItem("long.wav");
        var notifications = new List<string?>();
        item.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        Assert.False(item.CanPreviewTranscript);
        item.AppendRecognizedSegment("Hello");
        Assert.True(item.CanPreviewTranscript);
        Assert.Contains(nameof(item.CanPreviewTranscript), notifications);

        var snapshot = item.GetPreviewTranscript();
        item.AppendRecognizedSegment(" world");
        Assert.Equal("Hello", snapshot);
        Assert.Equal("Hello world", item.GetPreviewTranscript());

        item.RecognitionResult = new RecognitionResult("Final transcript", "en");
        Assert.Equal("Final transcript", item.GetPreviewTranscript());
        item.ProcessedTranscript = "Improved transcript";
        Assert.Equal("Improved transcript", item.GetPreviewTranscript());

        item.RecognitionResult = null;
        item.ProcessedTranscript = null;
        item.ResetPartialTranscript();
        Assert.False(item.CanPreviewTranscript);
        Assert.Equal(string.Empty, item.GetPreviewTranscript());
    }

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
    public async Task Unavailable_model_stays_on_Transcribe_and_shows_download_guidance()
    {
        var recognition = new FakeRecognitionService();
        var state = CreateState(recognition, false);
        state.AddFiles(["recording.wav"]);

        await state.TranscribeAsync(state.QueueRows[0]);

        Assert.False(state.IsSelectedModelAvailable);
        Assert.Equal("Download the selected model before transcription.", state.TranscriptionGuidance);
        Assert.Empty(recognition.Paths);
    }

    [Fact]
    public async Task Batch_uses_one_recognition_settings_snapshot()
    {
        var recognition = new FakeRecognitionService();
        var state = CreateAvailableState(recognition);
        state.ProcessingDevice = ProcessingDevice.Cpu;
        state.CpuThreads = 1;
        state.AddFiles(["first.wav", "second.wav"]);
        recognition.OnCall = count => { state.ProcessingDevice = ProcessingDevice.Gpu; state.CpuThreads = null; };
        await state.TranscribeAllAsync();
        Assert.Equal(2, recognition.Options.Count);
        Assert.All(recognition.Options, option => Assert.Equal(ProcessingDevice.Cpu, option.ProcessingDevice));
        Assert.All(recognition.Options, option => Assert.Equal(1, option.CpuThreads));
        Assert.Same(recognition.Options[0], recognition.Options[1]);
    }

    [Fact]
    public void Processing_device_change_requires_restart_only_after_runtime_initialization()
    {
        var recognition = new FakeRecognitionService();
        var runtime = new FakeRuntimeSelector();
        var state = CreateState(recognition, false, runtime);

        state.ProcessingDevice = ProcessingDevice.Cpu;
        Assert.Null(state.ProcessingDeviceReloadMessage);

        runtime.IsInitializedValue = true;
        runtime.ActiveDeviceValue = ProcessingDevice.Cpu;
        state.ProcessingDevice = ProcessingDevice.Gpu;

        Assert.Equal("The model will reload for the next transcription.", state.ProcessingDeviceReloadMessage);
    }

    [Fact]
    public async Task Copy_all_filters_completed_rows_in_visual_order()
    {
        var recognition = new FakeRecognitionService("second.wav");
        var state = CreateAvailableState(recognition);
        state.AddFiles(["first.wav", "second.wav", "third.wav"]);
        state.MoveQueueItem(2, 0);
        await state.TranscribeAllAsync();

        Assert.Equal(
            "third.wav transcript" + Environment.NewLine + Environment.NewLine + "first.wav transcript",
            state.GetCompletedTranscriptsForCopy());
    }

    [Fact]
    public async Task Incomplete_result_is_visible_and_never_runs_automatic_AI()
    {
        var recognition = new FakeRecognitionService { Complete = false };
        var state = CreateAvailableState(recognition);
        state.AiPostProcessingEnabled = true;
        state.AddFiles(["partial.wav"]);
        await state.TranscribeAsync(state.QueueRows[0]);
        var item = state.QueueRows[0];
        Assert.Equal("Incomplete", item.Status);
        Assert.False(item.CanEnhance);
        Assert.Equal(AiEnhancementState.NotRequested, item.AiEnhancementState);
        Assert.NotNull(item.RawTranscript);
    }

    [Fact]
    public void Model_deletion_unloads_before_removing_only_the_catalogued_file()
    {
        var recognition = new FakeRecognitionService();
        var state = CreateAvailableState(recognition);
        var path = Path.Combine(state.ModelsFolder, "parakeet-ultra-Q8_0.gguf");
        recognition.OnUnload = () => Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(state.ModelsFolder, "keep.txt"), "keep");
        state.DeleteModel(ParakeetModelCatalog.ModelId);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(state.ModelsFolder, "keep.txt")));
        Assert.False(state.IsSelectedModelAvailable);
    }

    [Theory]
    [InlineData("Ctrl+Shift+Space", true)]
    [InlineData("Ctrl+Alt+D", true)]
    [InlineData("Shift+F12", true)]
    [InlineData("Ctrl", false)]
    [InlineData("Ctrl+Shift+Meta", false)]
    [InlineData("Ctrl+Shift+Space+X", false)]
    public void Hotkey_validation_is_predictable(string shortcut, bool expected) =>
        Assert.Equal(expected, WindowsDictationPlatform.HotkeySpec.TryParse(shortcut, out _));

    [Fact]
    public async Task Dictation_uses_shared_queue_without_automatic_AI_and_keeps_insertion_pending()
    {
        var service = new FakeRecognitionService();
        var state = CreateAvailableState(service);
        state.AiPostProcessingEnabled = true;
        var observed = new TaskCompletionSource<TranscriptionQueueItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.DictationTranscribed += row => observed.TrySetResult(row);
        var start = DateTimeOffset.Now;
        var item = state.AddDictationRecording("recording.wav", start, start.AddSeconds(2));

        Assert.NotNull(item.DictationId);
        Assert.Equal("Push-to-Talk", item.SourceLabel);
        Assert.Equal(TextInsertionStatus.Pending, item.InsertionStatus);

        state.QueueDictationRecognition(item);
        Assert.Same(item, await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TranscriptionState.Completed, item.TranscriptionState);
        Assert.Equal(AiEnhancementState.NotRequested, item.AiEnhancementState);
        Assert.Equal(TextInsertionStatus.Pending, item.InsertionStatus);
        Assert.Single(service.Paths);
        Assert.Contains(item.RawTranscript!, state.GetCompletedTranscriptsForCopy());

        state.AiPostProcessingEnabled = false;
        item.InsertionStatus = TextInsertionStatus.Submitted;
        await state.TranscribeAsync(item);
        Assert.Equal(TextInsertionStatus.NotRequested, item.InsertionStatus);
    }

    [Fact]
    public async Task Dictation_without_installed_model_keeps_audio_in_history()
    {
        var service = new FakeRecognitionService();
        var state = CreateState(service, false);
        var observed = new TaskCompletionSource<TranscriptionQueueItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.DictationTranscribed += row => observed.TrySetResult(row);
        var item = state.AddDictationRecording("unrecognized.wav", DateTimeOffset.Now, DateTimeOffset.Now);
        state.QueueDictationRecognition(item);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TranscriptionState.Error, item.TranscriptionState);
        Assert.Contains("Download", item.ErrorMessage);
        Assert.Single(state.QueueRows);
        Assert.Empty(service.Paths);
    }

    private static InitialApplicationState CreateAvailableState(FakeRecognitionService service) =>
        CreateState(service, true);

    private static InitialApplicationState CreateState(
        FakeRecognitionService service,
        bool available,
        IRecognitionRuntimeSelector? runtimeSelector = null,
        IMicrophoneCaptureService? microphone = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if (available)
        {
            File.WriteAllText(Path.Combine(folder, "parakeet-ultra-Q8_0.gguf"), "model");
        }

        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var catalog = new TestModelCatalog();
        var store = new JsonUserSettingsStore(paths);
        store.Save(new UserSettings { ModelsFolder = folder });
        return new InitialApplicationState(
            store,
            catalog,
            new RecognitionLanguageCatalog(),
            new SelectedModelAvailability(catalog),
            recognitionService: service,
            runtimeSelector: runtimeSelector,
            microphoneCaptureService: microphone);
    }

    private sealed class FakeMicrophone : IMicrophoneCaptureService
    {
        public bool IsSupported => true;
        public string AvailabilityMessage => "Microphone available";
        public int StartCount { get; private set; }
        public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Started.Task;
        }
        public Task<string> StopAsync(CancellationToken cancellationToken = default) => Task.FromResult("microphone.wav");
        public void CleanupSession() { }
    }

    private sealed class FakeRecognitionService(params string[] failures) : IRecognitionService
    {
        private readonly HashSet<string> failed = [.. failures];

        public List<string> Paths { get; } = [];

        public List<RecognitionOptions> Options { get; } = [];

        public Action<int>? OnCall { get; set; }

        public string Text { get; set; } = "initial";
        public bool Complete { get; set; } = true;
        public Action? OnUnload { get; set; }

        public Task<RecognitionResult> TranscribeAsync(
            string modelPath,
            RecognitionOptions options,
            string audioPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default,
            IProgress<string>? recognizedSegments = null)
        {
            Paths.Add(audioPath);
            Options.Add(options);
            OnCall?.Invoke(Paths.Count);
            progress?.Report(0.5);
            if (failed.Contains(Path.GetFileName(audioPath)))
            {
                throw new InvalidOperationException("broken WAV");
            }

            progress?.Report(1);
            var transcript = Text == "initial" ? $"{Path.GetFileName(audioPath)} transcript" : Text;
            return Task.FromResult(new RecognitionResult(transcript, "ru") { IsComplete = Complete });
        }

        public void UnloadModel()
        {
            OnUnload?.Invoke();
        }

        public void Dispose()
        {
        }
    }

    private sealed class CollectingProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }

    private sealed class FakeRuntimeSelector : IRecognitionRuntimeSelector
    {
        public bool IsInitializedValue { get; set; }

        public ProcessingDevice ActiveDeviceValue { get; set; } = ProcessingDevice.Auto;

        public bool IsInitialized => IsInitializedValue;

        public ProcessingDevice ActiveDevice => ActiveDeviceValue;

        public bool RequiresModelReload(ProcessingDevice requestedDevice) =>
            IsInitialized && requestedDevice != ActiveDevice;

        public string? Backend => "cpu";
        public void MarkInitialized(ProcessingDevice requestedDevice, string backend) => IsInitializedValue = true;

    }
}
