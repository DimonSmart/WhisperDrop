using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace WhisperDrop.Media;

public sealed record FfmpegRuntimeInfo(
    string LibrariesPath,
    string AvCodecVersion,
    string AvFormatVersion,
    string AvUtilVersion,
    string SwResampleVersion);

public interface IFfmpegRuntime
{
    FfmpegRuntimeInfo EnsureInitialized();
}

public sealed class FfmpegRuntime : IFfmpegRuntime
{
    private const int ExpectedAvCodecMajor = 62;
    private const int ExpectedAvFormatMajor = 62;
    private const int ExpectedAvUtilMajor = 60;
    private const int ExpectedSwResampleMajor = 6;

    private readonly object sync = new();
    private FfmpegRuntimeInfo? initialized;

    public FfmpegRuntimeInfo EnsureInitialized()
    {
        lock (sync)
        {
            if (initialized is not null)
                return initialized;

            var rid = GetRuntimeIdentifier();
            var librariesPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", rid);
            if (!HasRequiredLibraries(librariesPath))
            {
                throw MediaPreparationException.Create(
                    MediaPreparationErrorKind.RuntimeUnavailable,
                    new DllNotFoundException($"Bundled FFmpeg libraries were not found under '{librariesPath}'."));
            }

            try
            {
                DynamicallyLoadedBindings.LibrariesPath = librariesPath;
                DynamicallyLoadedBindings.Initialize();

                var avCodec = ffmpeg.avcodec_version();
                var avFormat = ffmpeg.avformat_version();
                var avUtil = ffmpeg.avutil_version();
                var swResample = ffmpeg.swresample_version();

                if (GetMajor(avCodec) != ExpectedAvCodecMajor ||
                    GetMajor(avFormat) != ExpectedAvFormatMajor ||
                    GetMajor(avUtil) != ExpectedAvUtilMajor ||
                    GetMajor(swResample) != ExpectedSwResampleMajor)
                {
                    throw MediaPreparationException.Create(
                        MediaPreparationErrorKind.RuntimeIncompatible,
                        new InvalidOperationException(
                            $"Unexpected FFmpeg ABI: avcodec={FormatVersion(avCodec)}, avformat={FormatVersion(avFormat)}, " +
                            $"avutil={FormatVersion(avUtil)}, swresample={FormatVersion(swResample)}."));
                }

                initialized = new FfmpegRuntimeInfo(
                    librariesPath,
                    FormatVersion(avCodec),
                    FormatVersion(avFormat),
                    FormatVersion(avUtil),
                    FormatVersion(swResample));
                return initialized;
            }
            catch (MediaPreparationException)
            {
                throw;
            }
            catch (DllNotFoundException exception)
            {
                throw MediaPreparationException.Create(MediaPreparationErrorKind.RuntimeUnavailable, exception);
            }
            catch (EntryPointNotFoundException exception)
            {
                throw MediaPreparationException.Create(MediaPreparationErrorKind.RuntimeIncompatible, exception);
            }
            catch (BadImageFormatException exception)
            {
                throw MediaPreparationException.Create(MediaPreparationErrorKind.RuntimeIncompatible, exception);
            }
            catch (NotSupportedException exception)
            {
                throw MediaPreparationException.Create(MediaPreparationErrorKind.RuntimeIncompatible, exception);
            }
        }
    }

    internal static string GetRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        if (OperatingSystem.IsWindows() && architecture == Architecture.X64)
            return "win-x64";
        if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
            return "osx-arm64";
        if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
            return "osx-x64";
        if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
            return "linux-x64";
        if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
            return "linux-arm64";

        throw MediaPreparationException.Create(
            MediaPreparationErrorKind.RuntimeUnavailable,
            new PlatformNotSupportedException(
                $"No bundled FFmpeg runtime is defined for {RuntimeInformation.OSDescription} {architecture}."));
    }

    private static bool HasRequiredLibraries(string path)
    {
        if (!Directory.Exists(path))
            return false;

        foreach (var library in new[] { "avcodec", "avformat", "avutil", "swresample" })
        {
            if (!HasLibrary(path, library))
                return false;
        }

        return true;
    }

    private static bool HasLibrary(string path, string library)
    {
        IEnumerable<string> candidates = OperatingSystem.IsWindows()
            ? Directory.EnumerateFiles(path, $"{library}-*.dll", SearchOption.TopDirectoryOnly)
            : OperatingSystem.IsMacOS()
                ? Directory.EnumerateFiles(path, $"lib{library}*.dylib", SearchOption.TopDirectoryOnly)
                : Directory.EnumerateFiles(path, $"lib{library}.so*", SearchOption.TopDirectoryOnly);

        using var enumerator = candidates.GetEnumerator();
        return enumerator.MoveNext();
    }

    private static int GetMajor(uint version) => checked((int)(version >> 16));

    private static string FormatVersion(uint version) =>
        $"{version >> 16}.{(version >> 8) & 0xff}.{version & 0xff}";
}
