# Bundled media runtime

WhisperDrop media preparation uses FFmpeg only through in-process dynamic bindings.

Pinned pair:

- `FFmpeg.AutoGen.Bindings.DynamicallyLoaded`: 8.0.0.1
- `DevEnvy.FFmpeg.Binaries.LGPL`: 8.0.1.4
- expected FFmpeg 8.0 ABI: libavcodec 62, libavformat 62, libavutil 60, libswresample 6

The native package resolves shared libraries under `ffmpeg/<rid>/` relative to the application base directory. `FfmpegRuntime` deliberately resolves only this application-relative directory. It never searches `PATH`, Homebrew, `DYLD_LIBRARY_PATH`, `/usr/local`, `/opt/homebrew`, or the current working directory.

Required release RIDs are `win-x64`, `osx-arm64`, and `osx-x64`. Linux runtimes are used only so normal Linux CI can exercise media integration; Linux is not a release target.

The media pipeline demuxes the selected best audio stream, decodes only audio, resamples incrementally to 16 kHz mono signed PCM16, and writes a temporary WAV. Video decoders are never created. Temporary WAV files are deleted by `PreparedAudio` disposal, and stale files are best-effort cleaned from WhisperDrop's dedicated temp directory.

The binary NuGet package includes CLI tools for other use cases. WhisperDrop does not invoke them, and packaging removes `ffmpeg` / `ffprobe` executables.
