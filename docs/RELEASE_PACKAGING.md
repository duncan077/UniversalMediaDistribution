# Release packaging

Build with the .NET 11 RC1 SDK pinned in `global.json` and restore the matching
Android workload before publishing. `Publish-Production.ps1` resolves a compatible
SDK from `-DotnetPath`, `C:\dotnet-11`, or the standard installations; it no longer
prefers the old Preview 7 installation.

For the production build, NativeAOT server deployment, package upload, signed
manifest publication, and public verification in one command, use
`eng/Publish-Production.ps1` as documented in
[Server deployment and desktop updates](SERVER_DEPLOYMENT.md).

The native MVP includes a repeatable release command for self-contained desktop
archives:

```powershell
.\eng\Publish-Release.ps1 -Version 0.7.0 -RuntimeIdentifiers win-x64
```

Artifacts are written to `artifacts/release/<version>` by default. Every archive
has a neighboring SHA-256 checksum file.

The release and production scripts default to `win-x64,linux-x64`. Linux
packaging requires Docker with Linux containers (Docker Desktop on Windows).
Pass `-RuntimeIdentifiers win-x64` to build only Windows, or
`-DesktopRuntimeIdentifiers win-x64` to limit a production run.

Supported desktop runtime identifiers are:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`

No Apple platform is built or shipped: there is no macOS desktop target and no
iOS head in the solution.

Pass more than one value to create several archives:

```powershell
.\eng\Publish-Release.ps1 `
  -Version 0.7.0 `
  -RuntimeIdentifiers win-x64,linux-x64
```

The post-MVP browser prototype is intentionally excluded from this release
command and from the default solution build.

These ZIP and tar archives are both the manual distribution artifacts and the
packages the ask-first desktop updater consumes: it verifies the archive,
extracts it, replaces the files of the running install, and restarts the app.
Android is distributed as two signed APKs published under the `android-arm` and
`android-tv-arm` release identifiers and installed by Android itself after the
user confirms. See
[Server deployment and desktop updates](SERVER_DEPLOYMENT.md) for the signing
and upload flow.

## Platform notes

- The Windows package includes the pinned LibVLC runtime.
- Embedded online video servers use the platform-native web engine. Windows 11
  includes WebView2; Windows 10 installations may need its runtime installed.
- Linux archives include LibVLC 3, playback plugins, media dependency libraries
  and license notices. No system VLC installation is required. The
  bundle is built from Debian bookworm security packages in `eng/linux`, with
  the exact package/source versions recorded in `licenses/libvlc/packages.tsv`.
  Relative ELF RUNPATHs resolve private dependencies without changing the
  process-wide `LD_LIBRARY_PATH`. The host supplies glibc 2.36 or newer, the C++
  runtime, GLib/font/desktop libraries and the usual .NET system dependencies.
  Keeping that shared desktop ABI on the host allows a newer system WebKit to
  coexist with VLC. Alpine/musl is not a target.
- Linux releases are NativeAOT builds (`PublishAot=true`) on every build host.
  `Publish-LinuxAot.ps1` compiles inside a Debian bookworm container, using the
  SDK version from `global.json`, Clang and zlib development headers. The
  source mount is read-only; Linux gets a clean copy without Windows `bin/obj`
  files and a separate NuGet cache. Version and public update-verification
  settings are passed into the native build. No .NET installation or JIT is
  needed on the user's machine. Packaging rejects managed-runtime payloads.
- `Publish-LinuxDesktop.ps1` builds and verifies the native bundle before
  creating the tar archive. Native checks decode WAV and H.264/AAC fixtures in
  a clean Linux stage without a system VLC installation. The archive contains
  regular files, not symlinks, and sets executable modes explicitly even when
  built on Windows, so extraction by the desktop updater preserves the runtime.
- Linux website challenges use WebKitGTK 4.1 through `NativeWebDialog`, with
  scoped HttpOnly cookie recovery, the browser's user agent, document polling,
  cancellation and visible interactive video confirmation. The host must
  install WebKitGTK (Debian/Ubuntu: `libwebkit2gtk-4.1-0`), plus
  `gstreamer1.0-libav` and `gstreamer1.0-plugins-good` for browser video. A challenge may still
  be refused by a provider; browser verification is not a guarantee of access.
- Linux embedded web playback also requires the WPE WebKit runtime.
- Android APK/AAB signing is intentionally separate from this unsigned archive
  command. Store credentials must remain outside the repository and CI logs.
- Production creates an ARM phone/tablet APK (`arm64-v8a`) and an ARM TV APK
  (`armeabi-v7a`). Both are NativeAOT, and NativeAOT compiles exactly one RID,
  so each package carries a single ABI rather than the paired family the Mono
  packages shipped. Mobile packages omit the lean-back activity and TV banner;
  the TV package omits the touch launcher and requires lean-back. The TV keeps
  the 32-bit ABI because those devices run a 32-bit userspace and can never
  load an arm64 library; phones keep the 64-bit one because 64-bit-only
  handsets cannot run the 32-bit one at all.
- NativeAOT applies full trimming. The Mono packages used partial framework
  trimming because full application trimming was considered unsafe for
  reflection-heavy provider and Avalonia paths; those paths are exercised on
  device before each release instead.
- `android-x86-x64` is retired. NativeAOT has no `android-x86` runtime pack, so
  the package could only have carried `x86_64`, which is emulators and a few
  Chromebooks. Devices reporting an x86 or x64 process architecture no longer
  receive updates.
- The legacy universal `android` bridge is retired and no longer built. It
  named four ABIs, so it could not be AOT compiled and would have been the only
  Mono package left in a release. Pass `-IncludeLegacyAndroidBridge` to rebuild
  it for one more rollout. To retire the published bridge on the server as part
  of a production release, pass `-DeleteLegacyAndroidBridge`; deletion occurs
  only after every split package has been published and publicly verified.
- Native `magnet:` association still belongs to the signed installer or store
  package. The Android manifest already declares the scheme.

## Linux integration checks

`tests/UniversalMediaDistribution.Linux.SmokeTests` exercises the actual
WebKitGTK engine under Xvfb, using only a loopback HTTP server. It checks
HttpOnly clearance cookies, the user agent, small JSON responses, delayed
challenge completion, image documents, cancellation and a subsequent request.
It exercises playing-video capture and the trusted app confirmation before a
native-media probe. It also loads the bundled VLC through the application's
.NET initialization path and checks that system WebKit still loads afterwards.

```powershell
.\eng\Publish-LinuxAot.ps1 -RuntimeIdentifier linux-x64 `
  -OutputDirectory artifacts/linux-smoke-aot -SmokeTests
docker build --platform linux/amd64 --target verify `
  -t umd-linux-runtime:linux-x64 eng/linux
.\eng\Test-LinuxDesktop.ps1 -SmokeDirectory artifacts/linux-smoke-aot
```

The NativeAOT publisher requires an empty output directory. The smoke
executable checks that dynamic code generation is disabled before exercising
VLC and WebKit. The runner creates and removes a private Docker network without Internet
access: GLib needs a configured non-loopback interface to resolve the fixture
host. The WebKit sandbox override in that disposable test container is needed for
Docker's nested-user-namespace restriction. It is never set by the application
or included as a desktop launch option. Hardware decoding, audio devices and
real-provider challenges still need testing on a normal Linux desktop. ARM64
bundling selects `linux/arm64` and requires a native ARM64 Docker builder or
configured emulation; the x64 smoke result does not certify ARM64 hardware.
To repeat the same checks on Ubuntu 24.04, build the `verify` stage with
`--build-arg VERIFY_IMAGE=ubuntu:24.04` under a separate image tag and use that
tag as the runner's `-Image` parameter. The LibVLC bundle still comes from Debian bookworm.
