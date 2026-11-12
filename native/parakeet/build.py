"""Developer build of pinned app-local Parakeet libraries; never invoked at runtime."""
import argparse
import hashlib
import json
import pathlib
import platform
import shutil
import subprocess

ROOT = pathlib.Path(__file__).resolve().parent
PINS = json.loads((ROOT / "pins.json").read_text())


def run(*args):
    subprocess.run([str(a) for a in args], check=True)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


parser = argparse.ArgumentParser()
parser.add_argument("--cmake", default="cmake")
args = parser.parse_args()
if platform.system() != "Darwin" or platform.machine() != "arm64":
    parser.error("Build on macOS Apple Silicon")
artifacts = ROOT.parent.parent / "artifacts" / "parakeet"
artifacts.mkdir(parents=True, exist_ok=True)
source = artifacts / "source"
if not source.exists():
    run("git", "clone", PINS["transcribeRepository"], source)
run("git", "-C", source, "checkout", "--detach", PINS["transcribeCommit"])
if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
    raise RuntimeError("Upstream source must be clean")
if digest(source / "include/transcribe.h") != PINS["headerSha256"]:
    raise RuntimeError("Header hash mismatch")
if subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD:ggml"], text=True).strip() != PINS["ggmlVendoredTree"]:
    raise RuntimeError("Vendored ggml tree mismatch")
if PINS["ggmlBaseCommit"] not in (source / "ggml/UPSTREAM").read_text():
    raise RuntimeError("Vendored ggml base mismatch")
for name, expected in PINS["ggmlPatchHashes"].items():
    if digest(source / "patches/ggml" / name) != expected:
        raise RuntimeError("Downstream ggml patch mismatch: " + name)
build = artifacts / "build-osx-arm64"
stage = artifacts / "osx-arm64"
if stage.exists():
    shutil.rmtree(stage) # owned generated stage only; never a user model directory
flags = {
    "CMAKE_BUILD_TYPE": "Release", "TRANSCRIBE_BUILD_SHARED": "ON",
    "TRANSCRIBE_GGML_BACKEND_DL": "ON", "TRANSCRIBE_BUILD_TESTS": "OFF",
    "TRANSCRIBE_BUILD_EXAMPLES": "OFF", "TRANSCRIBE_BUILD_TOOLS": "OFF",
    "TRANSCRIBE_BUILD_REAL_MODEL_TESTS": "OFF", "TRANSCRIBE_INSTALL": "ON",
    "TRANSCRIBE_METAL": "ON",
    "TRANSCRIBE_VULKAN": "OFF",
    "TRANSCRIBE_CUDA": "OFF", "TRANSCRIBE_HIP": "OFF", "TRANSCRIBE_USE_OPENMP": "OFF",
    "TRANSCRIBE_USE_SYSTEM_BLAS": "OFF", "TRANSCRIBE_ENABLE_VALIDATION_HOOKS": "OFF",
    "GGML_NATIVE": "OFF", "GGML_METAL_EMBED_LIBRARY": "ON",
}
flags.update(CMAKE_OSX_ARCHITECTURES="arm64", CMAKE_OSX_DEPLOYMENT_TARGET="14.0", CMAKE_INSTALL_RPATH="@loader_path")
run(args.cmake, "-S", source, "-B", build, *(f"-D{k}={v}" for k, v in flags.items()))
run(args.cmake, "--build", build, "--config", "Release", "-j", "10")
stage.mkdir(parents=True)
inputs = [(build / "src/libtranscribe.0.4.2.dylib", "libtranscribe.dylib"),
          (build / "ggml/src/libggml.0.26.0.dylib", "libggml.0.dylib"),
          (build / "ggml/src/libggml-base.0.26.0.dylib", "libggml-base.0.dylib"),
          (build / "bin/libggml-cpu.so", "libggml-cpu.so")]
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
licenses = [
    (source / "LICENSE", "transcribe-LICENSE", "transcribeLicense"),
    (source / "ggml/LICENSE", "ggml-LICENSE", "ggmlLicense"),
    (ROOT / "model-LICENSE", "model-LICENSE", "modelLicense"),
    (ROOT / "model-NOTICE", "model-NOTICE", "modelNotice"),
]
for license_path, name, pin in licenses:
    if digest(license_path) != PINS["licenseHashes"][pin]:
        raise RuntimeError("License/notice hash mismatch: " + name)
    shutil.copy2(license_path, stage / name)
manifest = {
    "runtimeCommit": PINS["transcribeCommit"],
    "ggmlVendoredTree": PINS["ggmlVendoredTree"],
    "files": {p.name: {"bytes": p.stat().st_size, "sha256": digest(p)} for p in stage.iterdir()},
}
(stage / "native-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
(build / "build-config.json").write_text(json.dumps(flags, indent=2) + "\n")
print(stage)
