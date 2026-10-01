using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperDrop.Media;

public sealed class PreparedAudio : IAsyncDisposable
{
    private readonly bool deleteOnDispose;
    private int disposed;

    internal PreparedAudio(string audioPath, TimeSpan? duration, bool deleteOnDispose)
    {
        AudioPath = audioPath;
        Duration = duration;
        this.deleteOnDispose = deleteOnDispose;
    }

    public string AudioPath { get; }
    public TimeSpan? Duration { get; }

    internal static PreparedAudio Borrowed(string audioPath, TimeSpan? duration = null) =>
        new(audioPath, duration, deleteOnDispose: false);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0 || !deleteOnDispose)
            return ValueTask.CompletedTask;

        try
        {
            File.Delete(AudioPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }
}
