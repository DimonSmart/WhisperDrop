# Isolated Parakeet native-runtime spike

This research executable is outside `WhisperDrop.sln` and has no reference to the
shipping application. It exists to investigate the pinned C ABI without installing
Uno workloads or changing Whisper. `REPORT.md` records observed results and the
closed replacement gate. It is not a new product specification.

Prerequisites for developers: .NET 10, Python 3, Git, CMake, and the target C/C++
toolchain. Vulkan builds additionally need the build-time Vulkan SDK. These are
spike/build tools; none is added to the application's runtime requirements.
On the measured Mac, CMake 4.2.3 was installed into an ignored virtual environment:

```sh
python3 -m venv spikes/parakeet-native/artifacts/tools
spikes/parakeet-native/artifacts/tools/bin/pip install cmake==4.2.3
python3 spikes/parakeet-native/build_native.py --rid osx-arm64 --gpu \
  --cmake "$PWD/spikes/parakeet-native/artifacts/tools/bin/cmake" --download-model
python3 spikes/parakeet-native/build_native.py --rid osx-arm64 \
  --cmake "$PWD/spikes/parakeet-native/artifacts/tools/bin/cmake"
dotnet build spikes/parakeet-native/ParakeetNativeSpike.csproj -c Release
```

The download is explicit; omit `--download-model` to build without network model
activity. The source checkout is detached at the exact commit in `pins.json`;
dirty upstream sources or a changed header fail the build. The script refuses a
nonempty stage to prevent stale GPU modules in a CPU check. The local
`.gitignore` keeps generated artifacts, model data, binaries, and build outputs
out of repository changes.

Use `--rid osx-x64` on an Intel Mac for its CPU build; use `--rid win-x64 --gpu`
and `--rid win-x64` on Windows for the Vulkan and CPU builds. These configurations
are supplied for continuation and have **not** been verified on those targets.
Windows DLL dependency closure, CRT deployment, actual SDK/compiler versions,
and clean-machine behavior must be recorded there.

The harness reads **raw little-endian 16 kHz mono float32**, not a container or
arbitrary byte stream. Preparing an authorized recording is a developer-only
operation; the application still uses its existing bundled media implementation.
For this experiment, local FFmpeg 9.0.2 produced the input once:

```sh
ffmpeg -nostdin -v error -i "$PERMITTED_AUDIO" -map 0:a:0 -ar 16000 -ac 1 \
  -c:a pcm_f32le -f f32le spikes/parakeet-native/artifacts/long.f32
python3 - <<'PY'
from pathlib import Path
p = Path('spikes/parakeet-native/artifacts')
with (p/'long.f32').open('rb') as stream:
    (p/'short.f32').write_bytes(stream.read(20 * 16000 * 4))
PY
python3 spikes/parakeet-native/run_checks.py \
  --native-dir spikes/parakeet-native/artifacts/native/osx-arm64-gpu \
  --backend metal --model spikes/parakeet-native/artifacts/parakeet-ultra-Q8_0.gguf \
  --short spikes/parakeet-native/artifacts/short.f32 \
  --long spikes/parakeet-native/artifacts/long.f32 \
  --output spikes/parakeet-native/artifacts/local-metal
```

For CPU, change the stage to `osx-arm64-cpu`, backend to `cpu`, and output folder.
The long input is optional; when supplied, the runner rejects inputs shorter than
64 minutes. It runs from an unrelated temporary working directory. The model is
hashed before load; no model download occurs. `GGML_BACKEND_PATH` and
`TRANSCRIBE_BACKENDS` overrides are rejected for controlled evidence.

The native directory is passed explicitly. macOS uses a flat directory containing
`libtranscribe.dylib`, `libggml.0.dylib`, `libggml-base.0.dylib`, and loadable
`libggml-cpu.so` plus `libggml-metal.so` for GPU builds. The `.so` suffix is the
actual macOS upstream module output. The script changes local dependency links
to `@loader_path`, removes build-tree RPATHs, and applies local ad-hoc signatures.
It does not build, sign, notarize, or publish an application bundle.

`run_checks.py` saves content-free metrics and expected native error checks in the
chosen output directory. `--private-results` additionally writes transcript text
for local research; use only an ignored local output folder, never commit those
files. Native log messages are suppressed because they may include paths/text.
The reports use actual model-device `kind`; raw `transcribe_model_backend` is also
recorded because this pin returns `MTL0` / `CPU` instead of its documented examples.

Independent C/managed ABI comparison on macOS:

```sh
xcrun clang -std=c11 -I spikes/parakeet-native/artifacts/transcribe.cpp/include \
  spikes/parakeet-native/abi_probe.c -o spikes/parakeet-native/artifacts/abi-probe
spikes/parakeet-native/artifacts/abi-probe > spikes/parakeet-native/artifacts/abi-c.json
python3 - <<'PY'
import json
from pathlib import Path
p = Path('spikes/parakeet-native/artifacts')
assert json.loads((p/'abi-c.json').read_text()) == json.loads((p/'local-metal/abi.json').read_text())['Abi']
PY
```

The synthetic language-reference check used built-in `say` voices Samantha,
Monica, and Milena at 160 words/minute with the exact strings in
`reference_texts.json`. Each AIFF was resampled using the command above; the three
raw outputs were concatenated in en/es/ru order, each followed by seven seconds
of zero samples. Run the harness with that mixed input and `--private-result`,
then score only that synthetic result with `score_reference.py`. This does not
establish real-world language accuracy or the quality of the private recording.

Sessions serialize calls through their model's lock. Native init functions fill
every crossing struct after ABI checks. The input array is pinned for the native
call, without a second full PCM buffer. A rooted Cdecl callback observes a
CancellationToken until `run` returns and is cleared afterward. Result strings
are copied before any further mutation/free. `Dispose` frees sessions before the
model and accepts repeated calls; use after disposal throws. Backend modules stay
loaded for the process lifetime, as required by the upstream global registry.

The macOS ARM64 failure/lifetime continuation uses only synthetic input and the
same pinned model/stage. Run it explicitly after the build/download above:

```sh
python3 spikes/parakeet-native/run_failure_checks.py \
  --native-dir spikes/parakeet-native/artifacts/native/osx-arm64-gpu \
  --model spikes/parakeet-native/artifacts/parakeet-ultra-Q8_0.gguf \
  --output spikes/parakeet-native/artifacts/failure-checks
```

This additionally requires macOS `say` (Samantha), developer FFmpeg, and
`/usr/bin/leaks`. It verifies the model size/hash, creates dense/repeated speech,
noise and a tone, requests `n_ctx=1`, cancels during long-form work using a timer
and the tenth native callback poll, and checks same-session recovery. The harness
accepts `--context` and `--cancel-after-polls` solely for this controlled spike.
The first successful run is the comparison baseline after cancellation.
Each backend's profiler process performs 12 load/session/dispose cycles with
three runs each. CPU uses the CPU-only allowed-backend mask in the Metal-capable
stage. `summary.json` contains content-free measurements; raw profiler logs and
synthetic audio stay generated/ignored and should be removed after extracting
evidence. A passing experiment does not imply either incomplete status was
observed: consult the report's explicit limitations.
