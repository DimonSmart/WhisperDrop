"""Isolated spike build/staging only. Requires developer CMake/compiler; never called by the app."""
import argparse
import hashlib
import json
import pathlib
import platform
import shutil
import subprocess
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent
PINS = json.loads((ROOT / "pins.json").read_text())


def run(*args):
    subprocess.run([str(a) for a in args], check=True)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


parser = argparse.ArgumentParser()
parser.add_argument("--rid", choices=["osx-arm64", "osx-x64", "win-x64"], required=True)
parser.add_argument("--gpu", action="store_true")
parser.add_argument("--download-model", action="store_true", help="Explicit opt-in; 740,363,168 bytes")
parser.add_argument("--cmake", default="cmake")
args = parser.parse_args()
if args.gpu and args.rid == "osx-x64":
    parser.error("The Intel spike is CPU-only")
if (args.rid.startswith("osx") and platform.system() != "Darwin") or (args.rid.startswith("win") and platform.system() != "Windows"):
    parser.error("Build on the target OS; cross-build is not hardware evidence")

artifacts = ROOT / "artifacts"
artifacts.mkdir(exist_ok=True)
source = artifacts / "transcribe.cpp"
if not source.exists():
    run("git", "clone", PINS["transcribeRepository"], source)
run("git", "-C", source, "checkout", "--detach", PINS["transcribeCommit"])
if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
    raise RuntimeError("Upstream source must be clean")
if digest(source / "include/transcribe.h") != PINS["headerSha256"]:
    raise RuntimeError("Header hash mismatch")
if PINS["ggmlBaseCommit"] not in (source / "ggml/UPSTREAM").read_text():
    raise RuntimeError("Vendored ggml base mismatch")

build = artifacts / ("build-" + args.rid + ("-gpu" if args.gpu else "-cpu"))
stage = artifacts / "native" / (args.rid + ("-gpu" if args.gpu else "-cpu"))
if stage.exists():
    raise RuntimeError("Use a new/empty stage directory to avoid stale modules")
flags = {
    "CMAKE_BUILD_TYPE": "Release", "TRANSCRIBE_BUILD_SHARED": "ON",
    "TRANSCRIBE_GGML_BACKEND_DL": "ON", "TRANSCRIBE_BUILD_TESTS": "OFF",
    "TRANSCRIBE_BUILD_EXAMPLES": "OFF", "TRANSCRIBE_BUILD_TOOLS": "OFF",
    "TRANSCRIBE_BUILD_REAL_MODEL_TESTS": "OFF", "TRANSCRIBE_INSTALL": "ON",
    "TRANSCRIBE_METAL": "ON" if args.gpu and args.rid == "osx-arm64" else "OFF",
    "TRANSCRIBE_VULKAN": "ON" if args.gpu and args.rid == "win-x64" else "OFF",
    "TRANSCRIBE_CUDA": "OFF", "TRANSCRIBE_HIP": "OFF", "TRANSCRIBE_USE_OPENMP": "OFF",
    "TRANSCRIBE_USE_SYSTEM_BLAS": "OFF", "TRANSCRIBE_ENABLE_VALIDATION_HOOKS": "OFF",
    "GGML_NATIVE": "OFF", "GGML_METAL_EMBED_LIBRARY": "ON",
}
if args.rid.startswith("osx"):
    flags.update(CMAKE_OSX_ARCHITECTURES="arm64" if args.rid == "osx-arm64" else "x86_64",
                 CMAKE_OSX_DEPLOYMENT_TARGET="14.0", CMAKE_INSTALL_RPATH="@loader_path")
else:
    flags.update(TRANSCRIBE_X86_CONSERVATIVE="ON")
command = [args.cmake, "-S", str(source), "-B", str(build)]
if args.rid.startswith("win"):
    command += ["-A", "x64"]
run(*command, *(f"-D{k}={v}" for k, v in flags.items()))
run(args.cmake, "--build", build, "--config", "Release", "-j", "10")
stage.mkdir(parents=True)
if args.rid.startswith("osx"):
    inputs = [(build / "src/libtranscribe.0.4.2.dylib", "libtranscribe.dylib"),
              (build / "ggml/src/libggml.0.26.0.dylib", "libggml.0.dylib"),
              (build / "ggml/src/libggml-base.0.26.0.dylib", "libggml-base.0.dylib"),
              (build / "bin/libggml-cpu.so", "libggml-cpu.so")]
    if args.gpu:
        inputs.append((build / "bin/libggml-metal.so", "libggml-metal.so"))
    for src, name in inputs:
        dst = stage / name
        shutil.copy2(src, dst)
        run("install_name_tool", "-id", "@loader_path/" + name, dst)
        for line in subprocess.check_output(["otool", "-L", str(dst)], text=True).splitlines()[1:]:
            dep = line.strip().split(" (")[0]
            if dep.startswith("@rpath/"):
                run("install_name_tool", "-change", dep, "@loader_path/" + dep.split("/")[-1], dst)
        commands = subprocess.check_output(["otool", "-l", str(dst)], text=True).splitlines()
        for i, line in enumerate(commands):
            if line.strip() == "cmd LC_RPATH":
                path = commands[i + 2].strip().split(" (")[0].removeprefix("path ")
                run("install_name_tool", "-delete_rpath", path, dst)
        run("codesign", "--force", "--sign", "-", dst)  # local ad-hoc test signature only
else:
    # Target runtime dependency closure still must be audited on Windows (not verified here).
    for dll in build.rglob("*.dll"):
        if (stage / dll.name).exists():
            raise RuntimeError("Duplicate DLL name: " + dll.name)
        shutil.copy2(dll, stage / dll.name)
manifest = {p.name: {"bytes": p.stat().st_size, "sha256": digest(p)} for p in stage.iterdir()}
(stage / "native-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
(build / "spike-config.json").write_text(json.dumps(flags, indent=2) + "\n")

if args.download_model:
    target = artifacts / PINS["modelFile"]
    if not target.exists():
        temporary = target.with_suffix(".gguf.download")
        with urllib.request.urlopen(PINS["modelUrl"]) as response, temporary.open("wb") as stream:
            shutil.copyfileobj(response, stream, length=1024 * 1024)
        if temporary.stat().st_size != PINS["modelBytes"] or digest(temporary) != PINS["modelSha256"]:
            raise RuntimeError("Downloaded model integrity check failed")
        temporary.replace(target)
    if target.stat().st_size != PINS["modelBytes"] or digest(target) != PINS["modelSha256"]:
        raise RuntimeError("Existing model integrity check failed")
print(stage)
