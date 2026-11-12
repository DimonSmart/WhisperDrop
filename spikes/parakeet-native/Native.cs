using System.Reflection;
using System.Runtime.InteropServices;

// Only the pinned 0.4.2 ABI at 275cd50d2c4104c204009a197ac091b9687668e5.
internal static class Native
{
    const string Lib = "transcribe";
    internal static string Text(nint value) => Marshal.PtrToStringUTF8(value) ?? "";
    [DllImport("/usr/lib/libSystem.B.dylib")] static extern uint _dyld_image_count();
    [DllImport("/usr/lib/libSystem.B.dylib")] static extern nint _dyld_get_image_name(uint index);
    internal static string[] LoadedLibraries(string directory)
    {
        IEnumerable<string> paths;
        if (OperatingSystem.IsMacOS()) paths = Enumerable.Range(0, checked((int)_dyld_image_count())).Select(i => Text(_dyld_get_image_name((uint)i)));
        else paths = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().Select(m => m.FileName);
        var native = paths.Where(p => Path.GetFileName(p).Contains("ggml", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Contains("transcribe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (native.Any(p => !Path.GetFullPath(p).StartsWith(directory + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new InvalidOperationException("Recognition library loaded outside the selected stage");
        return native.Select(Path.GetFileName).OfType<string>().Order().ToArray();
    }
    internal static void Load(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Native directory must be absolute.");
        if (Environment.GetEnvironmentVariable("GGML_BACKEND_PATH") is not null ||
            Environment.GetEnvironmentVariable("TRANSCRIBE_BACKENDS") is not null)
            throw new InvalidOperationException("Remove backend environment overrides for this controlled check.");
        string file = OperatingSystem.IsWindows() ? "transcribe.dll" : "libtranscribe.dylib";
        var handle = NativeLibrary.Load(Path.Combine(directory, file));
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) => name == Lib ? handle : 0);
    }
    internal static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException($"Native status {status}: {Text(transcribe_status_string(status))}");
    }
    [StructLayout(LayoutKind.Sequential)] internal struct LoadParams { internal ulong Size; internal int Backend; internal nint Device; internal byte NoMmap; }
    [StructLayout(LayoutKind.Sequential)] internal struct SessionParams { internal ulong Size; internal int Threads, KvType, Context; }
    [StructLayout(LayoutKind.Sequential)] internal struct RunParams
    {
        internal ulong Size;
        internal int Task, Timestamps, Pnc, Itn, Diarize;
        internal nint Language, TargetLanguage;
        internal byte KeepSpecialTags;
        internal nint Family;
        internal int SpecDrafts;
        internal nint Vocabulary;
        internal int VocabularyCount;
        internal nint Prompt, Prefix;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Timings { internal ulong Size; public float Load, Mel, Encode, Decode; }
    [StructLayout(LayoutKind.Sequential)] internal struct Segment
    {
        internal ulong Size; internal long Start, End;
        internal int FirstWord, Words, FirstToken, Tokens;
        internal nint Text; internal int Speaker;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Word
    {
        internal ulong Size; internal long Start, End; internal int Segment, FirstToken, Tokens; internal nint Text;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Token
    {
        internal ulong Size; internal int Id; internal float Probability;
        internal long Start, End; internal int Segment, Word; internal nint Text;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct BackendParams { internal ulong Size; internal nint Directory; internal uint Allowed; }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfo { internal ulong Size; internal nint Name, Description, Kind, Id; internal ulong MemoryTotal, MemoryFree; internal int Type; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal delegate bool Abort(nint state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Log(int level, nint message, nint state);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern nint transcribe_version();
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern nint transcribe_version_commit();
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern nint transcribe_status_string(int status);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern nuint transcribe_abi_struct_size(int id);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern nuint transcribe_abi_struct_align(int id);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern void transcribe_log_set(Log callback, nint state);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern void transcribe_backend_init_params_init(out BackendParams p);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern int transcribe_init_backends_ex(ref BackendParams p);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern void transcribe_model_load_params_init(out LoadParams p);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern int transcribe_model_load_file([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref LoadParams p, out nint model);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern void transcribe_model_free(nint model);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern nint transcribe_model_backend(nint model);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern nint transcribe_model_device(nint model);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern void transcribe_device_info_init(out DeviceInfo p);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern int transcribe_device_get_info(nint device, ref DeviceInfo p);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern nint transcribe_model_arch_string(nint model);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern void transcribe_session_params_init(out SessionParams p);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern int transcribe_session_init(nint model, ref SessionParams p, out nint session);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern void transcribe_session_free(nint session);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern void transcribe_run_params_init(out RunParams p);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern unsafe int transcribe_run(nint session, float* pcm, int samples, ref RunParams p);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern void transcribe_set_abort_callback(nint session, Abort? callback, nint state);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool transcribe_was_aborted(nint session);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool transcribe_was_truncated(nint session);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern nint transcribe_full_text(nint session);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern nint transcribe_detected_language(nint session);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern int transcribe_returned_timestamp_kind(nint session);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern int transcribe_n_segments(nint session);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern int transcribe_n_words(nint session);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern int transcribe_n_tokens(nint session);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern void transcribe_segment_init(out Segment row);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern void transcribe_word_init(out Word row);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern void transcribe_token_init(out Token row);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern int transcribe_get_segment(nint session, int index, ref Segment row);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern int transcribe_get_word(nint session, int index, ref Word row);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern int transcribe_get_token(nint session, int index, ref Token row);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern void transcribe_timings_init(out Timings timings);
    [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] internal static extern int transcribe_get_timings(nint session, ref Timings timings);
}
