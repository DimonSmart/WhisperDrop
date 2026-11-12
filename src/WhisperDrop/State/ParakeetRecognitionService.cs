using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WhisperDrop.Models;
using WhisperDrop.Settings;

namespace WhisperDrop.State;

public interface IRecognitionService : IDisposable
{
    Task<RecognitionResult> TranscribeAsync(string modelPath, RecognitionOptions options, string audioPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default,
        IProgress<string>? recognizedSegments = null);
    void UnloadModel();
}

internal interface IParakeetRuntime
{
    IParakeetModel Load(string path, ProcessingDevice device);
}
internal interface IParakeetModel : IDisposable
{
    string Backend { get; }
    IParakeetSession CreateSession(int? threads);
}
internal interface IParakeetSession : IDisposable
{
    RecognitionResult Run(float[] samples, CancellationToken token);
}

/// <summary>One native call at a time, including load, inference and teardown. No partial-result callbacks.</summary>
public sealed class ParakeetRecognitionService : IRecognitionService
{
    // Backend registry and native logging are process-global at the pinned ABI.
    private static readonly SemaphoreSlim NativeGate = new(1, 1);
    private readonly IRecognitionRuntimeSelector selector;
    private readonly IParakeetRuntime runtime;
    private readonly Func<string, CancellationToken, Task<bool>> verify;
    private IParakeetModel? model;
    private string? loadedPath;
    private ProcessingDevice loadedDevice;
    private bool disposed;

    public ParakeetRecognitionService() : this(new ParakeetRuntimeSelector()) { }
    public ParakeetRecognitionService(IRecognitionRuntimeSelector selector)
        : this(selector, new ParakeetRuntime(), (path, token) => ModelIntegrity.VerifyAsync(path, new ParakeetModelCatalog().Get(ParakeetModelCatalog.ModelId), token)) { }
    internal ParakeetRecognitionService(IRecognitionRuntimeSelector selector, IParakeetRuntime runtime,
        Func<string, CancellationToken, Task<bool>> verify)
    { this.selector = selector; this.runtime = runtime; this.verify = verify; }

    public async Task<RecognitionResult> TranscribeAsync(string modelPath, RecognitionOptions options, string audioPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default,
        IProgress<string>? recognizedSegments = null)
    {
        await NativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (model is null || loadedPath != modelPath || loadedDevice != options.ProcessingDevice)
                {
                    UnloadCore();
                    if (!await verify(modelPath, cancellationToken).ConfigureAwait(false))
                        throw new RecognitionConfigurationException("The Parakeet model is missing or invalid. Download or repair it in Models.");
                    model = runtime.Load(modelPath, options.ProcessingDevice);
                    loadedPath = modelPath;
                    loadedDevice = options.ProcessingDevice;
                    selector.MarkInitialized(options.ProcessingDevice, model.Backend);
                }
                var samples = ReadPreparedAudio(audioPath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                using var session = model.CreateSession(options.CpuThreads);
                var result = session.Run(samples, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result with { Backend = model.Backend };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new RecognitionConfigurationException("The app-local Parakeet runtime could not be loaded. Rebuild or reinstall the application for this platform.", error);
        }
        finally { NativeGate.Release(); }
    }

    internal static float[] ReadPreparedAudio(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Expected prepared PCM WAV audio.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Expected prepared PCM WAV audio.");
        var formatValid = false;
        while (stream.Position + 8 <= stream.Length)
        {
            token.ThrowIfCancellationRequested();
            var chunk = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            var next = checked(stream.Position + size + (size & 1));
            if (next > stream.Length) throw new InvalidDataException("The prepared WAV is truncated.");
            if (chunk == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("Invalid prepared WAV format.");
                var format = reader.ReadUInt16(); var channels = reader.ReadUInt16(); var rate = reader.ReadUInt32();
                reader.ReadUInt32(); var align = reader.ReadUInt16(); var bits = reader.ReadUInt16();
                formatValid = format == 1 && channels == 1 && rate == 16000 && align == 2 && bits == 16;
            }
            else if (chunk == "data")
            {
                if (!formatValid || size == 0 || size % 2 != 0 || size / 2 > int.MaxValue)
                    throw new InvalidDataException("Recognition requires nonempty 16 kHz mono PCM16 prepared audio.");
                var samples = new float[checked((int)(size / 2))];
                for (var i = 0; i < samples.Length; i++)
                {
                    if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
                    samples[i] = reader.ReadInt16() / 32768f;
                }
                return samples;
            }
            stream.Position = next;
        }
        throw new InvalidDataException("Prepared WAV has no audio samples.");
    }

    public void UnloadModel()
    {
        NativeGate.Wait();
        try { UnloadCore(); }
        finally { NativeGate.Release(); }
    }
    private void UnloadCore() { model?.Dispose(); model = null; loadedPath = null; }
    public void Dispose()
    {
        NativeGate.Wait();
        try { if (disposed) return; UnloadCore(); disposed = true; }
        finally { NativeGate.Release(); }
    }
}

internal sealed class ParakeetRuntime : IParakeetRuntime
{
    public IParakeetModel Load(string path, ProcessingDevice device)
    {
        ParakeetNative.Load();
        return SelectBackend(device, backend =>
        {
            ParakeetNative.transcribe_model_load_params_init(out var parameters);
            parameters.Backend = backend;
            var status = ParakeetNative.transcribe_model_load_file(path, ref parameters, out var handle);
            if (status == 0 && handle != 0)
            {
                try
                {
                    ParakeetNative.AssertAppLocalLibraries();
                    return (status, new NativeModel(handle, backend));
                }
                catch { ParakeetNative.transcribe_model_free(handle); throw; }
            }
            if (handle != 0) ParakeetNative.transcribe_model_free(handle);
            return (status, null);
        });
    }

    internal static IParakeetModel SelectBackend(ProcessingDevice device, Func<int, (int Status, IParakeetModel? Model)> load)
    {
        var gpuBackend = OperatingSystem.IsWindows() ? 3 : 2;
        var gpuName = OperatingSystem.IsWindows() ? "Vulkan" : "Metal";
        foreach (var backend in ParakeetRuntimeSelector.BackendOrder(device))
        {
            var (status, model) = load(backend);
            if (status == 0 && model is not null) return model;
            model?.Dispose();
            if (backend == gpuBackend && status == 8 && device == ProcessingDevice.Auto) continue;
            if (backend == gpuBackend && status == 8)
                throw new RecognitionConfigurationException($"{gpuName} is unavailable. Select Auto or CPU, or reinstall the application for this platform.");
            if (status != 0)
                throw new RecognitionConfigurationException($"Parakeet model load failed (native status {status}). Select CPU or repair the model in Models.");
            throw new RecognitionConfigurationException("The native runtime returned no model.");
        }
        throw new RecognitionConfigurationException("No usable Parakeet backend. Select CPU.");
    }

    private sealed class NativeModel : IParakeetModel
    {
        private nint handle;
        public string Backend { get; }
        public NativeModel(nint handle, int requested)
        {
            this.handle = handle;
            ParakeetNative.transcribe_device_info_init(out var info);
            ParakeetNative.Check(ParakeetNative.transcribe_device_get_info(ParakeetNative.transcribe_model_device(handle), ref info));
            Backend = ParakeetNative.Text(info.Kind);
            var expectedBackend = requested == 1 ? "cpu" : requested == 2 ? "metal" : "vulkan";
            if (Backend != expectedBackend)
                throw new RecognitionConfigurationException("The native runtime initialized an unexpected device. Select CPU or reinstall the application.");
        }
        public IParakeetSession CreateSession(int? threads)
        {
            ObjectDisposedException.ThrowIf(handle == 0, this);
            ParakeetNative.transcribe_session_params_init(out var parameters);
            if (threads is int count)
            {
                if (count < 1 || count > Environment.ProcessorCount) throw new ArgumentOutOfRangeException(nameof(threads));
                parameters.Threads = count;
            }
            var status = ParakeetNative.transcribe_session_init(handle, ref parameters, out var session);
            if (status != 0 || session == 0)
            {
                if (session != 0) ParakeetNative.transcribe_session_free(session);
                ParakeetNative.Check(status);
                throw new RecognitionConfigurationException("The native runtime returned no session.");
            }
            return new NativeSession(session);
        }
        public void Dispose() { if (handle == 0) return; ParakeetNative.transcribe_model_free(handle); handle = 0; }
    }

    private sealed class NativeSession(nint handle) : IParakeetSession
    {
        public unsafe RecognitionResult Run(float[] samples, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(handle == 0, this);
            ParakeetNative.transcribe_run_params_init(out var parameters);
            ParakeetNative.Abort abort = _ => token.IsCancellationRequested;
            ParakeetNative.transcribe_set_abort_callback(handle, abort, 0);
            try
            {
                int status;
                fixed (float* pcm = samples) status = ParakeetNative.transcribe_run(handle, pcm, samples.Length, ref parameters);
                var aborted = ParakeetNative.transcribe_was_aborted(handle);
                var truncated = ParakeetNative.transcribe_was_truncated(handle);
                var complete = CheckRunStatus(status, aborted, truncated, token);
                var segments = new List<RecognitionSegment>();
                long previousStart = 0;
                for (var i = 0; i < ParakeetNative.transcribe_n_segments(handle); i++)
                {
                    ParakeetNative.transcribe_segment_init(out var segment);
                    ParakeetNative.Check(ParakeetNative.transcribe_get_segment(handle, i, ref segment));
                    if (segment.Start < previousStart || segment.Start < 0 || segment.End < segment.Start)
                        throw new InvalidDataException("Parakeet returned invalid segment timestamps.");
                    previousStart = segment.Start;
                    segments.Add(new(i, TimeSpan.FromMilliseconds(segment.Start), TimeSpan.FromMilliseconds(segment.End),
                        ParakeetNative.Text(segment.Text), null, null, null, null));
                }
                var language = ParakeetNative.Text(ParakeetNative.transcribe_detected_language(handle));
                return new RecognitionResult(ParakeetNative.Text(ParakeetNative.transcribe_full_text(handle)),
                    string.IsNullOrWhiteSpace(language) ? null : language, segments)
                {
                    IsComplete = complete,
                    QualityWarning = complete ? null : status == 19 ? "Parakeet reported repeated output; review this incomplete result." : "Parakeet reported truncated output; this result is incomplete."
                };
            }
            finally
            {
                ParakeetNative.transcribe_set_abort_callback(handle, null, 0);
                GC.KeepAlive(abort);
            }
        }
        public void Dispose() { if (handle == 0) return; ParakeetNative.transcribe_session_free(handle); handle = 0; }
    }
    internal static bool CheckRunStatus(int status, bool aborted, bool truncated, CancellationToken token)
    {
        if (token.IsCancellationRequested || aborted || status == 13) throw new OperationCanceledException("Parakeet recognition was cancelled.", token);
        if (status is 18 or 19) return false;
        if (status == 8) throw new RecognitionConfigurationException("Parakeet inference failed on the selected backend. Select CPU and retry.");
        if (status != 0) throw new InvalidOperationException($"Parakeet recognition failed (native status {status}). Retry with another audio file.");
        return !truncated;
    }
}
