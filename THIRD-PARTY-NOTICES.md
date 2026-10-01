# Third-party notices

WhisperDrop redistributes third-party managed and native components. Package license files remain authoritative.

## FFmpeg media runtime

WhisperDrop uses FFmpeg only as bundled shared libraries loaded in-process.

- FFmpeg runtime line: 8.0.x ABI
- Bundled binary package: `DevEnvy.FFmpeg.Binaries.LGPL` 8.0.1.4
- Managed bindings: `FFmpeg.AutoGen.Bindings.DynamicallyLoaded` 8.0.0.1
- Required release RIDs: `win-x64`, `osx-arm64`, `osx-x64`
- Expected ABI: libavcodec 62, libavformat 62, libavutil 60, libswresample 6
- FFmpeg binary package license: LGPL-2.1-or-later
- Binary package source: https://www.nuget.org/packages/DevEnvy.FFmpeg.Binaries.LGPL/8.0.1.4
- Bindings package source: https://www.nuget.org/packages/FFmpeg.AutoGen.Bindings.DynamicallyLoaded/8.0.0.1
- FFmpeg project/source: https://ffmpeg.org/

The selected binary package documents a shared LGPL-oriented build with GPL and nonfree components disabled. WhisperDrop dynamically loads its own application-relative copy from `ffmpeg/<rid>/`; it does not search for a system or Homebrew FFmpeg installation.

On Windows, the bundled FFmpeg shared libraries also require `libstdc++-6.dll`, `libgcc_s_seh-1.dll`, and `libwinpthread-1.dll`. WhisperDrop redistributes these MinGW runtime files beside the FFmpeg libraries. `libstdc++` and `libgcc` are distributed under GPL-3.0-or-later with the GCC Runtime Library Exception 3.1; `libwinpthread` uses the mingw-w64 permissive license. Their source is the Git for Windows MinGW-w64 runtime, version 2.55.0.windows.3.

The NuGet package also carries `ffmpeg` and `ffprobe` command-line programs for CLI consumers. WhisperDrop never executes them. Build/release packaging removes them from WhisperDrop artifacts.

Release packaging produces `FFMPEG-SHA256SUMS.txt` from the actual native FFmpeg files included in each final package. This records byte-level checksums of redistributed media runtime artifacts.
