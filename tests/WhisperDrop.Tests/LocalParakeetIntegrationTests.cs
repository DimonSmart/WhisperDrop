using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WhisperDrop.Media;
using WhisperDrop.Settings;
using WhisperDrop.State;
using Xunit;
using Xunit.Abstractions;

namespace WhisperDrop.Tests;

[CollectionDefinition("LocalParakeet", DisableParallelization = true)]
public sealed class LocalParakeetCollection { }

public sealed class LocalParakeetFactAttribute : FactAttribute
{
    public LocalParakeetFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PARAKEETDROP_LOCAL_MODEL") is null)
            Skip = "Opt in with PARAKEETDROP_LOCAL_MODEL; routine CI never downloads a model or requires hardware.";
    }
}

public sealed class WindowsMicrophoneFactAttribute : FactAttribute
{
    public WindowsMicrophoneFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("PARAKEETDROP_MICROPHONE_TEST") != "1" ||
            Environment.GetEnvironmentVariable("PARAKEETDROP_LOCAL_MODEL") is null)
            Skip = "Opt in on Windows with PARAKEETDROP_MICROPHONE_TEST=1 and PARAKEETDROP_LOCAL_MODEL; records the default input for three seconds.";
    }
}

[Collection("LocalParakeet")]
[Trait("Category", "LocalParakeet")]
public sealed class LocalParakeetIntegrationTests(ITestOutputHelper output)
{
    [WindowsMicrophoneFact]
    public async Task Windows_microphone_records_normalizes_and_runs_local_recognition()
    {
        var capture = new WindowsMicrophoneCaptureService();
        try
        {
            Assert.True(capture.IsSupported);
            await capture.StartAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));
            var path = await capture.StopAsync();
            Assert.True(new FileInfo(path).Length > 44);
            var media = new MediaPreparationService(new FfmpegRuntime());
            await using var prepared = await media.PrepareAsync(path);
            var bytes = await File.ReadAllBytesAsync(prepared.AudioPath);
            var peak = 0;
            for (var i = 44; i + 1 < bytes.Length; i += 2)
                peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(bytes, i)));
            using var recognition = new ParakeetRecognitionService();
            var result = await recognition.TranscribeAsync(Environment.GetEnvironmentVariable("PARAKEETDROP_LOCAL_MODEL")!,
                new() { ProcessingDevice = ProcessingDevice.Cpu, CpuThreads = 2 }, prepared.AudioPath);
            Assert.True(result.IsComplete);
            Assert.Equal("cpu", result.Backend);
            output.WriteLine($"Capture/preparation/recognition passed; PCM bytes={bytes.Length - 44}; peak={peak}; complete={result.IsComplete}; backend={result.Backend}; recognizedCharacters={result.Transcript.Length}");
            // A second start also verifies that the device and previous WAV were released.
            await capture.StartAsync();
            await Task.Delay(300);
            await capture.StopAsync();
        }
        finally { capture.CleanupSession(); }
    }

    [LocalParakeetFact]
    public async Task Production_recognition_service_runs_supported_devices_repeat_reload_and_unicode_audio()
    {
        Assert.True(OperatingSystem.IsMacOS() || (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64));
        var model = Environment.GetEnvironmentVariable("PARAKEETDROP_LOCAL_MODEL")!;
        var verifyWindowsGpu = OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("PARAKEETDROP_LOCAL_GPU_TESTS") == "1";
        var devices = OperatingSystem.IsWindows() && !verifyWindowsGpu
            ? new[] { ProcessingDevice.Cpu, ProcessingDevice.Auto }
            : new[] { ProcessingDevice.Cpu, ProcessingDevice.Gpu, ProcessingDevice.Auto };
        var gpuBackend = OperatingSystem.IsWindows() ? "vulkan" : "metal";
        var source = Environment.GetEnvironmentVariable("PARAKEETDROP_LOCAL_AUDIO") ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "parakeet-speech.wav");
        var root = Path.Combine(Path.GetTempPath(), "ParakeetDrop-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var unicode = Path.Combine(root, "запись café.wav");
            File.Copy(source, unicode);
            var media = new MediaPreparationService(new FfmpegRuntime());
            await using var prepared = await media.PrepareAsync(unicode);
            var longAudio = Path.Combine(root, "repeated-speech.wav");
            var preparedBytes = await File.ReadAllBytesAsync(prepared.AudioPath);
            using (var writer = new PcmWaveWriter(longAudio))
            {
                for (var repeat = 0; repeat < 16; repeat++) writer.Write(preparedBytes.AsSpan(44));
                writer.Complete();
            }
            using var service = new ParakeetRecognitionService();
            foreach (var device in devices)
            {
                string? first = null;
                for (var run = 0; run < 2; run++)
                {
                    var timer = Stopwatch.StartNew();
                    var progressCalls = 0;
                    var segmentCalls = 0;
                    var result = await service.TranscribeAsync(model, new() { ProcessingDevice = device, CpuThreads = 2 }, prepared.AudioPath,
                        new CallbackProgress<double>(_ => progressCalls++), recognizedSegments: new CallbackProgress<string>(_ => segmentCalls++));
                    Assert.Equal(0, progressCalls);
                    Assert.Equal(0, segmentCalls);
                    Assert.True(result.IsComplete);
                    Assert.False(string.IsNullOrWhiteSpace(result.Transcript));
                    if (device == ProcessingDevice.Cpu)
                        Assert.Equal("cpu", result.Backend);
                    else if (device == ProcessingDevice.Auto && OperatingSystem.IsWindows() && !verifyWindowsGpu)
                        Assert.Contains(result.Backend, new[] { "cpu", "vulkan" });
                    else
                        Assert.Equal(gpuBackend, result.Backend);
                    Assert.All(result.Segments, s => Assert.Null(s.Probability));
                    if (first is not null) Assert.Equal(first, result.Transcript);
                    first = result.Transcript;
                    output.WriteLine($"Requested={device}; actual={result.Backend}; run={run}; complete={result.IsComplete}; segments={result.Segments.Count}; elapsedMs={timer.ElapsedMilliseconds}");
                }
                using var cancellation = new CancellationTokenSource();
                cancellation.CancelAfter(250);
                var cancellationTimer = Stopwatch.StartNew();
                var cancelledRun = service.TranscribeAsync(model, new() { ProcessingDevice = device, CpuThreads = 2 }, longAudio, cancellationToken: cancellation.Token);
                await Task.Delay(150);
                GC.Collect(); GC.WaitForPendingFinalizers();
                await Assert.ThrowsAsync<OperationCanceledException>(() => cancelledRun);
                var recovery = await service.TranscribeAsync(model, new() { ProcessingDevice = device, CpuThreads = 2 }, prepared.AudioPath);
                Assert.True(recovery.IsComplete);
                if (device == ProcessingDevice.Cpu)
                    Assert.Equal("cpu", recovery.Backend);
                else if (device == ProcessingDevice.Auto && OperatingSystem.IsWindows() && !verifyWindowsGpu)
                    Assert.Contains(recovery.Backend, new[] { "cpu", "vulkan" });
                else
                    Assert.Equal(gpuBackend, recovery.Backend);
                output.WriteLine($"Requested={device}; actual={recovery.Backend}; cancellationAndRecovery=pass; elapsedMs={cancellationTimer.ElapsedMilliseconds}");
                service.UnloadModel();
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

}
