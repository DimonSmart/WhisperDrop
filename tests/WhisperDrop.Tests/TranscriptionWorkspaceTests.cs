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
        Assert.All(recognition.Options, option => Assert.Equal("auto", option.LanguageCode));
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

        Assert.Equal(InitialApplicationState.TranscribeTabIndex, state.SelectedTabIndex);
        Assert.Equal("Download the selected model before transcription.", state.TranscriptionGuidance);
        Assert.Empty(recognition.Paths);
    }

    [Fact]
    public async Task Batch_uses_one_recognition_settings_snapshot()
    {
        var recognition = new FakeRecognitionService();
        var state = CreateAvailableState(recognition);
        state.RecognitionLanguageCode = "en";
        state.AddFiles(["first.wav", "second.wav", "third.wav"]);
        recognition.OnCall = count =>
        {
            if (count == 1)
            {
                state.RecognitionLanguageCode = "es";
                state.VocabularyContext = "changed";
                state.WhisperMaxContextTokens = 24;
                state.WhisperNoContext = true;
            }
        };

        await state.TranscribeAllAsync();

        Assert.Equal(3, recognition.Options.Count);
        Assert.All(recognition.Options, option => Assert.Equal("en", option.LanguageCode));
        Assert.All(recognition.Options, option => Assert.Equal(string.Empty, option.Prompt));
        Assert.All(recognition.Options, option => Assert.Equal(224, option.WhisperMaxContextTokens));
        Assert.All(recognition.Options, option => Assert.False(option.WhisperNoContext));
    }

    [Fact]
    public async Task Single_transcription_passes_task_prompt_threads_and_device_as_one_snapshot()
    {
        var recognition = new FakeRecognitionService();
        var state = CreateAvailableState(recognition);
        state.RecognitionLanguageCode = "es";
        state.SelectedTranscriptionTask = TranscriptionTask.TranslateToEnglish;
        state.VocabularyContext = "C#, .NET, WhisperDrop";
        state.CpuThreads = 1;
        state.ProcessingDevice = ProcessingDevice.Cpu;
        state.WhisperMaxContextTokens = 64;
        state.WhisperNoContext = true;
        state.AddFiles(["recording.wav"]);

        await state.TranscribeAsync(state.QueueRows[0]);

        var options = Assert.Single(recognition.Options);
        Assert.Equal("es", options.LanguageCode);
        Assert.Equal(TranscriptionTask.TranslateToEnglish, options.Task);
        Assert.Equal("C#, .NET, WhisperDrop", options.Prompt);
        Assert.Equal(1, options.CpuThreads);
        Assert.Equal(ProcessingDevice.Cpu, options.ProcessingDevice);
        Assert.Equal(64, options.WhisperMaxContextTokens);
        Assert.True(options.WhisperNoContext);
    }

    [Fact]
    public void Runtime_selector_configures_auto_and_cpu_before_first_use()
    {
        var autoSelector = new WhisperRuntimeSelector();
        var autoOptions = autoSelector.ConfigureBeforeFirstUse(ProcessingDevice.Auto);
        Assert.True(autoOptions.UseGpu);

        var cpuSelector = new WhisperRuntimeSelector();
        var cpuOptions = cpuSelector.ConfigureBeforeFirstUse(ProcessingDevice.Cpu);
        Assert.False(cpuOptions.UseGpu);
    }

    [Fact]
    public void Runtime_selector_gpu_matches_packaged_platform_support()
    {
        var selector = new WhisperRuntimeSelector();

        if (OperatingSystem.IsMacOS() &&
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64)
        {
            var options = selector.ConfigureBeforeFirstUse(ProcessingDevice.Gpu);
            Assert.True(options.UseGpu);
        }
        else
        {
            var error = Assert.Throws<RecognitionConfigurationException>(
                () => selector.ConfigureBeforeFirstUse(ProcessingDevice.Gpu));
            Assert.Contains("GPU acceleration is not available", error.Message);
        }
    }

    [Fact]
    public void Processor_plan_maps_language_task_prompt_threads_and_chunk_bounds()
    {
        var explicitPlan = WhisperRecognitionService.CreateProcessorPlan(
            new RecognitionOptions
            {
                LanguageCode = "es",
                Task = TranscriptionTask.TranslateToEnglish,
                Prompt = " C#, .NET ",
                CpuThreads = 3,
                WhisperMaxContextTokens = 64
            },
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5));

        Assert.False(explicitPlan.DetectLanguage);
        Assert.Equal("es", explicitPlan.LanguageCode);
        Assert.True(explicitPlan.Translate);
        Assert.Equal(" C#, .NET ", explicitPlan.Prompt);
        Assert.Equal(3, explicitPlan.Threads);
        Assert.Equal(64, explicitPlan.MaxContextTokens);
        Assert.False(explicitPlan.NoContext);
        Assert.Equal(TimeSpan.FromSeconds(2), explicitPlan.Offset);
        Assert.Equal(TimeSpan.FromSeconds(5), explicitPlan.Duration);

        var autoPlan = WhisperRecognitionService.CreateProcessorPlan(
            new RecognitionOptions { LanguageCode = "auto", Prompt = "   " });

        Assert.True(autoPlan.DetectLanguage);
        Assert.Null(autoPlan.LanguageCode);
        Assert.False(autoPlan.Translate);
        Assert.Null(autoPlan.Prompt);
        Assert.Null(autoPlan.Threads);
        Assert.Equal(224, autoPlan.MaxContextTokens);
        Assert.False(autoPlan.NoContext);

        var disabledPlan = WhisperRecognitionService.CreateProcessorPlan(
            new RecognitionOptions { WhisperNoContext = true, WhisperMaxContextTokens = 32 });
        Assert.True(disabledPlan.NoContext);
        Assert.Null(disabledPlan.MaxContextTokens);

        var cappedPlan = WhisperRecognitionService.CreateProcessorPlan(
            new RecognitionOptions { WhisperMaxContextTokens = 10000 });
        Assert.Equal(224, cappedPlan.MaxContextTokens);
    }

    [Fact]
    public void Factory_reload_policy_ignores_processor_options_and_tracks_model_or_device()
    {
        Assert.False(WhisperRecognitionService.RequiresRecognitionFactoryReload(
            "model.bin", ProcessingDevice.Cpu, "model.bin", ProcessingDevice.Cpu));
        Assert.True(WhisperRecognitionService.RequiresRecognitionFactoryReload(
            "model.bin", ProcessingDevice.Cpu, "other.bin", ProcessingDevice.Cpu));
        Assert.True(WhisperRecognitionService.RequiresRecognitionFactoryReload(
            "model.bin", ProcessingDevice.Cpu, "model.bin", ProcessingDevice.Auto));
    }

    [Fact]
    public void Vad_region_plan_preserves_order_skips_empty_regions_and_weights_progress()
    {
        var plans = WhisperRecognitionService.CreateVadRegionPlan(
        [
            new Whisper.net.VadSegmentData(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)),
            new Whisper.net.VadSegmentData(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
            new Whisper.net.VadSegmentData(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(13))
        ]);

        Assert.Equal(2, plans.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), plans[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(2), plans[0].Duration);
        Assert.Equal(0, plans[0].ProgressOffset, 6);
        Assert.Equal(0.25, plans[0].ProgressWeight, 6);
        Assert.Equal(TimeSpan.FromSeconds(7), plans[1].Start);
        Assert.Equal(0.25, plans[1].ProgressOffset, 6);
        Assert.Equal(0.75, plans[1].ProgressWeight, 6);

        Assert.Empty(WhisperRecognitionService.CreateVadRegionPlan([]));
    }

    [Fact]
    public void Monotonic_progress_never_moves_back_and_clamps_to_one()
    {
        var values = new List<double>();
        var reporter = new MonotonicProgressReporter(new CollectingProgress(values));

        reporter.Report(0);
        reporter.Report(0.6);
        reporter.Report(0.4);
        reporter.Report(1.4);

        Assert.Equal([0d, 0.6d, 1d], values);
    }

    [Fact]
    public async Task Translation_rejects_an_english_only_model_before_recognition()
    {
        var recognition = new FakeRecognitionService();
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var catalog = new WhisperModelCatalog();
        var availability = new SelectedModelAvailability(catalog);
        File.WriteAllText(availability.GetModelPath(folder, "base-en"), "model");
        var store = new JsonUserSettingsStore(paths);
        store.Save(new UserSettings
        {
            ModelsFolder = folder,
            SelectedModelId = "base-en",
            Task = TranscriptionTask.TranslateToEnglish
        });
        var state = new InitialApplicationState(
            store,
            catalog,
            new RecognitionLanguageCatalog(),
            availability,
            recognitionService: recognition);
        state.AddFiles(["recording.wav"]);

        await state.TranscribeAsync(state.QueueRows[0]);

        Assert.Equal("Translation requires a multilingual Whisper model.", state.TranscriptionGuidance);
        Assert.Empty(recognition.Paths);
    }

    [Fact]
    public void Processing_device_change_requires_restart_only_after_runtime_initialization()
    {
        var recognition = new FakeRecognitionService();
        var runtime = new FakeRuntimeSelector();
        var state = CreateState(recognition, false, runtime);

        state.ProcessingDevice = ProcessingDevice.Cpu;
        Assert.Null(state.ProcessingDeviceRestartMessage);

        runtime.IsInitializedValue = true;
        runtime.ActiveDeviceValue = ProcessingDevice.Cpu;
        state.ProcessingDevice = ProcessingDevice.Gpu;

        Assert.Equal("Restart required to change processing device.", state.ProcessingDeviceRestartMessage);
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

    private static InitialApplicationState CreateAvailableState(FakeRecognitionService service) =>
        CreateState(service, true);

    private static InitialApplicationState CreateState(
        FakeRecognitionService service,
        bool available,
        IWhisperRuntimeSelector? runtimeSelector = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if (available)
        {
            File.WriteAllText(Path.Combine(folder, "ggml-base.bin"), "model");
        }

        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var catalog = new WhisperModelCatalog();
        var store = new JsonUserSettingsStore(paths);
        store.Save(new UserSettings { ModelsFolder = folder });
        return new InitialApplicationState(
            store,
            catalog,
            new RecognitionLanguageCatalog(),
            new SelectedModelAvailability(catalog),
            recognitionService: service,
            runtimeSelector: runtimeSelector);
    }

    private sealed class FakeRecognitionService(params string[] failures) : IRecognitionService
    {
        private readonly HashSet<string> failed = [.. failures];

        public List<string> Paths { get; } = [];

        public List<RecognitionOptions> Options { get; } = [];

        public Action<int>? OnCall { get; set; }

        public string Text { get; set; } = "initial";

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
            return Task.FromResult(new RecognitionResult(transcript, "ru"));
        }

        public void UnloadModel()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class CollectingProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }

    private sealed class FakeRuntimeSelector : IWhisperRuntimeSelector
    {
        public bool IsInitializedValue { get; set; }

        public ProcessingDevice ActiveDeviceValue { get; set; } = ProcessingDevice.Auto;

        public bool IsInitialized => IsInitializedValue;

        public ProcessingDevice ActiveDevice => ActiveDeviceValue;

        public bool RequiresRestart(ProcessingDevice requestedDevice) =>
            IsInitialized && requestedDevice != ActiveDevice;

        public Whisper.net.WhisperFactoryOptions ConfigureBeforeFirstUse(ProcessingDevice requestedDevice) =>
            Whisper.net.WhisperFactoryOptions.Default;

        public void MarkInitialized() => IsInitializedValue = true;
    }
}
