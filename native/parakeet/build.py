"""Developer build of pinned app-local Parakeet libraries; never invoked at runtime."""
import argparse
import hashlib
import json
import os
import pathlib
import platform
import shutil
import subprocess

ROOT = pathlib.Path(__file__).resolve().parent
PINS = json.loads((ROOT / "pins.json").read_text())


def run(*args):
    subprocess.run([str(a) for a in args], check=True)


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def resolve_cmake(value):
    found = shutil.which(value)
    if found:
        return found
    if platform.system() == "Windows" and value == "cmake":
        installer = pathlib.Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"))
        vswhere = installer / "Microsoft Visual Studio/Installer/vswhere.exe"
        if vswhere.is_file():
            installs = subprocess.check_output(
                [str(vswhere), "-latest", "-products", "*", "-requires",
                 "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
                text=True).splitlines()
            if installs:
                candidate = pathlib.Path(installs[0]) / "Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"
                if candidate.is_file():
                    return str(candidate)
    return value


parser = argparse.ArgumentParser()
parser.add_argument("--cmake", default="cmake")
parser.add_argument("--vulkan", action="store_true", help="Build the Windows Vulkan backend (requires the Vulkan SDK).")
args = parser.parse_args()
cmake = resolve_cmake(args.cmake)
system = platform.system()
machine = platform.machine().lower()
if not ((system == "Darwin" and machine in ("arm64", "aarch64")) or
        (system == "Windows" and machine in ("amd64", "x86_64"))):
    parser.error("Build on macOS Apple Silicon or Windows x64")
if args.vulkan and system != "Windows":
    parser.error("--vulkan is supported only for Windows x64")
artifacts = ROOT.parent.parent / "artifacts" / "parakeet"
artifacts.mkdir(parents=True, exist_ok=True)
source = artifacts / "source"
if not source.exists():
    # Preserve upstream bytes for pinned SHA-256 verification on Windows, where Git
    # otherwise defaults to autocrlf and silently rewrites checked-out headers.
    run("git", "clone", "-c", "core.autocrlf=false", PINS["transcribeRepository"], source)
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
rid = "osx-arm64" if system == "Darwin" else "win-x64"
vulkan = args.vulkan or (system == "Windows" and bool(os.environ.get("VULKAN_SDK")))
build = artifacts / f"build-{rid}"
stage = artifacts / rid
if stage.exists():
    shutil.rmtree(stage) # owned generated stage only; never a user model directory
flags = {
    "CMAKE_BUILD_TYPE": "Release", "TRANSCRIBE_BUILD_SHARED": "ON",
    "TRANSCRIBE_GGML_BACKEND_DL": "ON", "TRANSCRIBE_BUILD_TESTS": "OFF",
    "TRANSCRIBE_BUILD_EXAMPLES": "OFF", "TRANSCRIBE_BUILD_TOOLS": "OFF",
    "TRANSCRIBE_BUILD_REAL_MODEL_TESTS": "OFF", "TRANSCRIBE_INSTALL": "ON",
    "TRANSCRIBE_METAL": "ON" if system == "Darwin" else "OFF",
    "TRANSCRIBE_VULKAN": "ON" if vulkan else "OFF",
    "TRANSCRIBE_CUDA": "OFF", "TRANSCRIBE_HIP": "OFF", "TRANSCRIBE_USE_OPENMP": "OFF",
    "TRANSCRIBE_USE_SYSTEM_BLAS": "OFF", "TRANSCRIBE_ENABLE_VALIDATION_HOOKS": "OFF",
    "GGML_NATIVE": "OFF",
}
configure = [cmake, "-S", source, "-B", build]
if system == "Darwin":
    flags["GGML_METAL_EMBED_LIBRARY"] = "ON"
    flags["CMAKE_INSTALL_RPATH"] = "@loader_path"
    flags["CMAKE_OSX_ARCHITECTURES"] = "arm64"
    flags["CMAKE_OSX_DEPLOYMENT_TARGET"] = "14.0"
else:
    flags["CMAKE_MSVC_RUNTIME_LIBRARY"] = "MultiThreaded"
    configure.extend(["-A", "x64"])
run(*configure, *(f"-D{k}={v}" for k, v in flags.items()))
run(cmake, "--build", build, "--config", "Release", "-j", "10")
stage.mkdir(parents=True)
if system == "Darwin":
    inputs = [(build / "src/libtranscribe.0.4.2.dylib", "libtranscribe.dylib"),
              (build / "ggml/src/libggml.0.26.0.dylib", "libggml.0.dylib"),
              (build / "ggml/src/libggml-base.0.26.0.dylib", "libggml-base.0.dylib"),
              (build / "bin/libggml-cpu.so", "libggml-cpu.so"),
              (build / "bin/libggml-metal.so", "libggml-metal.so")]
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
    output = build / "bin" / "Release"
    required = ["transcribe.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll"]
    if vulkan:
        required.append("ggml-vulkan.dll")
    missing = [name for name in required if not (output / name).is_file()]
    if missing:
        raise RuntimeError("Windows native build is missing required libraries: " + ", ".join(missing))
    for src in output.glob("*.dll"):
        shutil.copy2(src, stage / src.name)
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
