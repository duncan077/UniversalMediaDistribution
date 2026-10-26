"""Bundle Debian's VLC 3 runtime, plugins, dependency closure and notices.

Every payload entry is a regular file: the desktop updater rejects symlinks.
ELF RUNPATHs are relative, so there is no global LD_LIBRARY_PATH override that
could change the libraries used by WebKit or the user's desktop applications.
"""
import glob
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys


def run(*args):
    return subprocess.check_output(args, text=True).strip()


destination = Path(sys.argv[1])
library_dir = destination / "libvlc"
library_dir.mkdir(parents=True, exist_ok=True)
roots = [Path(glob.glob("/usr/lib/*/libvlc.so.5")[0]),
         Path(glob.glob("/usr/lib/*/libvlccore.so.9")[0])]
plugins = [Path(p) for p in glob.glob("/usr/lib/*/vlc/plugins/**/*.so", recursive=True)]
if not plugins:
    raise RuntimeError("VLC plugins were not installed")

# glibc and the shared desktop/C++ ABI must come from the host. Loading older
# GLib, C++ or font libraries through VLC can prevent the host's newer WebKit
# from loading later in the same process, even with a private ELF RUNPATH.
host_libraries = {
    "libc.so.6", "libm.so.6", "libdl.so.2", "libpthread.so.0", "librt.so.1",
    "libresolv.so.2", "libutil.so.1", "libanl.so.1",
    "libstdc++.so.6", "libgcc_s.so.1", "libglib-2.0.so.0", "libgobject-2.0.so.0",
    "libgio-2.0.so.0", "libgmodule-2.0.so.0", "libgthread-2.0.so.0", "libffi.so.8",
    "libfontconfig.so.1", "libfreetype.so.6", "libharfbuzz.so.0", "libcairo.so.2",
    "libcairo-gobject.so.2", "libpango-1.0.so.0", "libpangocairo-1.0.so.0",
    "libpangoft2-1.0.so.0", "libgdk_pixbuf-2.0.so.0", "libpixman-1.so.0",
    "libxml2.so.2", "libexpat.so.1", "libz.so.1", "libpng16.so.16",
}
libraries = {path.name: path for path in roots}
pending = roots + plugins
checked = set()
while pending:
    source = pending.pop()
    if source in checked:
        continue
    checked.add(source)
    output = run("ldd", str(source))
    if "not found" in output:
        raise RuntimeError(f"Missing dependency of {source}: {output}")
    for name, path in re.findall(r"^\s*(\S+) => (/\S+)", output, re.MULTILINE):
        if name in host_libraries or name.startswith("ld-linux"):
            continue
        if name not in libraries:
            libraries[name] = Path(path)
            pending.append(Path(path))

owners = set()


def copy_native(source, target, rpath):
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, target)  # dereference symlinks, including SONAME aliases
    target.chmod(0o755)
    run("patchelf", "--set-rpath", rpath, str(target))
    # Debian's merged-/usr layout may report the pre-merge name as the owner.
    candidates = [str(source), str(source.resolve())]
    candidates += [p[4:] for p in candidates if p.startswith("/usr/lib/")]
    for candidate in candidates:
        result = subprocess.run(["dpkg-query", "-S", candidate], text=True, capture_output=True)
        if result.returncode == 0:
            owners.add(result.stdout.split(": ", 1)[0])
            break
    else:
        raise RuntimeError(f"Cannot identify package owner for {source}")


for name, source in sorted(libraries.items()):
    copy_native(source, library_dir / name, "$ORIGIN")
for source in plugins:
    relative = source.relative_to(next(p for p in source.parents if p.name == "plugins"))
    copy_native(source, library_dir / "plugins" / relative, "$ORIGIN/../..")

# .NET's default P/Invoke search finds libvlc.so next to the executable. Its
# dependencies are resolved inside libvlc/ by RUNPATH before the host search.
copy_native(roots[0], destination / "libvlc.so", "$ORIGIN/libvlc")
notices = destination / "licenses" / "libvlc"
notices.mkdir(parents=True, exist_ok=True)
manifest = []
for owner in sorted(owners):
    info = run("dpkg-query", "-W", "-f=${binary:Package}\t${Version}\t${source:Package}\t${source:Version}\n", owner)
    manifest.append(info)
    name = owner.split(":", 1)[0]
    copyright_file = Path("/usr/share/doc") / name / "copyright"
    if not copyright_file.exists():
        raise RuntimeError(f"Missing copyright notice for {owner}")
    shutil.copyfile(copyright_file, notices / f"{name}.copyright")
shutil.copytree("/usr/share/common-licenses", notices / "common-licenses", symlinks=False)
(notices / "packages.tsv").write_text("binary\tversion\tsource\tsource_version\n" + "\n".join(manifest) + "\n")
(notices / "README.txt").write_text(
    "LibVLC 3 and its modules are built by Debian bookworm.\n"
    "The accompanying copyright files describe each component's license.\n"
    "packages.tsv records the exact binary and corresponding source versions.\n"
    "Corresponding Debian source packages: https://sources.debian.org/\n"
    "Historical source versions: https://snapshot.debian.org/\n"
    "ELF RUNPATHs were changed with patchelf; sources were not modified.\n"
    "Build recipe: eng/linux/Dockerfile and eng/linux/bundle-vlc.py in UMD.\n"
)
print(f"Bundled {len(libraries)} libraries and {len(plugins)} VLC plugins")
