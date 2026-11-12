# ParakeetDrop macOS packaging

Build the pinned runtime first, as described in `native/parakeet/README.md`.
On a native Apple Silicon Mac with .NET 10 and Xcode command-line tools:

```sh
python3 native/parakeet/build.py --cmake "$PWD/artifacts/parakeet-tools/bin/cmake"
python3 scripts/package_macos.py --tag v0.1.0
```

The tag argument supplies the candidate version; this command never creates a
Git tag or publishes a release. Output is in ignored `artifacts/release-candidate/`:
`ParakeetDrop-v0.1.0-osx-arm64-app.zip`, `SHA256SUMS.txt`, and a local
`package-verification.json` evidence report. Only the ZIP and `SHA256SUMS.txt`
are uploaded by the tag-driven Release workflow. Repository/tap URLs remain
`DimonSmart/WhisperDrop`; the application and Cask are `ParakeetDrop` and
`parakeetdrop`. The checked-in Cask is bootstrap metadata until the first
ParakeetDrop release is published; the Homebrew workflow supplies its real hash.

The packager removes only the generated publish directory before publishing, to
avoid stale Whisper artifacts. It preserves the upstream Uno bundle layout:
managed assemblies and `parakeet/osx-arm64` / `ffmpeg/osx-arm64` under Resources,
and the executable plus .NET host/native runtime under MacOS. The current bundle
declares macOS 15.0; the Cask uses the same minimum and requires ARM64.

The pinned FFmpeg binaries contain build-machine install names; packaging
relocates bundled sibling references to `@loader_path`. Uno also flattens ICU 77
filenames, so packaging rewrites its one versioned data-library reference to the
actual app-local filename. Modified native libraries receive local ad-hoc
signatures. These are not Developer ID signing or notarization; releases remain
unsigned and not notarized under the existing distribution policy.

Verification reads the extracted final ZIP, checks version and identity, the
self-contained .NET host, the pinned Parakeet stage and notices, FFmpeg libraries
and notices, native-file and FFmpeg checksum manifests, ARM64 slices, actual
Mach-O dependency paths and RPATHs, and the outer archive checksum. CLI tools,
PDBs, embedded model weights, legacy Whisper files, and missing/external native
dependencies fail verification. A fresh process loads extracted FFmpeg, ICU,
Parakeet, CPU and Metal libraries without `DYLD_`/`GGML_` environment overrides.
This package-load check performs no inference and does not establish audio E2E,
GPU execution, GUI behavior, or a clean-machine installation.

To repeat verification without republishing:

```sh
python3 scripts/package_macos.py --tag v0.1.0 \
  --verify-only artifacts/release-candidate/ParakeetDrop-v0.1.0-osx-arm64-app.zip
```

Ordinary CI retains Windows/Linux build and unit tests. Windows is deferred,
unverified and not release-ready; Intel macOS and Rosetta are unsupported.

For explicitly controlled local GUI acceptance, launch the built executable with
`PARAKEETDROP_ACCEPTANCE_EVIDENCE` pointing to an existing ignored `artifacts/`
directory's JSONL file. The opt-in probe records launch/session counts, media
duration, elapsed time, actual backend, result counts and structural invariants,
preview opening, and clipboard equality after the native clipboard bridge settles.
It does not record source paths, filenames, audio, transcript text, or AI payloads.
No probe file is created when the variable is absent. On macOS the .NET peak
working-set counter may report zero; use native `vmmap -summary` physical-footprint
metrics and separate resident-memory sampling for the actual memory measurement.
The probe checks result structure, not semantic transcription accuracy.
