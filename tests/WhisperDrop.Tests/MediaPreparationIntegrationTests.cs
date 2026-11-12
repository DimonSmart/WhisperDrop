using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using WhisperDrop.Media;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class MediaPreparationIntegrationTests : IDisposable
{
    private readonly string temporaryRoot = Path.Combine(Path.GetTempPath(), "ParakeetDrop.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("test.wav")]
    [InlineData("test.mp3")]
    [InlineData("test.mp4")]
    public async Task Bundled_runtime_normalizes_supported_media_to_pcm16_wave(string fixtureName)
    {
        // Linux is a build target, not a ParakeetDrop release target. The bundled
        // Linux runtime is intentionally not an acceptance requirement.
        if (OperatingSystem.IsLinux())
            return;

        var source = Fixture(fixtureName);
        var service = new MediaPreparationService(new FfmpegRuntime(), temporaryRoot);
        string output;

        await using (var prepared = await service.PrepareAsync(source))
        {
            Assert.Equal(ffmpeg.AV_LOG_QUIET, ffmpeg.av_log_get_level());
            output = prepared.AudioPath;
            Assert.NotEqual(source, output);
            Assert.Equal(Path.Combine(temporaryRoot, "ParakeetDrop", "media"), Path.GetDirectoryName(output));
            Assert.True(File.Exists(output));
            Assert.True(prepared.Duration is null || prepared.Duration > TimeSpan.Zero);

            var bytes = await File.ReadAllBytesAsync(output);
            Assert.True(bytes.Length > 44);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2)));
            Assert.Equal((uint)16000, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4)));
            Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34, 2)));
            Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4)) > 0);
        }

        Assert.False(File.Exists(output));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Video_without_audio_is_reported_as_a_file_error()
    {
        if (OperatingSystem.IsLinux())
            return;

        var service = new MediaPreparationService(new FfmpegRuntime(), temporaryRoot);

        var exception = await Assert.ThrowsAsync<MediaPreparationException>(
            () => service.PrepareAsync(Fixture("no-audio.mp4")));

        Assert.Equal(MediaPreparationErrorKind.NoAudioStream, exception.Kind);
        Assert.Equal("The file does not contain an audio track.", exception.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(temporaryRoot, "ParakeetDrop", "media")));
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public void Dispose()
    {
        if (Directory.Exists(temporaryRoot))
            Directory.Delete(temporaryRoot, recursive: true);
    }
}
