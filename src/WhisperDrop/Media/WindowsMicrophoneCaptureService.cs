using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperDrop.Media;

public sealed class WindowsMicrophoneCaptureService : IMicrophoneCaptureService
{
    private readonly object gate = new();
    private readonly Func<IWaveIn> createCapture;
    private readonly string recordingsDirectory;
    private IWaveIn? capture;
    private WaveFileWriter? writer;
    private string? recordingPath;
    private Exception? captureFailure;
    private TaskCompletionSource<Exception?>? recordingStopped;
    private bool recordingStarted;
    private long capturedBytes;

    public WindowsMicrophoneCaptureService()
        : this(CreateCapture, Path.Combine(Path.GetTempPath(), "ParakeetDrop", "recordings")) { }

    internal WindowsMicrophoneCaptureService(Func<IWaveIn> createCapture, string recordingsDirectory)
    {
        this.createCapture = createCapture;
        this.recordingsDirectory = recordingsDirectory;
        Directory.CreateDirectory(recordingsDirectory);
        DeleteRecordings();
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public string AvailabilityMessage => "Records from the default Windows input microphone and adds the audio to the queue.";

    private static IWaveIn CreateCapture()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows microphone recording requires Windows.");
        using var devices = new MMDeviceEnumerator();
        using var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        return new WasapiCapture(device);
    }

    public Task<string> StartAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (capture is not null)
                throw new InvalidOperationException("Microphone recording is already active.");
            try
            {
                // Construct on a worker thread so callbacks never depend on the UI context.
                capture = createCapture();
                recordingPath = Path.Combine(recordingsDirectory,
                    $"Microphone recording {DateTime.Now:yyyy-MM-dd HH-mm-ss-fff}-{Guid.NewGuid():N}.wav");
                writer = new WaveFileWriter(recordingPath, capture.WaveFormat);
                capturedBytes = 0;
                captureFailure = null;
                recordingStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
                recordingStarted = false;
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();
                recordingStarted = true;
                return recordingPath;
            }
            catch (Exception exception)
            {
                ReleaseCapture();
                DeleteFailedRecording();
                throw CaptureError(exception);
            }
        }
    }, cancellationToken);

    public Task<string> StopAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (gate)
        {
            if (capture is null)
                throw new InvalidOperationException("Microphone recording is not active.");
            try
            {
                // Wait for the final data/error callback before finalizing the WAV.
                ReleaseCapture();
                cancellationToken.ThrowIfCancellationRequested();
                if (captureFailure is not null)
                    throw CaptureError(captureFailure);
                if (capturedBytes == 0)
                    throw new InvalidOperationException("No microphone audio was captured. Check Windows Settings → System → Sound → Input and the microphone input level.");
                var result = recordingPath!;
                recordingPath = null;
                return result;
            }
            catch
            {
                DeleteFailedRecording();
                throw;
            }
        }
    });

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        writer!.Write(args.Buffer, 0, args.BytesRecorded);
        capturedBytes += args.BytesRecorded;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args) => recordingStopped!.TrySetResult(args.Exception);

    private void ReleaseCapture()
    {
        try
        {
            if (capture is not null && recordingStarted)
            {
                capture.StopRecording();
                captureFailure = recordingStopped!.Task.GetAwaiter().GetResult();
            }
        }
        finally
        {
            try { capture?.Dispose(); }
            finally
            {
                capture = null;
                recordingStarted = false;
                try { writer?.Dispose(); }
                finally { writer = null; }
            }
        }
    }

    private static Exception CaptureError(Exception exception)
    {
        if (exception is UnauthorizedAccessException || exception.HResult == unchecked((int)0x80070005))
            return new UnauthorizedAccessException("Microphone access was denied. In Windows Settings → Privacy & security → Microphone, enable Microphone access and Let desktop apps access your microphone, then try again.", exception);
        if (exception is COMException)
            return new InvalidOperationException("Could not record from the Windows microphone. Connect and enable an input device in Settings → System → Sound → Input; also check Privacy & security → Microphone for desktop-app access.", exception);
        return new IOException("Could not record microphone audio. Check the input device and available disk space, then try again.", exception);
    }

    public void CleanupSession()
    {
        lock (gate)
        {
            try { ReleaseCapture(); }
            catch (Exception) { }
            recordingPath = null;
            DeleteRecordings();
        }
    }

    private void DeleteFailedRecording()
    {
        if (recordingPath is not null)
            TryDelete(recordingPath);
        recordingPath = null;
    }

    private void DeleteRecordings()
    {
        foreach (var path in Directory.EnumerateFiles(recordingsDirectory, "*.wav", SearchOption.TopDirectoryOnly))
            TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
