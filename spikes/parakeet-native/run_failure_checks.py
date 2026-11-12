"""Explicit macOS ARM64 real-model failure/lifetime experiment; never routine CI.

All inputs are controlled public synthetic audio. Generated audio, profiler logs,
and build outputs belong under the ignored artifacts directory and are disposable.
"""
import argparse
import array
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import random
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent
PINS = json.loads((ROOT / "pins.json").read_text())
TEXT = ("One two three four five six seven eight nine ten. "
        "This is a controlled local native recognition experiment. "
        "The next sentence repeats the same short phrase. " + "Green apple " * 10)


def run(command, output, env=None):
    with output.open("w") as log:
        result = subprocess.run([str(x) for x in command], cwd=tempfile.gettempdir(),
                                stdout=log, stderr=subprocess.STDOUT, env=env)
    return result.returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-dir", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if platform.system() != "Darwin" or platform.machine() != "arm64":
        parser.error("This evidence runner requires real macOS ARM64 hardware")
    if any(os.environ.get(k) for k in ("GGML_BACKEND_PATH", "TRANSCRIBE_BACKENDS")):
        parser.error("Remove native backend overrides")
    model, native, out = args.model.resolve(), args.native_dir.resolve(), args.output.resolve()
    if model.stat().st_size != PINS["modelBytes"]:
        raise RuntimeError("Model byte length mismatch")
    with model.open("rb") as stream:
        model_hash = hashlib.file_digest(stream, "sha256").hexdigest()
    if model_hash != PINS["modelSha256"]:
        raise RuntimeError("Model digest mismatch")
    out.mkdir(parents=True, exist_ok=True)
    app = ROOT / "bin/Release/net10.0/ParakeetNativeSpike"
    subprocess.run(["dotnet", "build", str(ROOT / "ParakeetNativeSpike.csproj"), "-c", "Release"], check=True)
    subprocess.run(["say", "-v", "Samantha", "-r", "420", "-o", str(out / "dense.aiff"), TEXT], check=True)
    subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-y", "-i", str(out / "dense.aiff"),
                    "-ar", "16000", "-ac", "1", "-c:a", "pcm_f32le", "-f", "f32le", str(out / "dense.f32")], check=True)
    dense = (out / "dense.f32").read_bytes()
    (out / "repeat.f32").write_bytes(dense * max(1, round(180 / (len(dense) / 64000))))
    rng = random.Random(20261112)
    for name, sample in (("noise", lambda i: rng.uniform(-0.75, 0.75)),
                         ("tone", lambda i: 0.7 * math.sin(2 * math.pi * 440 * i / 16000))):
        (out / (name + ".f32")).write_bytes(array.array("f", (sample(i) for i in range(35 * 16000))).tobytes())
    evidence = {"runtimeCommit": PINS["transcribeCommit"], "modelRevision": PINS["modelRevision"],
                "modelBytes": model.stat().st_size, "modelSha256": model_hash,
                "hardware": {"architecture": platform.machine(), "system": platform.system(),
                             "cpu": subprocess.check_output(["sysctl", "-n", "machdep.cpu.brand_string"], text=True).strip()},
                "fixtures": {}, "checks": {}, "replacementGate": "closed"}
    for name in ("dense", "repeat", "noise", "tone"):
        path = out / (name + ".f32")
        evidence["fixtures"][name] = {"samples": path.stat().st_size // 4,
                                      "seconds": path.stat().st_size / 64000,
                                      "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
    for backend in ("cpu", "metal"):
        base = [app, "--native-dir", native, "--model", model, "--backend", backend]
        cases = [("dense-context-one", "dense", ["--context", "1"]),
                 ("repeated-speech", "repeat", []), ("noise", "noise", []), ("tone", "tone", []),
                 ("cancel-poll-ten-recover", "repeat", ["--cancel-after-polls", "10", "--cancel-first-only", "true", "--repeat", "3"]),
                 ("cancel-timer-recover", "repeat", ["--cancel-after-ms", "100", "--cancel-first-only", "true", "--repeat", "3"]),
                 ("profile-lifecycle", "dense", ["--repeat", "3", "--reload", "12"])]
        for name, fixture, extra in cases:
            key = backend + "-" + name
            report, log = out / (key + ".json"), out / (key + ".log")
            command = base + ["--audio", out / (fixture + ".f32"), "--report", report] + extra
            profiled = name == "profile-lifecycle"
            if profiled:
                command = ["/usr/bin/leaks", "--noContent", "--nostacks", "--atExit", "--"] + command
            code = run(command, log)
            check = {"exitCode": code, "fixture": fixture, "options": extra}
            if report.exists():
                raw = json.loads(report.read_text())
                check.update({k: raw[k] for k in ("Version", "Commit", "Reports", "DoubleDispose", "UseAfterDispose")})
            if profiled:
                found = re.search(r"(\d+) leaks for (\d+) total leaked bytes", log.read_text())
                check["profiler"] = {"tool": "/usr/bin/leaks", "atExit": True,
                                      "leaks": int(found[1]) if found else None,
                                      "leakedBytes": int(found[2]) if found else None,
                                      "scope": "managed P/Invoke process; native models and sessions deterministically disposed"}
            evidence["checks"][key] = check
            (out / "summary.json").write_text(json.dumps(evidence, indent=2) + "\n")
            print(key, "exit", code, flush=True)
    passed = all(c["exitCode"] == 0 and c.get("Reports") and
                 all(r["RunPassed"] for r in c["Reports"]) and
                 ("profiler" not in c or c["profiler"]["leaks"] == 0)
                 for c in evidence["checks"].values())
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
