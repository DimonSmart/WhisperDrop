# Bundled media runtime

ParakeetDrop media preparation uses FFmpeg only through in-process dynamic bindings.

Pinned pair:

- `FFmpeg.AutoGen.Bindings.DynamicallyLoaded`: 8.0.0.1
- `DevEnvy.FFmpeg.Binaries.LGPL`: 8.0.1.4
- expected FFmpeg 8.0 ABI: libavcodec 62, libavformat 62, libavutil 60, libswresample 6

The native package resolves shared libraries under `ffmpeg/<rid>/` relative to the application base directory. `FfmpegRuntime` deliberately resolves only this application-relative directory. It never searches `PATH`, Homebrew, `DYLD_LIBRARY_PATH`, `/usr/local`, `/opt/homebrew`, or the current working directory.

The only current release RID is `osx-arm64`. Windows x64 is planned for later and is not verified or release-ready. Windows and Linux media assets remain available for ordinary development CI; Linux is not a release target. Intel macOS and Rosetta are unsupported.

The media pipeline demuxes the selected best audio stream, decodes only audio, resamples incrementally to 16 kHz mono signed PCM16, and writes a temporary WAV. Video decoders are never created. Temporary WAV files are deleted by `PreparedAudio` disposal, and stale files are best-effort cleaned from ParakeetDrop's dedicated temp directory.

The binary NuGet package includes CLI tools for other use cases. ParakeetDrop does not invoke them, and packaging removes `ffmpeg` / `ffprobe` executables.

Final `.app` packages place these libraries in `Contents/Resources/ffmpeg/osx-arm64/`. `scripts/package_macos.py` validates the extracted final ZIP, rejects external native dependencies and CLI tools, and verifies `FFMPEG-SHA256SUMS.txt` plus `NATIVE-SHA256SUMS.txt`. The LGPL license and upstream binary-package notices are included under `Contents/Resources/licenses/`.
