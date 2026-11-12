using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WhisperDrop.State;

// Only the pinned 0.4.2 ABI at 275cd50d2c4104c204009a197ac091b9687668e5.
internal static class ParakeetNative
{
    const string Lib = "transcribe";
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchUserDirs = 0x00000400;
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;
    internal static string Text(nint value) => Marshal.PtrToStringUTF8(value) ?? "";
    private static readonly Lazy<nint> Library = new(Initialize);
    private static readonly Log QuietLog = (_, _, _) => { }; // Native messages may include paths or recognized text.
    internal static string DirectoryPath => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? Path.Combine(AppContext.BaseDirectory, "parakeet", "osx-arm64")
        : OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? Path.Combine(AppContext.BaseDirectory, "parakeet", "win-x64")
            : throw new RecognitionConfigurationException("Parakeet recognition requires macOS ARM64 or Windows x64.");
    internal static void Load() => _ = Library.Value;
    private static nint Initialize()
    {
        if (!(OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64) &&
            !(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64))
            throw new RecognitionConfigurationException("Parakeet recognition requires macOS ARM64 or Windows x64.");
        if (Environment.GetEnvironmentVariable("GGML_BACKEND_PATH") is not null || Environment.GetEnvironmentVariable("TRANSCRIBE_BACKENDS") is not null)
            throw new RecognitionConfigurationException("Remove GGML_BACKEND_PATH and TRANSCRIBE_BACKENDS overrides to use the bundled runtime.");
        var nativeLibrary = OperatingSystem.IsWindows() ? "transcribe.dll" : "libtranscribe.dylib";
        var libraryPath = Path.Combine(DirectoryPath, nativeLibrary);
        var library = OperatingSystem.IsWindows()
            ? LoadWindowsLibrary(DirectoryPath, libraryPath)
            : NativeLibrary.Load(libraryPath);
        // The registry owns backend modules for the process lifetime. Never unload or retry a failed initialization.
        NativeLibrary.SetDllImportResolver(typeof(ParakeetNative).Assembly, (name, _, _) => name == Lib ? library : 0);
        transcribe_log_set(QuietLog, 0);
        if (Text(transcribe_version()) != "0.4.2" || Text(transcribe_version_commit()) != "275cd50d")
            throw new RecognitionConfigurationException("The bundled Parakeet runtime does not match this application. Reinstall ParakeetDrop.");
        CheckAbi<LoadParams>(0); CheckAbi<SessionParams>(1); CheckAbi<RunParams>(2);
        CheckAbi<Segment>(6); CheckAbi<BackendParams>(15); CheckAbi<DeviceInfo>(13);
        transcribe_backend_init_params_init(out var init);
        var directory = Marshal.StringToCoTaskMemUTF8(DirectoryPath);
        try
        {
            init.Directory = directory;
            init.Allowed = OperatingSystem.IsWindows() ? 5u : 3u; // App-local CPU and the platform GPU backend only.
            Check(transcribe_init_backends_ex(ref init));
        }
        finally { Marshal.FreeCoTaskMem(directory); }
        AssertAppLocalLibraries();
        return library;
    }
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern uint _dyld_image_count();
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern nint _dyld_get_image_name(uint index);
    private static nint LoadWindowsLibrary(string directory, string libraryPath)
    {
        if (!SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs))
            throw new RecognitionConfigurationException("Windows could not configure safe app-local native library loading.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        if (AddDllDirectory(Path.GetFullPath(directory)) == 0)
            throw new RecognitionConfigurationException("The bundled Parakeet runtime directory is unavailable.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        var library = LoadLibraryEx(Path.GetFullPath(libraryPath), 0,
            LoadLibrarySearchDllLoadDir | LoadLibrarySearchUserDirs | LoadLibrarySearchDefaultDirs);
        if (library == 0)
            throw new RecognitionConfigurationException("The bundled Windows Parakeet runtime could not be loaded. Rebuild or reinstall ParakeetDrop.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        return library;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint AddDllDirectory(string newDirectory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string fileName, nint file, uint flags);

    internal static void AssertAppLocalLibraries()
    {
        var directory = Path.GetFullPath(DirectoryPath) + Path.DirectorySeparatorChar;
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                var name = module.ModuleName;
                if ((name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase) || name.StartsWith("transcribe", StringComparison.OrdinalIgnoreCase)) &&
                    !Path.GetFullPath(module.FileName).StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                    throw new RecognitionConfigurationException("A recognition library was loaded outside the application. Reinstall ParakeetDrop and remove native-library overrides.");
            }
            return;
        }

        for (uint i = 0; i < _dyld_image_count(); i++)
        {
            var path = Text(_dyld_get_image_name(i));
            var name = Path.GetFileName(path);
            if ((name.StartsWith("libggml", StringComparison.Ordinal) || name.StartsWith("libtranscribe", StringComparison.Ordinal)) &&
                !Path.GetFullPath(path).StartsWith(directory, StringComparison.Ordinal))
                throw new RecognitionConfigurationException("A recognition library was loaded outside the application. Reinstall ParakeetDrop and remove native-library overrides.");
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct AlignmentProbe<T> where T : struct { public byte Prefix; public T Value; }
    private static void CheckAbi<T>(int id) where T : struct
    {
        if ((nuint)Marshal.SizeOf<T>() != transcribe_abi_struct_size(id) ||
            (nuint)Marshal.OffsetOf<AlignmentProbe<T>>(nameof(AlignmentProbe<T>.Value)) != transcribe_abi_struct_align(id))
            throw new RecognitionConfigurationException("The bundled Parakeet ABI is incompatible. Reinstall ParakeetDrop.");
    }
    internal static void Check(int status)
    {
        if (status != 0) throw new RecognitionConfigurationException($"Parakeet native status {status}: {Text(transcribe_status_string(status))}. Try CPU or reinstall the application.");
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
