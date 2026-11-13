using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using WhisperDrop.Media;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class WindowsMicrophoneCaptureTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ParakeetDrop-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Stop_drains_capture_finalizes_wave_and_allows_another_recording()
    {
        var input = new FakeCapture();
        var service = new WindowsMicrophoneCaptureService(() => input, directory);
        try
        {
            var path = await service.StartAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync());
            input.Emit([1, 0, 2, 0]);
            Assert.Equal(path, await service.StopAsync());
            Assert.True(input.Disposed);
            using (var reader = new WaveFileReader(path))
            {
                Assert.Equal(8, reader.Length);
                var bytes = new byte[8];
                Assert.Equal(8, reader.Read(bytes, 0, bytes.Length));
                Assert.Equal(new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 }, bytes);
            }
            input = new FakeCapture();
            var second = await service.StartAsync();
            Assert.NotEqual(path, second);
            Assert.Equal(second, await service.StopAsync());
        }
        finally { service.CleanupSession(); }
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task Access_denial_explains_windows_privacy_controls_and_allows_retry()
    {
        var denied = true;
        var service = new WindowsMicrophoneCaptureService(() => denied
            ? throw new UnauthorizedAccessException()
            : new FakeCapture(), directory);
        try
        {
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.StartAsync());
            Assert.Contains("Let desktop apps access your microphone", error.Message);
            Assert.Empty(Directory.GetFiles(directory));
            denied = false;
            await service.StartAsync();
            await service.StopAsync();
        }
        finally { service.CleanupSession(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_or_failed_capture_does_not_return_a_recording(bool fail)
    {
        var input = new FakeCapture { EmitOnStop = false, Failure = fail ? new IOException("Device disconnected") : null };
        var service = new WindowsMicrophoneCaptureService(() => input, directory);
        await service.StartAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => service.StopAsync());
        Assert.True(input.Disposed);
        Assert.Empty(Directory.GetFiles(directory));
        service.CleanupSession();
    }

    [Fact]
    public async Task Closing_session_stops_active_capture_and_removes_session_audio_only()
    {
        var input = new FakeCapture();
        var service = new WindowsMicrophoneCaptureService(() => input, directory);
        var otherFile = Path.Combine(directory, "keep.txt");
        File.WriteAllText(otherFile, "keep");
        await service.StartAsync();
        service.CleanupSession();
        Assert.True(input.Disposed);
        Assert.Equal(new[] { otherFile }, Directory.GetFiles(directory));
    }

    public void Dispose() => Directory.Delete(directory, true);

    private sealed class FakeCapture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(48000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public bool Disposed { get; private set; }
        public bool EmitOnStop { get; init; } = true;
        public Exception? Failure { get; init; }
        public void StartRecording() { }
        public void StopRecording()
        {
            if (EmitOnStop) Emit([3, 0, 4, 0]);
            RecordingStopped?.Invoke(this, new StoppedEventArgs(Failure));
        }
        public void Emit(byte[] bytes) => DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
        public void Dispose()
        {
            Disposed = true;
        }
    }
}
