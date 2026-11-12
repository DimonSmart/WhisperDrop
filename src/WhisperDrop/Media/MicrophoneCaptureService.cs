using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperDrop.Media;

public interface IMicrophoneCaptureService
{
    bool IsSupported { get; }

    string AvailabilityMessage { get; }

    void CleanupSession();

    Task<string> StartAsync(CancellationToken cancellationToken = default);

    Task<string> StopAsync(CancellationToken cancellationToken = default);
}

public sealed class UnsupportedMicrophoneCaptureService : IMicrophoneCaptureService
{
    public bool IsSupported => false;

    public string AvailabilityMessage => "Microphone recording is not yet available on Windows.";

    public void CleanupSession() { }

    public Task<string> StartAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new PlatformNotSupportedException(AvailabilityMessage));

    public Task<string> StopAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new PlatformNotSupportedException(AvailabilityMessage));
}

public sealed class MacOsMicrophoneCaptureService : IMicrophoneCaptureService
{
    private const string AudioToolboxFramework = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const int BufferCount = 3;
    private const int BufferByteCount = 4096;
    // Keep the captured source at a common recording rate; the existing media pipeline
    // performs the one required normalization to 16 kHz mono when transcription starts.
    private const int SampleRate = 44100;
    private readonly object gate = new();
    private readonly string recordingsDirectory = Path.Combine(Path.GetTempPath(), "ParakeetDrop", "recordings");
    private AudioQueueInputCallback? callback;
    private AudioStreamBasicDescription format;
    private IntPtr queue;
    private FileStream? output;
    private string? recordingPath;
    private Exception? callbackFailure;
    private long pcmByteCount;
    private bool isStopping;

    public MacOsMicrophoneCaptureService()
    {
        Directory.CreateDirectory(recordingsDirectory);
        foreach (var staleRecording in Directory.EnumerateFiles(recordingsDirectory, "*.wav", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(staleRecording); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public bool IsSupported => OperatingSystem.IsMacOS();

    public string AvailabilityMessage => "Records from the selected microphone and adds the audio to the queue.";

    public void CleanupSession()
    {
        bool shouldStop;
        lock (gate)
            shouldStop = queue != IntPtr.Zero;

        try
        {
            if (shouldStop)
            {
                try { Stop(); }
                catch (Exception) { }
            }
        }
        finally
        {
            foreach (var recording in Directory.EnumerateFiles(recordingsDirectory, "*.wav", SearchOption.TopDirectoryOnly))
                TryDelete(recording);
        }
    }

    public async Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMicrophonePermissionAsync(cancellationToken);
        return await Task.Run(Start, cancellationToken);
    }

    public Task<string> StopAsync(CancellationToken cancellationToken = default) => Task.Run(() => Stop(), cancellationToken);

    private string Start()
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("Microphone recording is available only on macOS.");

        lock (gate)
        {
            if (queue != IntPtr.Zero)
                throw new InvalidOperationException("Microphone recording is already active.");

            recordingPath = Path.Combine(recordingsDirectory,
                $"Microphone recording {DateTime.Now:yyyy-MM-dd HH-mm-ss-fff}-{Guid.NewGuid():N}.wav");
            output = new FileStream(recordingPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            WriteWaveHeader(output, 0);
            pcmByteCount = 0;
            callbackFailure = null;
            isStopping = false;
            format = new AudioStreamBasicDescription
            {
                SampleRate = SampleRate,
                FormatId = 0x6c70636d, // linear PCM
                FormatFlags = 0x0c, // signed integer, packed
                BytesPerPacket = 2,
                FramesPerPacket = 1,
                BytesPerFrame = 2,
                ChannelsPerFrame = 1,
                BitsPerChannel = 16
            };
            callback = OnInput;
            var status = AudioQueueNewInput(ref format, callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out queue);
            if (status != 0)
            {
                CleanupAfterFailedStart();
                throw new InvalidOperationException($"Could not open the microphone (AudioQueue error {status}). Check the macOS microphone permission in System Settings.");
            }

            for (var i = 0; i < BufferCount; i++)
            {
                status = AudioQueueAllocateBuffer(queue, BufferByteCount, out var buffer);
                if (status == 0)
                    status = AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
                if (status != 0)
                {
                    CleanupAfterFailedStart();
                    throw new InvalidOperationException($"Could not prepare microphone recording (AudioQueue error {status}).");
                }
            }

            status = AudioQueueStart(queue, IntPtr.Zero);
            if (status != 0)
            {
                CleanupAfterFailedStart();
                throw new InvalidOperationException($"Could not start microphone recording (AudioQueue error {status}). Check the macOS microphone permission in System Settings.");
            }

            return recordingPath;
        }
    }

    private static async Task EnsureMicrophonePermissionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bridge = PermissionBridge.Instance;
        var status = bridge.GetAuthorizationStatus();
        if (status == (int)MicrophoneAuthorizationStatus.Authorized)
            return;

        if (status != (int)MicrophoneAuthorizationStatus.NotDetermined)
            throw CreatePermissionDeniedException();

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(completion);
        try
        {
            bridge.RequestAccess(GCHandle.ToIntPtr(handle), permissionCallback);
        }
        catch
        {
            handle.Free();
            throw;
        }

        if (!await completion.Task)
            throw CreatePermissionDeniedException();
    }

    private static UnauthorizedAccessException CreatePermissionDeniedException() =>
        new("ParakeetDrop does not have microphone access. In macOS System Settings, open Privacy & Security → Microphone, enable ParakeetDrop, then try again.");

    private static readonly MicrophonePermissionCallback permissionCallback = CompletePermissionRequest;

    private static void CompletePermissionRequest(IntPtr context, int status)
    {
        var handle = GCHandle.FromIntPtr(context);
        var completion = (TaskCompletionSource<bool>)handle.Target!;
        handle.Free();
        completion.TrySetResult(status == (int)MicrophoneAuthorizationStatus.Authorized);
    }

    private enum MicrophoneAuthorizationStatus
    {
        NotDetermined = 0,
        Restricted = 1,
        Denied = 2,
        Authorized = 3
    }

    private sealed class PermissionBridge
    {
        private static readonly Lazy<PermissionBridge> instance = new(() => new PermissionBridge());
        private readonly IntPtr library;
        private readonly GetAuthorizationStatusDelegate getAuthorizationStatus;
        private readonly RequestAccessDelegate requestAccess;

        private PermissionBridge()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "native", "libparakeetdrop_microphone.dylib");
            library = NativeLibrary.Load(path);
            getAuthorizationStatus = Marshal.GetDelegateForFunctionPointer<GetAuthorizationStatusDelegate>(
                NativeLibrary.GetExport(library, "pd_microphone_authorization_status"));
            requestAccess = Marshal.GetDelegateForFunctionPointer<RequestAccessDelegate>(
                NativeLibrary.GetExport(library, "pd_request_microphone_access"));
        }

        public static PermissionBridge Instance => instance.Value;

        public int GetAuthorizationStatus() => getAuthorizationStatus();

        public void RequestAccess(IntPtr context, MicrophonePermissionCallback callback) =>
            requestAccess(context, callback);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetAuthorizationStatusDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RequestAccessDelegate(IntPtr context, MicrophonePermissionCallback callback);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MicrophonePermissionCallback(IntPtr context, int status);

    private string Stop()
    {
        IntPtr activeQueue;
        lock (gate)
        {
            if (queue == IntPtr.Zero || output is null || recordingPath is null)
                throw new InvalidOperationException("Microphone recording is not active.");
            if (isStopping)
                throw new InvalidOperationException("Microphone recording is already stopping.");
            isStopping = true;
            activeQueue = queue;
        }

        // Stop outside the callback lock. AudioQueueStop can wait for an input callback.
        var stopStatus = AudioQueueStop(activeQueue, false);
        var disposeStatus = AudioQueueDispose(activeQueue, true);
        lock (gate)
        {
            queue = IntPtr.Zero;
            callback = null;
            var writer = output;
            var path = recordingPath;
            output = null;
            recordingPath = null;

            try
            {
                if (callbackFailure is not null)
                    throw new IOException("Could not write microphone audio.", callbackFailure);
                if (stopStatus != 0 || disposeStatus != 0)
                    throw new InvalidOperationException($"Could not finish microphone recording (AudioQueue errors {stopStatus}, {disposeStatus}).");
                if (pcmByteCount == 0)
                    throw new InvalidOperationException("No microphone audio was captured. Check the selected input device and macOS microphone permission.");

                WriteWaveHeader(writer, pcmByteCount);
                writer.Flush(true);
                writer.Dispose();
                return path;
            }
            catch
            {
                writer.Dispose();
                TryDelete(path);
                throw;
            }
        }
    }

    private void OnInput(IntPtr userData, IntPtr audioQueue, IntPtr buffer, IntPtr startTime,
        uint packetCount, IntPtr packetDescriptions)
    {
        try
        {
            var audioBuffer = Marshal.PtrToStructure<AudioQueueBuffer>(buffer);
            lock (gate)
            {
                if (output is null)
                    return;
                if (audioBuffer.AudioDataByteSize > 0)
                {
                    var bytes = checked((int)audioBuffer.AudioDataByteSize);
                    var audio = new byte[bytes];
                    Marshal.Copy(audioBuffer.AudioData, audio, 0, bytes);
                    output.Write(audio, 0, bytes);
                    pcmByteCount += bytes;
                }

                if (!isStopping)
                {
                    var status = AudioQueueEnqueueBuffer(audioQueue, buffer, 0, IntPtr.Zero);
                    if (status != 0)
                        callbackFailure = new IOException($"Could not recycle a microphone buffer (AudioQueue error {status}).");
                }
            }
        }
        catch (Exception exception)
        {
            lock (gate)
                callbackFailure = exception;
        }
    }

    private void CleanupAfterFailedStart()
    {
        if (queue != IntPtr.Zero)
        {
            AudioQueueStop(queue, true);
            AudioQueueDispose(queue, true);
            queue = IntPtr.Zero;
        }
        callback = null;
        output?.Dispose();
        output = null;
        if (recordingPath is not null)
            TryDelete(recordingPath);
        recordingPath = null;
    }

    private static void WriteWaveHeader(Stream stream, long dataSize)
    {
        if (dataSize > uint.MaxValue - 36)
            throw new IOException("The microphone recording exceeded the WAV size limit.");
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Seek(0, SeekOrigin.Begin);
        writer.Write("RIFF"u8);
        writer.Write((uint)(36 + dataSize));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((uint)SampleRate);
        writer.Write((uint)(SampleRate * 2));
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write((uint)dataSize);
        if (dataSize == 0)
            writer.Flush();
        else
        {
            stream.Seek(0, SeekOrigin.End);
            writer.Flush();
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AudioQueueInputCallback(IntPtr userData, IntPtr queue, IntPtr buffer,
        IntPtr startTime, uint packetCount, IntPtr packetDescriptions);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        // Match AudioToolbox/AudioQueue.h exactly, including pointer alignment.
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueNewInput(ref AudioStreamBasicDescription format,
        AudioQueueInputCallback callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode,
        uint flags, out IntPtr audioQueue);

    [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueAllocateBuffer(IntPtr audioQueue, uint bufferByteSize, out IntPtr buffer);

    [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr audioQueue, IntPtr buffer,
        uint packetDescriptionCount, IntPtr packetDescriptions);

    [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueStart(IntPtr audioQueue, IntPtr startTime);

    [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueStop(IntPtr audioQueue, [MarshalAs(UnmanagedType.I1)] bool immediate);

    [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueDispose(IntPtr audioQueue, [MarshalAs(UnmanagedType.I1)] bool immediate);
}
