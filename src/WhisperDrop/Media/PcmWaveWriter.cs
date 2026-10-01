using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace WhisperDrop.Media;

internal sealed class PcmWaveWriter : IDisposable
{
    private const int HeaderSize = 44;
    private const int SampleRate = 16000;
    private const short ChannelCount = 1;
    private const short BitsPerSample = 16;

    private readonly FileStream stream;
    private bool completed;
    private long dataLength;

    public PcmWaveWriter(string path)
    {
        stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        stream.Write(new byte[HeaderSize]);
    }

    public long DataLength => dataLength;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (completed)
            throw new InvalidOperationException("The WAV writer has already been completed.");

        if (dataLength + bytes.Length > uint.MaxValue - 36L)
            throw new IOException("The normalized WAV exceeds the RIFF size limit.");

        stream.Write(bytes);
        dataLength += bytes.Length;
    }

    public void Complete()
    {
        if (completed)
            return;

        Span<byte> header = stackalloc byte[HeaderSize];
        Encoding.ASCII.GetBytes("RIFF", header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], checked((uint)(36 + dataLength)));
        Encoding.ASCII.GetBytes("WAVE", header[8..]);
        Encoding.ASCII.GetBytes("fmt ", header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..22], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..24], ChannelCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], SampleRate);
        var byteRate = SampleRate * ChannelCount * BitsPerSample / 8;
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..32], checked((uint)byteRate));
        var blockAlign = ChannelCount * BitsPerSample / 8;
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..34], checked((ushort)blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..36], BitsPerSample);
        Encoding.ASCII.GetBytes("data", header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..44], checked((uint)dataLength));

        stream.Position = 0;
        stream.Write(header);
        stream.Flush(flushToDisk: false);
        completed = true;
    }

    public void Dispose() => stream.Dispose();
}
