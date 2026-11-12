# App-local Parakeet runtime

This directory owns the production native build. The application calls the C API
in-process through `State/ParakeetNative.cs` and `ParakeetRecognitionService`.
It never runs this build script, Python, CMake, or a recognizer CLI at runtime.

`pins.json` identifies transcribe.cpp 0.4.2, the exact vendored ggml tree, its four
downstream patch hashes, the C header, the single model, and license files. The
build rejects a dirty upstream checkout or a mismatch in any of those source
identifiers. It does not substitute an independently checked-out ggml base.

## Developer build

On macOS Apple Silicon, use .NET 10, Python 3.11+, Git, Xcode command-line tools,
and CMake 4.2.3. A local developer CMake installation can live in ignored artifacts:

```sh
python3 -m venv artifacts/parakeet-tools
artifacts/parakeet-tools/bin/pip install cmake==4.2.3
python3 native/parakeet/build.py --cmake "$PWD/artifacts/parakeet-tools/bin/cmake"
dotnet build WhisperDrop.sln -c Release -p:ContinuousIntegrationBuild=true
dotnet test tests/WhisperDrop.Tests/WhisperDrop.Tests.csproj -c Release
```

The script stages ARM64 shared libraries in `artifacts/parakeet/osx-arm64/`.
It builds dynamic CPU and Metal modules, embeds Metal shaders, disables system
BLAS and OpenMP, rewrites dependencies to `@loader_path`, removes build-tree
RPATHs, and applies local ad-hoc signatures. The script verifies and stages the
MIT runtime licenses and pinned CC BY 4.0 model license/NOTICE. It records file
hashes and the CMake configuration. Application build/publish targets copy the
stage under `parakeet/osx-arm64/`, including into a macOS bundle's Resources.
The build script downloads source code, and never downloads model weights.
`scripts/package_macos.py` creates and verifies the final ARM64 `.app` archive, including this stage and its native manifest, after extraction. Release packages remain unsigned and not notarized; local ad-hoc native signatures are not a Developer ID signature.

The runtime loader uses `AppContext.BaseDirectory`, verifies the native version,
commit, struct sizes/alignment, and actual loaded-image locations, and rejects
backend environment overrides. There is no system-library fallback. Backend
registration remains alive for the process lifetime. Model/session operations
are serialized; sessions and rooted abort callbacks live until inference returns,
and sessions are released before models. A requested device change reloads the
model on the next run. Device reporting comes from the initialized model device.

CPU forces the CPU backend; Auto tries Metal and falls back on backend-unavailable
status; forced GPU reports guidance when Metal is unavailable. Model inspection
is offline, and model loads are deferred until transcription. The download UI
explicitly starts the pinned artifact transfer and uses hash/size verification
before atomic installation. No WhisperDrop settings or model folders are read.

## Explicit real-model check

Ordinary tests do not download a model or require GPU hardware. The local check
is skipped unless `PARAKEETDROP_LOCAL_MODEL` names an existing, fully verified
copy of the exact model. Obtain it through the application's explicit model
download flow, or explicitly download the pinned URL in `pins.json` for a local
engineering check. No environment variable makes the test download anything.

```sh
PARAKEETDROP_LOCAL_MODEL='/absolute/path/parakeet-ultra-Q8_0.gguf' \
  dotnet test tests/WhisperDrop.Tests/WhisperDrop.Tests.csproj -c Release \
  --filter Category=LocalParakeet --logger 'console;verbosity=detailed'
```

The test exercises the production media preparation and recognition service with
`Fixtures/parakeet-speech.wav`, checks real CPU/Metal/Auto devices, repeat runs,
reloads, Unicode audio paths, cancellation with GC pressure, and successful
recovery. Its output records actual backends and elapsed times without transcript
text or user paths. The optional `PARAKEETDROP_LOCAL_AUDIO` selects another local
fixture. The opt-in collection runs without parallel tests because the existing
media service's startup cleanup shares a temporary directory across instances.

The committed speech fixture is synthetic, generated with macOS `say` / Samantha:

```sh
say -v Samantha -o tests/WhisperDrop.Tests/Fixtures/parakeet-speech.wav \
  --data-format=LEI16@16000 \
  'This is a local speech recognition test. The quick brown fox jumps over the lazy dog. We are checking the Apple Silicon application on both CPU and Metal.'
```

Content-free actual measurements are in
`evidence/2026-11-12-production-service.json`. That check ran from an unrelated
temporary working directory on the M4 Pro. It establishes the production service
path, rather than a final GUI/clean-install release, long-recording completeness,
or multilingual quality. Native truncation/repetition remains uninduced in the
preserved spike; production mapping of non-success, aborted and incomplete
results is covered by unit tests. Cancellation is observed at the pinned native
runtime's safe polling points; it does not imply interruption within an individual
encoder/decoder operation. Windows is deferred; Intel macOS and Rosetta are not
supported.
