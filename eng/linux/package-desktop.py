"""Create an updater-compatible tar with correct Unix modes, even on Windows."""
from pathlib import Path
import sys
import tarfile

root = Path(sys.argv[1]).resolve()
archive = Path(sys.argv[2])
executable = root / "UniversalMediaDistribution.Desktop"
if not executable.is_file() or executable.read_bytes()[:4] != b"\x7fELF":
    raise RuntimeError("The Linux NativeAOT executable is missing or is not ELF")
for managed_file in ("UniversalMediaDistribution.Desktop.dll", "libcoreclr.so", "libclrjit.so", "libhostfxr.so"):
    if (root / managed_file).exists():
        raise RuntimeError(f"Linux releases must be NativeAOT, but contain {managed_file}")
for required in ("libvlc.so", "libvlc/libvlccore.so.9", "libvlc/plugins", "licenses/libvlc/packages.tsv"):
    if not (root / required).exists():
        raise RuntimeError(f"The Linux runtime bundle is incomplete: {required}")
with tarfile.open(archive, "w:gz", format=tarfile.PAX_FORMAT) as output:
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise RuntimeError(f"Release payload must contain no symbolic links: {path}")
        info = output.gettarinfo(str(path), "./" + path.relative_to(root).as_posix())
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        info.mode = 0o755 if path.is_dir() or path == executable or ".so" in path.name or path.suffix == ".sh" else 0o644
        if path.is_file():
            with path.open("rb") as content:
                output.addfile(info, content)
        else:
            output.addfile(info)
print(f"Created {archive.name}")
