using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Native;

// An explicitly invoked research executable, deliberately outside the application solution.
internal static class Program
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    static readonly Log QuietLog = (_, _, _) => { }; // Never persist native messages containing paths/text.
    static string Normalize(string text) => Regex.Replace(text, @"\s+", "").Trim();
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] struct AlignmentProbe<T> where T : struct { public byte Prefix; public T Value; }
    static object Abi<T>(int id) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var align = Marshal.OffsetOf<AlignmentProbe<T>>(nameof(AlignmentProbe<T>.Value)).ToInt32();
        Require((nuint)size == transcribe_abi_struct_size(id) && (nuint)align == transcribe_abi_struct_align(id), $"ABI mismatch: {typeof(T).Name}");
        var offsets = typeof(T).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .ToDictionary(field => field.Name, field => Marshal.OffsetOf<T>(field.Name).ToInt32());
        return new { Type = typeof(T).Name, Size = size, Align = align, Offsets = offsets };
    }
    static int Main(string[] args)
    {
        try { return Execute(args); }
        catch (Exception error) { Console.Error.WriteLine(error is InvalidOperationException ? error.GetType().Name + ": " + error.Message : error.GetType().Name); return 1; }
    }
    static int Execute(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i += 2) options.Add(args[i], args[i+1]);
        string Get(string name, string? fallback = null) => options.TryGetValue(name, out var value) ? value : fallback ?? throw new ArgumentException("Missing " + name);
        var directory = Path.GetFullPath(Get("--native-dir"));
        Native.Load(directory);
        transcribe_log_set(QuietLog, 0);
        Require(Text(transcribe_version()) == "0.4.2", "Unexpected native version");
        Require("275cd50d2c4104c204009a197ac091b9687668e5".StartsWith(Text(transcribe_version_commit()), StringComparison.Ordinal) && Text(transcribe_version_commit()).Length >= 7, "Unexpected native commit");
        var abi = new[] { Abi<LoadParams>(0), Abi<SessionParams>(1), Abi<RunParams>(2), Abi<Timings>(5), Abi<Segment>(6), Abi<Word>(7), Abi<Token>(8), Abi<BackendParams>(15), Abi<DeviceInfo>(13) };
        transcribe_backend_init_params_init(out var init);
        var pathUtf8 = Marshal.StringToCoTaskMemUTF8(directory);
        try
        {
            init.Directory = pathUtf8;
            init.Allowed = Get("--backend", "cpu") == "cpu" ? 1u : uint.MaxValue;
            Check(transcribe_init_backends_ex(ref init));
        }
        finally { Marshal.FreeCoTaskMem(pathUtf8); }
        if (Get("--mode", "run") == "abi")
        {
            // Reject a zero-initialized public struct; no model needed.
            LoadParams invalid = default;
            Require(transcribe_model_load_file("missing-model.gguf", ref invalid, out var empty) == 14 && empty == 0, "Bad struct-size rejection failed");
            transcribe_model_load_params_init(out var missing);
            Require(transcribe_model_load_file("missing-model.gguf", ref missing, out empty) == 3 && empty == 0, "Missing model rejection failed");
            string corrupt = Path.GetTempFileName();
            try {
                File.WriteAllBytes(corrupt, [0, 1, 2, 3, 4, 5, 6, 7]);
                transcribe_model_load_params_init(out var badFile);
                Require(transcribe_model_load_file(corrupt, ref badFile, out empty) == 4 && empty == 0, "Invalid GGUF rejection failed");
            } finally { File.Delete(corrupt); }
            Console.WriteLine(JsonSerializer.Serialize(new { Version = Text(transcribe_version()), Commit = Text(transcribe_version_commit()), Abi = abi, BadStructSize = 14, MissingModel = 3, InvalidGguf = 4 }, Json));
            return 0;
        }
        var pcm = ReadPcm(Get("--audio", "silence"));
        string modelPath = Path.GetFullPath(Get("--model"));
        using (var stream = File.OpenRead(modelPath))
        {
            Require(stream.Length == 740363168, "Model byte length does not match pin");
            Require(Convert.ToHexString(SHA256.HashData(stream)).Equals("007a59761e9258779f189df396b50d12b83bb5b79e66df4b955c230d2f2a0a59", StringComparison.OrdinalIgnoreCase), "Model SHA-256 mismatch");
        }
        var reports = new List<object>();
        bool failed = false;
        var backendRequest = Get("--backend", "cpu") switch { "cpu" => 1, "metal" => 2, "vulkan" => 3, "auto" => 0, _ => throw new ArgumentException("Unknown backend") };
        int repeat = int.Parse(Get("--repeat", "1"));
        int reload = int.Parse(Get("--reload", "1"));
        int cancelMs = int.Parse(Get("--cancel-after-ms", "-1"));
        int cancelPoll = int.Parse(Get("--cancel-after-polls", "-1"));
        int context = int.Parse(Get("--context", "0"));
        Require(repeat is > 0 and <= 100 && reload is > 0 and <= 100, "Invalid repeat/reload count");
        for (int cycle = 0; cycle < reload; cycle++)
        {
            var timer = Stopwatch.StartNew();
            using var model = new Model(modelPath, backendRequest, Get("--no-mmap", "false") == "true");
            var loadMs = timer.Elapsed.TotalMilliseconds;
            string backend = model.Backend;
            Require(backend == Get("--expect-backend", Get("--backend", "cpu")), "Loaded backend: " + backend + "; does not match expectation");
            Require(model.Architecture == "parakeet", "Unexpected model architecture");
            using var session = model.CreateSession(int.Parse(Get("--threads", "0")), context);
            session.CheckInvalidInputs();
            string? firstHash = null;
            for (int iteration = 0; iteration < repeat; iteration++)
            {
                using var cancellation = new CancellationTokenSource();
                bool expectCancel = (cancelMs >= 0 || cancelPoll > 0) && (iteration == 0 || Get("--cancel-first-only", "false") != "true");
                if (expectCancel && cancelMs >= 0) cancellation.CancelAfter(cancelMs);
                GC.Collect(); GC.WaitForPendingFinalizers(); // stress callback ownership before every native run
                var result = session.Run(pcm, cancellation.Token, expectCancel ? cancelPoll : -1);
                var digest = Hash(result.Text);
                bool? equal = firstHash is null || result.Status != 0 ? null : digest == firstHash;
                if (result.Status == 0) firstHash ??= digest;
                bool runPassed = expectCancel ? result.Status == 13 && result.Aborted : result.Status == 0 && !result.Aborted && !result.Truncated;
                runPassed &= result.TimestampErrors == 0 && result.SegmentTextMatches;
                if (Get("--audio", "silence") == "silence") runPassed &= result.Text.Length == 0;
                failed |= !runPassed;
                string outcome = result.Status switch {
                    13 => "Cancelled", 18 => "Incomplete: output truncated", 19 => "Incomplete: output repetition",
                    8 => "Backend error", 0 when !result.Aborted && !result.Truncated => "Completed", _ => "Native error or incomplete"
                };
                var report = new {
                    Cycle = cycle, Iteration = iteration, Backend = backend, NativeBackend = model.RawBackend, Device = model.Device, Architecture = model.Architecture,
                    LoadedLibraries = Native.LoadedLibraries(directory),
                    NoMmap = Get("--no-mmap", "false") == "true",
                    Context = context, CancelAfterMs = expectCancel ? cancelMs : -1,
                    CancelAfterPolls = expectCancel ? cancelPoll : -1,
                    Outcome = outcome, RunPassed = runPassed,
                    Samples = pcm.Length, AudioSeconds = pcm.Length / 16000.0, LoadMs = loadMs,
                    result.Status, result.Aborted, result.Truncated, result.WallMs,
                    Rtf = result.WallMs / 1000 / (pcm.Length / 16000.0), result.Timings,
                    result.Language, result.TimestampKind, Characters = result.Text.Length,
                    Segments = result.Segments.Count, result.Words, result.Tokens,
                    result.TimestampErrors, result.SegmentTextMatches, TextEqualToFirst = equal,
                    LastTimestampMs = result.Segments.Count == 0 ? 0 : result.Segments[^1].End,
                    PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                    WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64,
                    ManagedBytes = GC.GetTotalMemory(false), result.AbortPolls
                };
                reports.Add(report);
                if (options.TryGetValue("--private-result", out var output))
                    File.WriteAllText(output + $".{cycle}.{iteration}.json", JsonSerializer.Serialize(result, Json));
            }
            session.Dispose(); session.Dispose();
            try { session.Run(pcm, CancellationToken.None); throw new InvalidOperationException("Use-after-dispose allowed"); }
            catch (ObjectDisposedException) { }
            model.Dispose(); model.Dispose();
        }
        var summary = JsonSerializer.Serialize(new { Version = Text(transcribe_version()), Commit = Text(transcribe_version_commit()), Abi = abi, Reports = reports, DoubleDispose = "pass", UseAfterDispose = "rejected", EmptyNativeInput = "rejected: 1", ZeroRunStruct = "rejected: 14" }, Json);
        File.WriteAllText(Get("--report"), summary);
        Console.WriteLine(summary);
        return failed ? 1 : 0;
    }
    static float[] ReadPcm(string path)
    {
        if (path == "silence") return new float[16000 * 5];
        using var stream = File.OpenRead(path);
        Require(stream.Length > 0 && stream.Length % sizeof(float) == 0 && stream.Length / sizeof(float) <= int.MaxValue, "Invalid raw mono float32 sample length");
        var pcm = GC.AllocateUninitializedArray<float>(checked((int)(stream.Length / sizeof(float))));
        stream.ReadExactly(MemoryMarshal.AsBytes(pcm.AsSpan()));
        Require(BitConverter.IsLittleEndian, "Raw input requires little endian host");
        foreach (float sample in pcm) Require(float.IsFinite(sample) && sample >= -1 && sample <= 1, "PCM must be finite and normalized");
        return pcm;
    }
    sealed class Model : IDisposable
    {
        nint handle;
        readonly List<Session> sessions = [];
        internal readonly object Gate = new();
        internal string Backend { get; }
        internal string RawBackend { get; }
        internal string Device { get; }
        internal string Architecture { get; }
        internal Model(string path, int backend, bool noMmap)
        {
            transcribe_model_load_params_init(out var p); p.Backend = backend; p.NoMmap = noMmap ? (byte)1 : (byte)0;
            Check(transcribe_model_load_file(path, ref p, out handle));
            try
            {
                RawBackend = Text(transcribe_model_backend(handle)); Architecture = Text(transcribe_model_arch_string(handle));
                transcribe_device_info_init(out var info);
                Check(transcribe_device_get_info(transcribe_model_device(handle), ref info));
                Backend = Text(info.Kind); Device = Text(info.Description);
            }
            catch { transcribe_model_free(handle); handle = 0; throw; }
        }
        internal Session CreateSession(int threads, int context)
        {
            lock (Gate)
            {
                ObjectDisposedException.ThrowIf(handle == 0, this);
                transcribe_session_params_init(out var p); p.Threads = threads; p.Context = context;
                Check(transcribe_session_init(handle, ref p, out var session));
                var wrapper = new Session(this, session); sessions.Add(wrapper); return wrapper;
            }
        }
        public void Dispose()
        {
            lock (Gate) { foreach (var session in sessions) session.Dispose(); if (handle != 0) transcribe_model_free(handle); handle = 0; }
        }
    }
    record Row(long Start, long End, string Text);
    record Result(int Status, bool Aborted, bool Truncated, string Text, string Language, int TimestampKind,
        List<Row> Segments, int Words, int Tokens, int TimestampErrors, bool SegmentTextMatches,
        double WallMs, Timings Timings, int AbortPolls);
    sealed class Session(Model model, nint initialHandle) : IDisposable
    {
        nint handle = initialHandle;
        internal unsafe void CheckInvalidInputs()
        {
            lock (model.Gate)
            {
                ObjectDisposedException.ThrowIf(handle == 0, this);
                transcribe_run_params_init(out var p);
                Require(transcribe_run(handle, null, 0, ref p) == 1, "Empty native input was not rejected");
                RunParams invalid = default; float sample = 0;
                Require(transcribe_run(handle, &sample, 1, ref invalid) == 14, "Zero run struct was not rejected");
            }
        }
        internal unsafe Result Run(float[] pcm, CancellationToken token, int cancelAfterPolls = -1)
        {
            lock (model.Gate)
            {
                ObjectDisposedException.ThrowIf(handle == 0, this);
                int polls = 0;
                Abort abort = _ => { int count = Interlocked.Increment(ref polls); return token.IsCancellationRequested || (cancelAfterPolls > 0 && count >= cancelAfterPolls); };
                var root = GCHandle.Alloc(abort);
                transcribe_set_abort_callback(handle, abort, 0);
                try
                {
                    transcribe_run_params_init(out var p); // Auto language; native timestamp defaults.
                    var watch = Stopwatch.StartNew();
                    int status;
                    fixed (float* samples = pcm) status = transcribe_run(handle, samples, pcm.Length, ref p);
                    watch.Stop();
                    string text = Text(transcribe_full_text(handle));
                    string language = Text(transcribe_detected_language(handle));
                    var rows = new List<Row>(); int invalid = 0; long previous = 0;
                    for (int i = 0; i < transcribe_n_segments(handle); i++)
                    {
                        transcribe_segment_init(out var row); Check(transcribe_get_segment(handle, i, ref row));
                        if (row.Start < 0 || row.End < row.Start || row.Start < previous || row.End > pcm.Length / 16 + 100) invalid++;
                        previous = row.Start; rows.Add(new(row.Start, row.End, Text(row.Text)));
                    }
                    int words = transcribe_n_words(handle), tokens = transcribe_n_tokens(handle);
                    previous = 0;
                    for (int i = 0; i < words; i++) { transcribe_word_init(out var row); Check(transcribe_get_word(handle, i, ref row)); _ = Text(row.Text); if (row.Start < previous || row.End < row.Start || row.End > pcm.Length / 16 + 100) invalid++; previous = row.Start; }
                    previous = 0;
                    for (int i = 0; i < tokens; i++) { transcribe_token_init(out var row); Check(transcribe_get_token(handle, i, ref row)); _ = Text(row.Text); if (row.Start < previous || row.End < row.Start || row.End > pcm.Length / 16 + 100) invalid++; previous = row.Start; }
                    transcribe_timings_init(out var timings); Check(transcribe_get_timings(handle, ref timings));
                    return new(status, transcribe_was_aborted(handle), transcribe_was_truncated(handle), text,
                        language, transcribe_returned_timestamp_kind(handle), rows, words, tokens, invalid,
                        Normalize(text) == Normalize(string.Join(" ", rows.Select(r => r.Text))), watch.Elapsed.TotalMilliseconds, timings, polls);
                }
                finally { transcribe_set_abort_callback(handle, null, 0); GC.KeepAlive(abort); root.Free(); }
            }
        }
        public void Dispose() { lock (model.Gate) { if (handle != 0) transcribe_session_free(handle); handle = 0; } }
    }
}
