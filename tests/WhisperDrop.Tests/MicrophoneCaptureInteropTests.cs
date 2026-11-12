using System;
using System.Reflection;
using System.Runtime.InteropServices;
using WhisperDrop.Media;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class MicrophoneCaptureInteropTests
{
    [Fact]
    public void Audio_queue_buffer_reads_the_64_bit_macos_native_layout()
    {
        // AudioToolbox/AudioQueue.h: UInt32, pointer, UInt32, pointer,
        // UInt32, pointer, UInt32; the macOS ARM64 ABI aligns pointers to 8 bytes.
        if (IntPtr.Size != 8)
            return;

        var bufferType = typeof(MacOsMicrophoneCaptureService)
            .GetNestedType("AudioQueueBuffer", BindingFlags.NonPublic)!;
        var memory = Marshal.AllocHGlobal(56);
        try
        {
            Marshal.Copy(new byte[56], 0, memory, 56);
            Marshal.WriteInt32(memory, 0, 4096);
            Marshal.WriteIntPtr(memory, 8, new IntPtr(0x12345678));
            Marshal.WriteInt32(memory, 16, 2048);
            Marshal.WriteIntPtr(memory, 24, new IntPtr(0x23456789));
            Marshal.WriteInt32(memory, 32, 10);
            Marshal.WriteIntPtr(memory, 40, new IntPtr(0x34567890));
            Marshal.WriteInt32(memory, 48, 3);

            var buffer = Marshal.PtrToStructure(memory, bufferType)!;
            Assert.Equal(56, Marshal.SizeOf(bufferType));
            Assert.Equal(4096u, Read<uint>("AudioDataBytesCapacity"));
            Assert.Equal(new IntPtr(0x12345678), Read<IntPtr>("AudioData"));
            Assert.Equal(2048u, Read<uint>("AudioDataByteSize"));
            Assert.Equal(new IntPtr(0x23456789), Read<IntPtr>("UserData"));
            Assert.Equal(10u, Read<uint>("PacketDescriptionCapacity"));
            Assert.Equal(new IntPtr(0x34567890), Read<IntPtr>("PacketDescriptions"));
            Assert.Equal(3u, Read<uint>("PacketDescriptionCount"));

            T Read<T>(string name) => (T)bufferType.GetField(name)!.GetValue(buffer)!;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }
}
