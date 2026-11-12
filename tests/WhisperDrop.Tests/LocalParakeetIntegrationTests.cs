using System;
using System.Diagnostics;
using System.IO;
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

[Collection("LocalParakeet")]
[Trait("Category", "LocalParakeet")]
public sealed class LocalParakeetIntegrationTests(ITestOutputHelper output)
{
    [LocalParakeetFact]
    public async Task Production_recognition_service_runs_CPU_Metal_repeat_reload_and_unicode_audio()
    {
        Assert.True(OperatingSystem.IsMacOS());
        var model = Environment.GetEnvironmentVariable("PARAKEETDROP_LOCAL_MODEL")!;
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
            foreach (var device in new[] { ProcessingDevice.Cpu, ProcessingDevice.Gpu, ProcessingDevice.Auto })
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
                    Assert.Equal(device == ProcessingDevice.Cpu ? "cpu" : "metal", result.Backend);
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
                Assert.Equal(device == ProcessingDevice.Cpu ? "cpu" : "metal", recovery.Backend);
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
