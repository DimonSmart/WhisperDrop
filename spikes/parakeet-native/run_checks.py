"""Explicit local-model checks. No downloads; never imported by the app or ordinary CI."""
import argparse
import json
import pathlib
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument("--native-dir", type=pathlib.Path, required=True)
parser.add_argument("--backend", choices=["cpu", "metal", "vulkan", "auto"], required=True)
parser.add_argument("--expect-backend", choices=["cpu", "metal", "vulkan"])
parser.add_argument("--model", type=pathlib.Path, required=True)
parser.add_argument("--short", type=pathlib.Path, required=True, help="16 kHz mono little-endian raw float32")
parser.add_argument("--long", type=pathlib.Path, help="Optional permitted recording, at least 64 minutes")
parser.add_argument("--output", type=pathlib.Path, required=True)
parser.add_argument("--private-results", action="store_true", help="Save transcript output locally; do not commit")
args = parser.parse_args()
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=True)
if args.long and args.long.stat().st_size / (4 * 16000) < 64 * 60:
    parser.error("Long recording must be at least 64 minutes")
subprocess.run(["dotnet", "build", str(ROOT / "ParakeetNativeSpike.csproj"), "-c", "Release"], check=True)
dll = ROOT / "bin/Release/net10.0/ParakeetNativeSpike.dll"
base = ["dotnet", str(dll), "--native-dir", str(args.native_dir.resolve()), "--model", str(args.model.resolve()),
        "--backend", args.backend, "--expect-backend", args.expect_backend or args.backend]
audio = str(args.short.resolve())
cancel_audio = str(args.long.resolve()) if args.long else audio
cases = [("abi", ["--mode", "abi"]),
         ("short", ["--audio", audio, "--repeat", "3", "--reload", "2"]),
         ("silence", ["--audio", "silence", "--repeat", "3"]),
         ("cancel-recover", ["--audio", cancel_audio, "--cancel-after-ms", "1500" if args.long else "0",
                             "--cancel-first-only", "true", "--repeat", "2"])]
if args.long:
    cases.append(("long", ["--audio", str(args.long.resolve()), "--repeat", "2"]))
if args.backend == "cpu":
    cases.append(("no-mmap", ["--audio", audio, "--no-mmap", "true"]))
results = {}
with tempfile.TemporaryDirectory(prefix="parakeet-spike-cwd-") as unrelated_cwd:
    for name, options in cases:
        command = [*base, *options, "--report", str(output / (name + ".json"))]
        if args.private_results and name != "abi":
            command += ["--private-result", str(output / ("private-" + name))]
        # time -l is supplemental macOS peak-RSS evidence; other OSes use native process tools separately.
        if __import__("platform").system() == "Darwin":
            command = ["/usr/bin/time", "-l", *command]
        with (output / (name + ".stdout")).open("w") as stdout, (output / (name + ".stderr")).open("w") as stderr:
            result = subprocess.run(command, cwd=unrelated_cwd, stdout=stdout, stderr=stderr)
        if name == "abi" and result.returncode == 0:
            (output / "abi.json").write_text((output / "abi.stdout").read_text())
        results[name] = result.returncode
        print(name, result.returncode, flush=True)
(output / "check-exits.json").write_text(json.dumps(results, indent=2) + "\n")
raise SystemExit(0 if all(code == 0 for code in results.values()) else 1)
