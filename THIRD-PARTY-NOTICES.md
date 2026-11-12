# Third-party notices

ParakeetDrop redistributes third-party managed and native components. Package license files remain authoritative.

## FFmpeg media runtime

ParakeetDrop uses FFmpeg only as bundled shared libraries loaded in-process.

- FFmpeg runtime line: 8.0.x ABI
- Bundled binary package: `DevEnvy.FFmpeg.Binaries.LGPL` 8.0.1.4
- Managed bindings: `FFmpeg.AutoGen.Bindings.DynamicallyLoaded` 8.0.0.1
- Current release RID: `osx-arm64`; Windows x64 is deferred and unverified; Intel macOS is unsupported.
- Expected ABI: libavcodec 62, libavformat 62, libavutil 60, libswresample 6
- FFmpeg binary package license: LGPL-2.1-or-later
- Binary package source: https://www.nuget.org/packages/DevEnvy.FFmpeg.Binaries.LGPL/8.0.1.4
- Bindings package source: https://www.nuget.org/packages/FFmpeg.AutoGen.Bindings.DynamicallyLoaded/8.0.0.1
- FFmpeg project/source: https://ffmpeg.org/

The selected binary package documents a shared LGPL-oriented build with GPL and nonfree components disabled. ParakeetDrop dynamically loads its own application-relative copy from `ffmpeg/<rid>/`; it does not search for a system or Homebrew FFmpeg installation.

For the preserved Windows development code, FFmpeg shared libraries also require `libstdc++-6.dll`, `libgcc_s_seh-1.dll`, and `libwinpthread-1.dll`. ParakeetDrop redistributes these MinGW runtime files beside the FFmpeg libraries. `libstdc++` and `libgcc` are distributed under GPL-3.0-or-later with the GCC Runtime Library Exception 3.1; `libwinpthread` uses the mingw-w64 permissive license. Their source is the Git for Windows MinGW-w64 runtime, version 2.55.0.windows.3.

The NuGet package also carries `ffmpeg` and `ffprobe` command-line programs for CLI consumers. ParakeetDrop never executes them. Build/release packaging removes them from ParakeetDrop artifacts.

Release packaging produces `FFMPEG-SHA256SUMS.txt` and `NATIVE-SHA256SUMS.txt` from the redistributed files, then verifies them again after extracting the final archive. The app includes the full LGPL 2.1 license as `licenses/FFmpeg-LGPL-2.1.txt` and the exact binary-package notice as `licenses/FFmpeg-package-NOTICES.md`. FFmpeg 8.0.1 source is available at https://github.com/FFmpeg/FFmpeg/tree/n8.0.1 and https://ffmpeg.org/releases/ffmpeg-8.0.1.tar.xz. The binary-package source commit is `041eb84a7562e84cc68bcb2937c9cf4bdb88423d`; its NuGet metadata and notices identify the LGPL shared build (`--disable-gpl`, `--disable-nonfree`, `--disable-autodetect`). This software is based in part on the work of the Independent JPEG Group; ParakeetDrop makes no changes to the upstream FFmpeg source. macOS packaging rewrites only the binary install names and sibling load paths to `@loader_path`, replacing the binary producer's absolute build-machine paths, and reapplies local ad-hoc signatures. Users may replace the dynamically loaded FFmpeg libraries with ABI-compatible builds.

## Parakeet recognition runtime and model

ParakeetDrop uses the in-process transcribe.cpp C API at commit `275cd50d2c4104c204009a197ac091b9687668e5` (0.4.2), under the MIT license. Its vendored ggml tree is `09259938849bc5f871148aa50d823ade83140df9`, based on `d7cb574130e6f01ad25b3289685489200febcd74` with the downstream patches pinned in `native/parakeet/pins.json`. ggml is also MIT licensed. The complete runtime license texts accompany the app-local native libraries as `transcribe-LICENSE` and `ggml-LICENSE`.

The explicitly downloaded Parakeet Ultra Q8_0 model is the handy-computer GGUF conversion of Moondream Parakeet Ultra, at model revision `a1539be1b849ea7463b88343e5277b373d0c4ea9`. It is licensed under CC BY 4.0. The model is downloaded separately and is not bundled with the initial application. Its exact upstream `LICENSE` and conversion `NOTICE` accompany the native files as `model-LICENSE` and `model-NOTICE`; the same documents are preserved under `native/parakeet/` in the source tree. The GGUF conversion repackages and quantizes the original weights without retraining or fine-tuning.

Runtime source: [transcribe.cpp pinned source](https://github.com/handy-computer/transcribe.cpp/tree/275cd50d2c4104c204009a197ac091b9687668e5). Model source and attribution: [pinned Parakeet Ultra GGUF revision](https://huggingface.co/handy-computer/parakeet-ultra-gguf/tree/a1539be1b849ea7463b88343e5277b373d0c4ea9).
