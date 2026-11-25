"""Build a native UMD desktop ZIP, or package a release built from this checkout."""
import argparse
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import zipfile

DESKTOP = Path(__file__).resolve().parent
WORKSPACE = DESKTOP.parent
CONFIG = json.loads((DESKTOP / "src-tauri/tauri.conf.json").read_text(encoding="utf-8"))
WINDOWS = sys.platform == "win32"
EXECUTABLE = "umd-desktop.exe" if WINDOWS else "umd-desktop"


def binary_architecture(binary):
    with binary.open("rb") as source:
        header = source.read(64)
        if WINDOWS and header[:2] == b"MZ":
            source.seek(struct.unpack_from("<I", header, 60)[0])
            if source.read(4) == b"PE\0\0":
                machine = int.from_bytes(source.read(2), "little")
                return {0x8664: "x64", 0xAA64: "arm64", 0x14C: "x86"}[machine]
        elif not WINDOWS and header[:4] == b"\x7fELF" and header[4:6] == b"\x02\x01":
            return {62: "x64", 183: "arm64"}[struct.unpack_from("<H", header, 18)[0]]
    raise ValueError(f"Unexpected executable format in {binary}")


def build():
    npm = "npm.cmd" if WINDOWS else "npm"
    subprocess.run([npm, "run", "tauri", "--", "build", "--no-bundle", "--", "--locked"], cwd=DESKTOP, check=True)
    metadata = subprocess.check_output(
        ["cargo", "metadata", "--no-deps", "--format-version", "1"], cwd=WORKSPACE, text=True
    )
    return Path(json.loads(metadata)["target_directory"]) / "release" / EXECUTABLE


def add_tree(archive, root, prefix):
    for file in sorted(root.rglob("*")):
        if file.is_file():
            archive.write(file, (prefix / file.relative_to(root)).as_posix())


def package(binary, output, linux_vlc):
    arch = binary_architecture(binary)
    runtime = f"{'win' if WINDOWS else 'linux'}-{arch}"
    version = CONFIG["version"]
    output.mkdir(parents=True, exist_ok=True)
    final = output / f"UMD-{version}-{runtime}.zip"
    with tempfile.TemporaryDirectory(dir=output) as temp:
        staging = Path(temp)
        archive_path = staging / final.name
        with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.write(binary, EXECUTABLE)
            identity = {"product": CONFIG["identifier"], "version": version, "runtimeIdentifier": runtime}
            archive.writestr("umd-package.json", json.dumps(identity))
            if WINDOWS:
                vlc = binary.parent / "vlc" / arch
                for part in ("libvlc.dll", "libvlccore.dll", "plugins"):
                    if not (vlc / part).exists():
                        raise FileNotFoundError(vlc / part)
                add_tree(archive, vlc, Path("vlc") / arch)
                readme = "Extraé la carpeta completa y abrí umd-desktop.exe. Requiere Microsoft Edge WebView2 Runtime."
            else:
                if linux_vlc is None:
                    linux_vlc = staging / "vlc-runtime"
                    subprocess.run([sys.executable, str(WORKSPACE.parent / "eng/linux/bundle-vlc.py"), str(linux_vlc)], check=True)
                for part in ("libvlc.so", "libvlc/libvlccore.so.9", "libvlc/plugins", "licenses/libvlc/packages.tsv"):
                    if not (linux_vlc / part).exists():
                        raise FileNotFoundError(linux_vlc / part)
                archive.write(linux_vlc / "libvlc.so", "libvlc.so")
                for tree in ("libvlc", "licenses"):
                    add_tree(archive, linux_vlc / tree, Path(tree))
                launcher = zipfile.ZipInfo("UMD")
                launcher.create_system = 3
                launcher.external_attr = 0o100755 << 16
                archive.writestr(launcher, '#!/bin/sh\nset -eu\napp=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)\nexport VLC_PLUGIN_PATH="$app/libvlc/plugins"\nexec "$app/umd-desktop" "$@"\n')
                readme = "Extraé el ZIP con unzip y ejecutá ./UMD en una sesión gráfica. Requiere GTK 3 y WebKitGTK 4.1. El paquete incluye VLC."
            archive.writestr("LEEME.txt", readme + "\nPara actualizar, abrí Configuración → Actualizaciones y elegí Actualizar y reiniciar. La biblioteca se guarda en tu perfil de usuario.\n")
        os.replace(archive_path, final)
    return final


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, help="Native release executable already built from this checkout")
    parser.add_argument("--linux-vlc", type=Path, help="Existing output of eng/linux/bundle-vlc.py")
    parser.add_argument("--output", type=Path, default=WORKSPACE.parent / "artifacts/desktop")
    args = parser.parse_args()
    if not WINDOWS and sys.platform != "linux":
        parser.error("Run on Windows or Linux")
    print(package(args.binary or build(), args.output, args.linux_vlc))
