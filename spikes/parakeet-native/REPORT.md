# Parakeet Ultra native-runtime spike — 12 November 2026

**Replacement gate: CLOSED. Spike incomplete.** The available Apple Silicon
machine runs the pinned model through C# P/Invoke on Metal and CPU. Required
Windows and physical Intel environments were unavailable; those paths are not
verified. Whisper code, assets, application projects, tests, packaging and release
workflows remain intact. No release tag, replacement package, or application
runtime CLI dependency was created. Product Intent and Engineering were not edited.

Reproduction instructions are in [README.md](README.md); machine-readable,
content-free measurements are in
[evidence/2026-11-12-macos-arm64.json](evidence/2026-11-12-macos-arm64.json).
The later [quality/comparison evidence](evidence/2026-11-12-macos-arm64-quality.json)
adds measured runs and automatic boundary triage, with explicit reference and
Handy-equivalence limits.
The extra .NET executable has practical value as an isolated ABI/model experiment;
it is outside the application solution and needs no Uno workload.

## Exact pins and build configuration

| Item | Observed pin |
| --- | --- |
| transcribe.cpp | `275cd50d2c4104c204009a197ac091b9687668e5` |
| Header / native version | `0.4.2`; `transcribe_version_commit()` returned `275cd50d` |
| `include/transcribe.h` SHA-256 | `5c8267936370f40d911866df8617cb320dda6dba951e5ba89287645988105dbf` |
| ggml base | `d7cb574130e6f01ad25b3289685489200febcd74` |
| Actual ggml storage | Vendored tree, **no submodule** at this runtime pin; tree object `09259938849bc5f871148aa50d823ade83140df9` |
| Model revision | `a1539be1b849ea7463b88343e5277b373d0c4ea9` |
| Model file | `parakeet-ultra-Q8_0.gguf` |
| Downloaded file length | **740,363,168 bytes** |
| SHA-256 computed over downloaded bytes | `007a59761e9258779f189df396b50d12b83bb5b79e66df4b955c230d2f2a0a59` |

Sources: [pinned runtime](https://github.com/handy-computer/transcribe.cpp/tree/275cd50d2c4104c204009a197ac091b9687668e5),
[pinned header](https://github.com/handy-computer/transcribe.cpp/blob/275cd50d2c4104c204009a197ac091b9687668e5/include/transcribe.h),
[vendored ggml provenance](https://github.com/handy-computer/transcribe.cpp/blob/275cd50d2c4104c204009a197ac091b9687668e5/ggml/UPSTREAM),
[exact GGUF URL](https://huggingface.co/handy-computer/parakeet-ultra-gguf/resolve/a1539be1b849ea7463b88343e5277b373d0c4ea9/parakeet-ultra-Q8_0.gguf).
Hugging Face's file-tree metadata independently matched the measured length/hash.
`pins.json` records these values. No estimate from the model-card size was used.

ggml includes four downstream patches: threadpool oversubscription, backend
registration filtering, Metal mapped-buffer null handling, and x86 OS XSAVE
capability scoring. Exact patch names/hashes are in the evidence file; pinning the
ggml base alone does not identify these sources.

Both tested configurations use Release, shared libraries, dynamic backend modules
(`TRANSCRIBE_BUILD_SHARED=ON`, `TRANSCRIBE_GGML_BACKEND_DL=ON`),
`TRANSCRIBE_USE_OPENMP=OFF`, `TRANSCRIBE_USE_SYSTEM_BLAS=OFF`,
`GGML_NATIVE=OFF`, `GGML_METAL_EMBED_LIBRARY=ON`, arm64, deployment target 14.0.
Examples, tools, tests, real-model tests, validation hooks, CUDA, HIP and Vulkan
are OFF; install rules are ON. Metal is ON for the GPU build and OFF for the
separate CPU build. Every actual CMake option and native output size/hash is
recorded in the evidence file. `build_native.py` reproduced both stages.

The flat app-local GPU stage contains `libtranscribe.dylib`, `libggml.0.dylib`,
`libggml-base.0.dylib`, `libggml-cpu.so`, and `libggml-metal.so`. The CPU stage
omits the Metal module. The `.so` modules are Mach-O arm64 files. Dependency links
were changed to `@loader_path`, all build-tree RPATHs removed, and local ad-hoc
signatures applied. `otool` found only stage-local or Apple system dependencies;
loaded-image inspection verified all recognition libraries came from the chosen
stage. Runs used unrelated temporary working directories without backend override
environment variables. An Uno `.app` bundle and clean distribution installation
were **not verified** by this isolated executable.

## Actual environment and target matrix

MacBook Pro Mac16,7, Apple M4 Pro: 14 CPU cores (10 performance + 4 efficiency),
20 GPU cores, 48 GiB RAM. macOS 26.7.2 (25G332), Darwin 25.6.0 arm64, Metal 4.
.NET SDK 10.0.400, runtime 10.0.11; CMake 4.2.3; Apple clang 21.0.0
(`clang-2100.4.1.1`); Xcode 27.2 (27B52), macOS SDK 27.2.

| Required path | Evidence / result |
| --- | --- |
| macOS ARM64 Metal, short + long | **Inference passed on real hardware.** Loaded model device kind `metal`, description Apple M4 Pro; short/repeat/reload and 64-minute runs completed. Reference completeness limitations below remain. |
| macOS ARM64 CPU | **Inference passed.** Separate CPU-only build, actual kind `cpu`; no GPU module loaded. |
| Windows x64 Vulkan GPU | **NOT VERIFIED / blocked:** no compatible Windows machine exposed. No Windows compiler, SDK, DLL closure or inference claim. |
| Windows x64 without Vulkan | **NOT VERIFIED / blocked:** no Windows machine without the Vulkan runtime exposed. ARM64 CPU success does not establish this target. |
| macOS x64 Intel CPU, no GPU module | **NOT VERIFIED / blocked:** no physical Intel Mac exposed. No Rosetta/cross-build was counted as evidence. |

Important upstream discrepancy: `transcribe_model_backend()` returned **`MTL0`**
and **`CPU`**, rather than the header's example lower-case labels. Backend proof
therefore uses `transcribe_model_device()` plus initialized
`transcribe_device_get_info()` and its actual `kind`; reports preserve both values.
The requested unavailable Vulkan backend on this Mac returned status **8** and a
nonzero harness exit, without silently selecting Metal/CPU. Auto on the separate
CPU-only stage selected `cpu`. This is not Windows driver-failure evidence.

## ABI, audio and lifecycle observations

All nine crossing structures used by the harness matched native size/alignment;
an independently compiled C probe also matched **every field offset**. Sizes in
bytes: load 32, session 24, run 104, timings 24, segment 56, word 48, token 48,
backend-init 24, device-info 64; all alignment 8. Native init functions fill each
instance. C booleans use one-byte fields/return marshalling; Cdecl delegates and
UTF-8 string marshalling were exercised. A model symlink containing Cyrillic,
accented Latin and a space passed six Metal runs over two loads. Windows UTF-8
path handling remains not verified.

Negative checks observed native bad-struct-size **14**, missing model **3**, invalid
GGUF **4**, empty native input **1**, and zero run-struct **14**. Managed input
validation rejected empty, unaligned, NaN and out-of-range raw PCM. The normalized
array is pinned for `transcribe_run`; exact sample count is passed without an
additional full native PCM copy. No application VAD, overlapping chunking, or
second source decode was introduced.

Both CPU and Metal completed three sequential short runs per model across two
load/unload cycles, with stable text within each cycle. Session/model double
Dispose passed; managed calls after session disposal were rejected. Sessions are
released before models, including error paths. CPU `no_mmap=true` also completed,
exercising the true C-bool input path. Bounded repetitions showed no crash; they
do **not** prove unbounded leak freedom. ASan/UBSan were not run. The follow-up
below adds bounded macOS `leaks` profiler evidence.

The rooted abort callback reads a CancellationToken, remains alive throughout the
call, is cleared afterward, and is never replaced mid-run. Metal cancellation
requested at 1,500 ms returned **13**, `was_aborted=true`, after 1,603.966 ms; the
same session subsequently completed the full recording with the same text and
segments as a fresh session. Immediate CPU cancellation and CPU recovery passed;
separate 100 ms long-input cancellations returned after 270.554 ms (CPU) and
311.625 ms (Metal). Native mid-encoder cancellation was not verified. These tests
did not establish retention of nonempty partial text: observed cancelled outputs
were empty.

Five seconds of digital silence returned status 0, empty text and zero segments
on both backends, three times each. Native `OUTPUT_TRUNCATED` (18) and
`OUTPUT_REPETITION` (19) were **not induced**; the harness labels both Incomplete
and exits unsuccessfully rather than treating them as Completed. This mapping
is not evidence that the actual model has exercised those failure paths.

## Native failure and lifetime follow-up

The isolated `run_failure_checks.py` experiment rebuilt the same pinned runtime
on the M4 Pro and explicitly downloaded the same pinned model. Downloaded bytes
again measured **740,363,168** and SHA-256
`007a59761e9258779f189df396b50d12b83bb5b79e66df4b955c230d2f2a0a59`.
The 14 real-model checks and native output hashes are recorded in
[content-free follow-up evidence](evidence/2026-11-12-macos-arm64-failure-lifetime.json).
Both selections used the rebuilt Metal-capable stage; CPU initialization allowed
only the CPU backend and loaded only the CPU module. These are native ARM64
hardware results, with no cross-build evidence.

Controlled inputs were dense synthetic English speech (Samantha, 420 words/min),
that waveform repeated to approximately 181 seconds, 35 seconds of deterministic
uniform noise, and a 35-second 440 Hz tone. A separate dense-speech run requested
session `n_ctx=1`. All four cases on both CPU and Metal returned native status 0;
noise and tone returned empty text. Neither **OUTPUT_TRUNCATED (18)** nor
**OUTPUT_REPETITION (19)** was induced; both remain **NOT VERIFIED**. No status
was mocked or injected. These adversarial synthetic inputs are failure probes,
not transcript-quality evidence.

There is a specific source limitation at this pin: Parakeet does not call the
token repetition guard or set `was_truncated`; its run dispatcher returns the
family status directly. `docs/input-limits.md` excludes Parakeet from the greedy
families using repetition protection and says Parakeet ignores `n_ctx`. The TDT
decoder's pathological iteration-cap exit returns backend error, not status 18
or 19. Changing the model bytes, native source, or another family's status to
force these outcomes would not verify the pinned Parakeet path. Source hashes
are retained in the follow-up evidence. Harness status mapping remains only
binding behavior, not proof that this model can emit either incomplete signal.

Cancellation after the **tenth native abort callback poll** on the repeated
input returned status **13**, `was_aborted=true`, after **2,684.667 ms CPU** and
**1,386.043 ms Metal**. Each same session then completed two successful runs
with matching text. Timer cancellation requested after 100 ms returned after
**261.266 ms CPU** and **245.295 ms Metal**, again followed by two matching
successful recovery runs. These establish cancellation after native long-form
work has begun, at safe observation points. The pinned one-shot implementation
checks cancellation only before its inference helper; interruption inside an
individual encoder/decoder remains **NOT VERIFIED**. Cancelled outputs were
empty, so retention of nonempty partial text remains **NOT VERIFIED**.

`/usr/bin/leaks --noContent --nostacks --atExit` profiled the actual managed
P/Invoke executable separately on CPU and Metal. Each process performed **12
model load/session/dispose cycles, three recognition runs per cycle (36 total)**,
double disposal, and rejected managed use after disposal. Every run passed and
within-cycle texts matched. Each profiler reported **0 leaks / 0 leaked bytes**
after deterministic session/model disposal. This is bounded malloc reachability
evidence; it does not prove absence of reachable growth, unbounded leaks,
use-after-free, or GPU-driver leaks. ASan/UBSan were not run. Generated source
checkouts, model/audio, raw profiler logs, binaries, and build artifacts were
removed after preserving the content-free evidence.

## Long-form measurements and quality limits

The explicitly requested recording was locally available and used only for local
testing. Its source size was 81,937,025 bytes. FFmpeg 9.0.2 prepared it once in
1.89 seconds into 61,838,677 mono float samples: 247,354,708 bytes,
**3,864.9173125 seconds (64 min 24.917 s)**. This developer fixture preparation
used the local FFmpeg tool; application media code and packaged FFmpeg were not
changed. No private audio, transcript, filename, or source path is committed.

| Final Metal run | Wall inference | RTF (wall / audio) |
| --- | ---: | ---: |
| Fresh-process first full run | 38.127 s | 0.009865 |
| Same-session warm second run | 37.030 s | 0.009581 |
| After cancellation, same-session recovery | 135.162 s | 0.034972 |

Model-load wall time in the fresh full-run process was 39.671 ms; native reported
37.437 ms. These are first model loads in a fresh process with filesystem pages
already cached by hash verification, **not cold-disk measurements**. The separate
20-second fixture took 159.876–178.652 ms on Metal and 392.526–422.002 ms on CPU.

Each full result had 62,972 characters, 158 segments, 11,646 words and 22,223
tokens, timestamp kind 4 (token), and final timestamp 3,864,720 ms. Repeated and
recovered text/segments matched. All timestamps were nonnegative, source ordered,
`start <= end`, and within input duration tolerance; segment text matched full
text after whitespace normalization. Returned language was **empty** on all
observed fixtures; no fallback language or calibrated confidence was invented.

Peak RSS from `/usr/bin/time -l` for the final two-run long process was
614,809,600 bytes; peak footprint 630,589,264 bytes. The separate CPU short/reload
process peaked at 1,345,536,000 bytes RSS (581,846,696 footprint); mmap-backed
pages and footprint are different measurements. .NET `PeakWorkingSet64` returned
zero on this host and is retained as an unsupported measurement, not a real peak.

The evidence retains actual `transcribe_get_timings` values, but some were
inconsistent with wall time: first full-run encode 49,482.652 ms exceeded its
38,126.584 ms wall time; after cancellation encode was 2,960,539.5 ms for a
135,162.373 ms run. The cause of both this timing discrepancy and the slower
recovery run is **unresolved**. Wall Stopwatch measurements, not native timing
sums, determine the RTF values. No Handy speed comparison is claimed.

An automated boundary scan found no adjacent identical segments and one candidate
three-word suffix/prefix repeat between segments 132 and 133. The follow-up below locates it at **3,246.000 s** and tests an isolated excerpt;
whether it is natural source repetition or a model artifact remains **not verified**. Manual
reference excerpts, source boundary listening, omissions, proper names, numbers,
negations, pause coverage and WER/CER on this private recording remain **not
verified**; reaching the final timestamp alone does not establish completeness.
At this initial checkpoint Handy 0.10.0 equivalence was not established. The
follow-up below adds a real Handy run and common-input measurements, but retains
the strict matched-comparison qualification.

A separate 40.673-second synthetic en/es/ru fixture used the exact public texts
in `reference_texts.json`, local TTS voices, and seven-second pauses. It completed
across native long-form boundaries with text from all three scripts/languages.
The simple punctuation-stripped, lower-case scorer measured **13 word edits /
54 reference words = 24.074% WER**, and **35 / 301 characters = 11.628% CER**;
digits were not expanded. This small synthetic sample is limited reference
evidence, not real-world accuracy or 25-language validation. Long-result AI
chunking was not exercised because the application integration is outside this spike.

## Long-form quality and Handy follow-up

The authorized recording remained local and was prepared once again with FFmpeg
9.0.2 in **1.903 s**. Its sample count/duration and the pinned model's measured
**740,363,168 bytes / SHA-256 `007a59761e9258779f189df396b50d12b83bb5b79e66df4b955c230d2f2a0a59`**
were verified again. The runtime was rebuilt from the same exact pin and Metal
stage configuration. A fresh .NET 10 Release build passed with zero warnings and
errors. The same M4 Pro/macOS environment reported actual device kind `metal`,
raw backend `MTL0`. Both complete native calls returned status 0, 158 segments,
62,972 characters, empty returned language, zero timestamp errors and matching
segment/full text. Text and segments matched between runs. This is reproducible
execution evidence; it does not establish transcript completeness.

| Follow-up on original float32 preparation | Inference wall | RTF (seconds / audio seconds) |
| --- | ---: | ---: |
| First full run | 35.813 s | 0.009266 |
| Same-session second run | 35.819 s | 0.009268 |

Model load was **27.086 ms wall / 25.324 ms native** after model hashing had
already warmed filesystem pages. It was not a cold-disk measurement. Native
second-run mel/encode/decode timings were 246.564 / 29,204.707 / 5,991.922 ms;
this run did not reproduce the earlier large timing discrepancy, whose original
cause remains unresolved. `/usr/bin/time -l` reported **630,571,008 bytes peak
RSS** and **628,131,688 bytes peak footprint** for the two-run process.

### Boundary triage and reference limits

**Quality acceptance remains NOT VERIFIED.** No human source listening or human
reference annotation was performed. No suitable reference sidecar was available,
so private-recording **WER/CER are not calculated**. Another ASR result, including
Handy or an isolated crop, is not ground truth. Proper names, numbers, negations,
actual language switches, omission/repetition correctness and complete pause
coverage still need source/reference review. Neither the final timestamp nor
repeatable model text proves those properties.

Fifteen independently invoked Metal excerpts examined automatic consistency at
the beginning (0–65 s), end (3,799.917–3,864.917 s), the reported repeat, one
signal-selected pause, four candidate Spanish/English switches and seven other
native boundaries distributed through the recording. Each used a crop of the
already prepared PCM, with about 35 s of preceding and 40 s of following context
for interior boundaries. Every excerpt returned status 0 with valid timestamps
and segment/full-text correspondence. Four target passages were exact normalized
substrings of their excerpt outputs; others changed punctuation or word sequences.
A lowercase Unicode-word `difflib.SequenceMatcher(autojunk=False)` check retained
94.186–100% of target words in aligned matching blocks. **This is self-consistency,
not WER, accuracy, or evidence that omitted words were absent from the source.**
Exact windows, indices and measured counts are in the content-free evidence.

The three-word suffix/prefix candidate joins **one-based segments 132 and 133 at
3,246.000 s**. An independent **3,211–3,286 s** excerpt did not recreate the same
adjacent-segment suffix/prefix layout, but the candidate phrase occurred **twice
in both** the full-run neighboring text and the excerpt text. This result does
not distinguish spoken repetition from a model error. The target text also
changed elsewhere in that excerpt. No private words are retained in the report.

FFmpeg `silencedetect` at **−38 dB for at least 2 s** found one low-energy interval,
**3,446.287–3,448.892 s**; its neighboring excerpt also differed from the full run.
This threshold is only a signal probe, not speech VAD or a complete catalogue of
pauses. Language-switch locations came from a common-word heuristic over model
text, then excerpt inference; they are candidates, not verified language labels.
No application VAD/chunking was introduced. The overall native language remained
empty and was not converted to a fabricated language result.

### Handy investigation and supplementary timing

Installed **Handy 0.10.0** was actually invoked through its headless file CLI,
with hard-selected **device index 0**. Device enumeration identified **Apple M4
Pro / Metal** and the completed run reported **`MTL0`**. Its local model directory
was empty, but its shared Hugging Face cache contained the selected Ultra Q8_0:
repository revision `39eeb55181f0d354fd934f06e92fd8d5037fed8e`, with the **same
measured byte size and SHA-256 as this spike's pinned model**. Different repository
metadata did not imply different model bytes. No Handy model download, history
transcription, user-settings write, or AI correction was requested by the worker.
A copied isolated Handy executable could not launch (signal 9/exit 137); its
measurements were discarded. The installed executable completed successfully.

To control preparation, the already decoded PCM was converted to one 16 kHz mono
16-bit WAV, then reproduced for the C# harness as **`float32(i16 / 32767)`**, matching
Handy's release-tag `read_wav_samples`. The shared input had 61,838,677 samples;
the resulting float32 SHA-256 is recorded in evidence. This was not another decode
of the private source. Both programs ran sequentially, twice per loaded model.
The comparison harness also used `GGML_METAL_NO_RESIDENCY=1`, matching Handy's
release-tag default. Handy's ordinary file-import decoder (Symphonia/Rubato) was
bypassed by its headless WAV path; equivalence of normal app import was not tested.

| Supplementary common-input experiment | Load wall | First wall / RTF | Second wall / RTF |
| --- | ---: | ---: | ---: |
| C# harness, pinned native runtime | 18.492 ms | 36.957 s / 0.009562 | 36.825 s / 0.009528 |
| Installed Handy headless wrapper | 227 ms | 36.997 s / 0.009573 | 37.242 s / 0.009636 |

These are **not a fully verified matched comparison**. Handy's exact native
commit/build identity and internal worker PCM were not read back. Its verified
release-tag source is commit `82dfab9fda18c4bc24e1f6e56d36474d38eca0bc`; its
Cargo.lock resolves `transcribe-cpp-sys` 0.4.0, whose checksum-verified published
crate records native source commit `f1dcd15858c4bbba87ca97862b4e448b2cf15b8f` and
header version **0.4.0**, rather than this harness's **0.4.2** pin. The crate's
header and Parakeet model implementation hashes differ. This is source provenance
for the tagged build, not independent identification of the installed executable's
native build. Handy's language-detection/helper work and final text transforms were
also not disabled in the installed profile. The final raw texts differed; neither
is treated as a reference. No controlled attribution of speed differences or
25% acceptance conclusion is made from these wrapper timings.

Handy's JSON `rtf` is **audio/inference speedup** (104.466× for its fastest run),
not the requested RTF; the table recomputes inference/audio. Its CLI does not
expose `transcribe_get_timings`, so that measurement is **unsupported/unavailable**.
The pinned harness's second native mel/encode/decode values were 242.874 /
30,132.473 / 6,064.455 ms. Both load measurements had warm filesystem pages and
represent different setup scopes; neither establishes cold-disk performance.
`/usr/bin/time -l` reported Handy **1,535,082,496 bytes RSS / 761,677,216 bytes
footprint**, and harness **628,047,872 / 625,821,568 bytes**. Handy uses a separate
worker process; these rusage values do not establish simultaneous combined
parent/worker/GPU allocation peaks or a directly comparable total memory budget.

All generated model copies, checkouts, audio/transcripts, copied Handy files,
logs and build outputs were removed after preserving the content-free evidence.
The original recording, installed Handy, its shared cache and shipping application
were preserved. The replacement gate remains **CLOSED**.

## Licenses and remaining blockers

The exact source pin carries MIT licenses for transcribe.cpp and ggml. The pinned
[model LICENSE](https://huggingface.co/handy-computer/parakeet-ultra-gguf/blob/a1539be1b849ea7463b88343e5277b373d0c4ea9/LICENSE)
is CC BY 4.0; its [NOTICE](https://huggingface.co/handy-computer/parakeet-ultra-gguf/blob/a1539be1b849ea7463b88343e5277b373d0c4ea9/NOTICE)
attributes Handy's GGUF conversion of Moondream Parakeet Ultra commit `73175eb`
and describes quantization/repackaging without retraining. Exact license/notice
hashes are in evidence. No weights or native binaries are distributed by this
change; a future packaging task still needs the full dependency/license audit.

The hardware gate remains closed until real Windows Vulkan, Windows CPU without
Vulkan prerequisites, and Intel CPU paths have recorded passes. Native incomplete
signals, accurate diagnostics, reference completeness, broader lifetime/sanitizer
verification, matched
Handy conditions, and final app-local bundle/clean-install evidence also remain
open as described above. No permission to remove Whisper follows from these
Apple Silicon results.
