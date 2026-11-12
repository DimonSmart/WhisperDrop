"""Build and verify the sole current release artifact. Never publishes or tags."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PINS = json.loads((ROOT / "native/parakeet/pins.json").read_text())
NATIVE = {"libtranscribe.dylib", "libggml.0.dylib", "libggml-base.0.dylib",
          "libggml-cpu.so", "libggml-metal.so"}
NOTICES = {"transcribe-LICENSE", "ggml-LICENSE", "model-LICENSE", "model-NOTICE"}
MAGIC = {b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca"}


def run(*args):
    subprocess.run([str(a) for a in args], check=True)


def output(*args):
    return subprocess.check_output([str(a) for a in args], text=True)


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def files(root):
    return sorted(p for p in root.rglob("*") if p.is_file())


def native_files(app):
    result = []
    for path in files(app):
        with path.open("rb") as stream:
            if stream.read(4) in MAGIC:
                result.append(path)
    return result


def write_sums(root, paths, destination):
    destination.write_text("".join(f"{digest(p)}  {p.relative_to(root).as_posix()}\n" for p in sorted(paths)))


def verify_sums(root, manifest, expected):
    actual = {}
    for line in manifest.read_text().splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  (.+)", line)
        require(match is not None, f"Invalid checksum line in {manifest.name}")
        name = match[2]
        path = (root / name).resolve()
        require(path.is_relative_to(root.resolve()) and path.is_file(), f"Missing/unsafe checksum path: {name}")
        require(name not in actual and digest(path) == match[1], f"Duplicate or incorrect checksum: {name}")
        actual[name] = match[1]
    require(set(actual) == {p.relative_to(root).as_posix() for p in expected}, f"Incomplete checksum set: {manifest.name}")


def verify_app(app, version):
    resources = app / "Contents/Resources"
    plist = plistlib.loads((app / "Contents/Info.plist").read_bytes())
    require(plist["CFBundleShortVersionString"] == version, "Bundle version differs from tag")
    require(plist["CFBundleIdentifier"] == "io.dimonsmart.parakeetdrop", "Incorrect application identity")
    require(plist["CFBundleExecutable"] == "ParakeetDrop", "Incorrect executable name")
    executable = app / "Contents/MacOS/ParakeetDrop"
    require(executable.is_file() and os.access(executable, os.X_OK), "Missing executable app host")
    for path in (resources / "ParakeetDrop.dll", executable.parent / "ParakeetDrop.runtimeconfig.json",
                 executable.parent / "libcoreclr.dylib", executable.parent / "libhostfxr.dylib"):
        require(path.is_file(), f"Missing self-contained .NET file: {path.name}")
    require(plist["LSMinimumSystemVersion"] == "15.0", "Bundle and Cask minimum macOS version differ")
    deps = json.loads((executable.parent / "ParakeetDrop.deps.json").read_text())
    require(f"ParakeetDrop/{version}" in deps["libraries"], "Managed release version differs from tag")
    parakeet = resources / "parakeet/osx-arm64"
    for name in NATIVE | NOTICES | {"native-manifest.json"}:
        require((parakeet / name).is_file(), f"Missing Parakeet runtime asset: {name}")
    manifest = json.loads((parakeet / "native-manifest.json").read_text())
    require(manifest["runtimeCommit"] == PINS["transcribeCommit"] and manifest["ggmlVendoredTree"] == PINS["ggmlVendoredTree"], "Unpinned recognition runtime")
    require(set(manifest["files"]) == NATIVE | NOTICES, "Unexpected recognition runtime manifest")
    for name, info in manifest["files"].items():
        path = parakeet / name
        require(path.stat().st_size == info["bytes"] and digest(path) == info["sha256"], f"Recognition runtime hash mismatch: {name}")
    for name, pin in {"transcribe-LICENSE": "transcribeLicense", "ggml-LICENSE": "ggmlLicense", "model-LICENSE": "modelLicense", "model-NOTICE": "modelNotice"}.items():
        require(digest(parakeet / name) == PINS["licenseHashes"][pin], f"Incorrect notice: {name}")
    media = resources / "ffmpeg/osx-arm64"
    for name in ("libavcodec.dylib", "libavformat.dylib", "libavutil.dylib", "libswresample.dylib"):
        require((media / name).is_file(), f"Missing FFmpeg runtime asset: {name}")
    for name in ("THIRD-PARTY-NOTICES.md", "licenses/FFmpeg-LGPL-2.1.txt", "licenses/FFmpeg-package-NOTICES.md"):
        require((resources / name).is_file() and (resources / name).stat().st_size > 0, f"Missing notice: {name}")
    for path in app.rglob("*"):
        require(not path.is_symlink() or (path.exists() and path.resolve().is_relative_to(app.resolve())), "Missing/external symlink in app")
    for path in files(app):
        require(path.suffix.lower() != ".pdb", f"Forbidden PDB: {path.name}")
        require(path.name.lower() not in {"ffmpeg", "ffprobe", "ffmpeg.exe", "ffprobe.exe", "transcribe", "transcribe-cli", "transcribe-cli.exe", "whisper-cli", "whisper-cli.exe", "main", "main.exe"}, f"Forbidden CLI: {path.name}")
        require("whisper" not in path.name.lower() and path.suffix.lower() != ".gguf", f"Unexpected legacy runtime or embedded model: {path.name}")
    natives = native_files(app)
    require(executable in natives, "App host is not Mach-O")
    require(all((parakeet / name) in natives for name in NATIVE), "Non-native recognition library")
    dependencies = {}
    for path in natives:
        require(path == executable or path.suffix in {".dylib", ".so"}, f"Unexpected native executable/CLI: {path.name}")
        require("arm64" in output("/usr/bin/lipo", "-archs", path).split(), f"Native file has no ARM64 slice: {path.name}")
        commands = output("/usr/bin/otool", "-l", path).splitlines()
        rpaths = [commands[i + 2].strip().split(" (", 1)[0].removeprefix("path ")
                  for i, line in enumerate(commands) if line.strip() == "cmd LC_RPATH"]
        for rpath in rpaths:
            require(rpath.startswith(("@loader_path", "@executable_path")), f"External native RPATH: {rpath}")
            expanded = Path(rpath.replace("@loader_path", str(path.parent)).replace("@executable_path", str(executable.parent)))
            require(expanded.resolve().is_relative_to(app.resolve()), f"Native RPATH escapes app: {rpath}")
        # otool -L includes LC_ID_DYLIB, which is the library's own install name,
        # not a dependency. Check actual loaded dependencies, excluding that ID.
        install_ids = {line.strip() for line in output("/usr/bin/otool", "-D", path).splitlines()[1:]}
        deps = [line.strip().split(" (", 1)[0] for line in output("/usr/bin/otool", "-L", path).splitlines()[1:] if " (" in line]
        deps = [dep for dep in deps if dep not in install_ids]
        dependencies[path.relative_to(app).as_posix()] = deps
        for dep in deps:
            if dep.startswith(("/usr/lib/", "/System/Library/")):
                require("libggml" not in dep and "libtranscribe" not in dep, "System recognition dependency")
                continue
            def expand(value):
                return Path(value.replace("@loader_path", str(path.parent)).replace("@executable_path", str(executable.parent)))
            if dep.startswith(("@loader_path/", "@executable_path/")):
                candidates = [expand(dep)]
            elif dep.startswith("@rpath/"):
                # Include the app host's runpaths inherited by loaded images.
                host_commands = output("/usr/bin/otool", "-l", executable).splitlines()
                host_rpaths = [host_commands[i + 2].strip().split(" (", 1)[0].removeprefix("path ")
                               for i, line in enumerate(host_commands) if line.strip() == "cmd LC_RPATH"]
                candidates = [expand(r) / dep.removeprefix("@rpath/") for r in rpaths]
                candidates += [Path(r.replace("@loader_path", str(executable.parent)).replace("@executable_path", str(executable.parent))) / dep.removeprefix("@rpath/") for r in host_rpaths]
            else:
                raise RuntimeError(f"External/relative native dependency in {path.name}: {dep}")
            require(any(p.is_file() and p.resolve().is_relative_to(app.resolve()) for p in candidates), f"Missing app-local dependency in {path.name}: {dep}")
    verify_sums(app, resources / "NATIVE-SHA256SUMS.txt", natives)
    verify_sums(app, resources / "FFMPEG-SHA256SUMS.txt", files(media))
    return {"version": version, "architecture": "arm64", "nativeFiles": len(natives), "dependencies": dependencies}


def verify_archive(archive, version):
    require(archive.name == f"ParakeetDrop-v{version}-osx-arm64-app.zip", "Incorrect release asset name")
    with zipfile.ZipFile(archive) as bundle:
        require(bundle.testzip() is None, "Archive CRC verification failed")
        for item in bundle.infolist():
            name = Path(item.filename)
            require(not name.is_absolute() and ".." not in name.parts, "Unsafe archive path")
            require(name.parts[0] in {"ParakeetDrop.app", "__MACOSX"}, "Unexpected archive content")
            if name.parts[0] == "__MACOSX" and not item.is_dir():
                require(name.name.startswith("._"), "Unexpected resource-fork archive content")
    verify_sums(archive.parent, archive.parent / "SHA256SUMS.txt", [archive])
    with tempfile.TemporaryDirectory(prefix="parakeetdrop-package-") as temporary:
        run("/usr/bin/ditto", "-x", "-k", archive, temporary)
        app = Path(temporary) / "ParakeetDrop.app"
        result = verify_app(app, version)
        # Fresh loader process: no preloaded FFmpeg, no library search overrides,
        # no model/audio, and no dependency on the repository working directory.
        smoke = '''import ctypes, pathlib, sys
app = pathlib.Path(sys.argv[1])
resources = app / "Contents/Resources"
ffmpeg = ctypes.CDLL(str(resources / "ffmpeg/osx-arm64/libavformat.dylib"))
ffmpeg.avformat_version.restype = ctypes.c_uint
assert ffmpeg.avformat_version() >> 16 == 62
ctypes.CDLL(str(app / "Contents/MacOS/libicuuc.dylib"))
native = resources / "parakeet/osx-arm64"
engine = ctypes.CDLL(str(native / "libtranscribe.dylib"))
engine.transcribe_version.restype = ctypes.c_char_p
engine.transcribe_version_commit.restype = ctypes.c_char_p
assert engine.transcribe_version() == b"0.4.2"
assert engine.transcribe_version_commit() == b"275cd50d"
ctypes.CDLL(str(native / "libggml-cpu.so"))
ctypes.CDLL(str(native / "libggml-metal.so"))
'''
        environment = {k: v for k, v in os.environ.items() if not k.startswith(("DYLD_", "GGML_"))}
        subprocess.run([sys.executable, "-c", smoke, str(app)], check=True, cwd=temporary, env=environment)
        result["extractedNativeLoad"] = "passed (no model or inference)"
        return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/release-candidate")
    parser.add_argument("--verify-only", type=Path)
    args = parser.parse_args()
    require(platform.system() == "Darwin" and platform.machine() == "arm64", "Use a native macOS ARM64 environment")
    require(re.fullmatch(r"v\d+\.\d+\.\d+", args.tag), "Tag must be vMAJOR.MINOR.PATCH")
    version = args.tag[1:]
    args.output.mkdir(parents=True, exist_ok=True)
    if args.verify_only:
        archive = args.verify_only.resolve()
    else:
        publish = ROOT / "src/WhisperDrop/bin/Release/net10.0-desktop/osx-arm64/publish"
        if publish.exists():
            shutil.rmtree(publish)  # generated publish output only, prevents stale legacy runtime content
        run("dotnet", "publish", ROOT / "src/WhisperDrop/WhisperDrop.csproj", "-c", "Release", "-f", "net10.0-desktop", "-r", "osx-arm64", "--self-contained", "true",
            "-p:PackageFormat=app", "-p:PublishTrimmed=false", "-p:DebugType=None", "-p:DebugSymbols=false", "-p:CopyOutputSymbolsToPublishDirectory=false", "-p:UnoMacOSIncludeDebugSymbols=false", "-p:IncludeSourceRevisionInInformationalVersion=false",
            f"-p:Version={version}", f"-p:AssemblyVersion={version}.0", f"-p:FileVersion={version}.0", f"-p:InformationalVersion={version}", f"-p:ApplicationDisplayVersion={version}")
        app = publish / "ParakeetDrop.app"
        resources = app / "Contents/Resources"
        # Uno flattens the .NET ICU filenames, but ICU 77 retains its versioned
        # sibling load command. Repair only this known package relocation.
        icu = app / "Contents/MacOS/libicuuc.dylib"
        if "libicudata.77.dylib (" in output("/usr/bin/otool", "-L", icu):
            require((icu.parent / "libicudata.dylib").is_file(), "Missing bundled ICU data")
            run("/usr/bin/install_name_tool", "-change", "libicudata.77.dylib", "@loader_path/libicudata.dylib", icu)
            run("/usr/bin/codesign", "--force", "--sign", "-", icu)
        # The pinned FFmpeg NuGet includes its producer's absolute build paths.
        # Relocate only dependencies whose exact filename is bundled beside it;
        # every other non-system dependency is rejected by verify_app.
        media = resources / "ffmpeg/osx-arm64"
        for library in sorted(media.glob("*.dylib")):
            run("/usr/bin/install_name_tool", "-id", "@loader_path/" + library.name, library)
            for line in output("/usr/bin/otool", "-L", library).splitlines()[1:]:
                dep = line.strip().split(" (", 1)[0]
                if dep.startswith(("/usr/lib/", "/System/Library/", "@loader_path/")):
                    continue
                if (media / Path(dep).name).is_file():
                    run("/usr/bin/install_name_tool", "-change", dep, "@loader_path/" + Path(dep).name, library)
            run("/usr/bin/codesign", "--force", "--sign", "-", library)
        licenses = resources / "licenses"
        licenses.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / "packaging/licenses/FFmpeg-LGPL-2.1.txt", licenses)
        nuget = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
        shutil.copy2(nuget / "devenvy.ffmpeg.binaries.lgpl/8.0.1.4/THIRD_PARTY_NOTICES.md", licenses / "FFmpeg-package-NOTICES.md")
        write_sums(app, native_files(app), resources / "NATIVE-SHA256SUMS.txt")
        write_sums(app, files(resources / "ffmpeg/osx-arm64"), resources / "FFMPEG-SHA256SUMS.txt")
        verify_app(app, version)
        archive = args.output.resolve() / f"ParakeetDrop-{args.tag}-osx-arm64-app.zip"
        if archive.exists():
            archive.unlink()
        run("/usr/bin/ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", app, archive)
        write_sums(archive.parent, [archive], archive.parent / "SHA256SUMS.txt")
    evidence = verify_archive(archive, version)
    evidence["archive"] = archive.name
    evidence["sha256"] = digest(archive)
    (args.output / "package-verification.json").write_text(json.dumps(evidence, indent=2) + "\n")
    print(json.dumps({k: v for k, v in evidence.items() if k != "dependencies"}, indent=2))


if __name__ == "__main__":
    main()
