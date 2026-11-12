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
    public void Startup_cleanup_removes_only_new_application_media_wavs()
    {
        var stale = CreateFile("ParakeetDrop/media/stale.wav");
        var legacy = CreateFile("WhisperDrop/media/previous.wav");
        var unrelated = CreateFile("ParakeetDrop/media/keep.txt");
        var nested = CreateFile("ParakeetDrop/media/nested/keep.wav");
        var other = CreateFile("other.wav");

        _ = new MediaPreparationService(new UnavailableRuntime(), root);

        Assert.False(File.Exists(stale));
        foreach (var retained in new[] { legacy, unrelated, nested, other })
            Assert.Equal("untouched", File.ReadAllText(retained));
    }

    [Fact]
    public async Task Preparation_failure_cleans_only_new_application_output()
    {
        var source = CreateFile("source.wav");
        var legacy = CreateFile("WhisperDrop/media/previous.wav");
        var service = new MediaPreparationService(new UnavailableRuntime(), root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(source));

        Assert.Empty(Directory.GetFiles(Path.Combine(root, "ParakeetDrop", "media")));
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
