using System;
using System.IO;
using System.Threading.Tasks;
using WhisperDrop.Media;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class MediaTemporaryStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ParakeetDrop.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Creating_a_media_service_never_deletes_other_session_or_legacy_files()
    {
        var existing = CreateFile("ParakeetDrop/media/other-session/recording.wav");
        var legacy = CreateFile("WhisperDrop/media/previous.wav");
        var unrelated = CreateFile("ParakeetDrop/media/keep.txt");
        using var service = new MediaPreparationService(new UnavailableRuntime(), root);

        Assert.NotEqual(Path.GetDirectoryName(existing), service.TemporaryDirectory);
        foreach (var retained in new[] { existing, legacy, unrelated })
            Assert.Equal("untouched", File.ReadAllText(retained));
    }

    [Fact]
    public void Disposing_a_media_session_does_not_delete_another_sessions_audio()
    {
        using var first = new MediaPreparationService(new UnavailableRuntime(), root);
        var second = new MediaPreparationService(new UnavailableRuntime(), root);
        var firstFile = Path.Combine(first.TemporaryDirectory, "first.wav");
        var secondFile = Path.Combine(second.TemporaryDirectory, "second.wav");
        Directory.CreateDirectory(first.TemporaryDirectory);
        Directory.CreateDirectory(second.TemporaryDirectory);
        File.WriteAllText(firstFile, "first");
        File.WriteAllText(secondFile, "second");

        second.Dispose();

        Assert.Equal("first", File.ReadAllText(firstFile));
        Assert.False(File.Exists(secondFile));
    }

    [Fact]
    public void Microphone_sessions_do_not_remove_other_recordings()
    {
        var legacy = CreateFile("ParakeetDrop/recordings/old.wav");
        var first = new WindowsMicrophoneCaptureService();
        var second = new WindowsMicrophoneCaptureService();
        var mac = new MacOsMicrophoneCaptureService();
        try
        {
            Assert.NotEqual(first.RecordingsDirectory, second.RecordingsDirectory);
            Assert.NotEqual(first.RecordingsDirectory, mac.RecordingsDirectory);
            var firstFile = Path.Combine(first.RecordingsDirectory, "recording.wav");
            File.WriteAllText(firstFile, "first");
            second.CleanupSession();
            mac.CleanupSession();
            Assert.Equal("first", File.ReadAllText(firstFile));
            Assert.Equal("untouched", File.ReadAllText(legacy));
        }
        finally
        {
            first.CleanupSession();
            second.CleanupSession();
            mac.CleanupSession();
        }
    }

    [Fact]
    public async Task Preparation_failure_cleans_only_new_application_output()
    {
        var source = CreateFile("source.wav");
        var legacy = CreateFile("WhisperDrop/media/previous.wav");
        using var service = new MediaPreparationService(new UnavailableRuntime(), root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(source));

        Assert.Empty(Directory.GetFiles(service.TemporaryDirectory));
        Assert.Equal("untouched", File.ReadAllText(source));
        Assert.Equal("untouched", File.ReadAllText(legacy));
    }

    private string CreateFile(string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "untouched");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class UnavailableRuntime : IFfmpegRuntime
    {
        public FfmpegRuntimeInfo EnsureInitialized() => throw new InvalidOperationException("No media runtime is needed for this test.");
    }
}
