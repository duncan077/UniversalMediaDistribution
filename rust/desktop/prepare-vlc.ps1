param([string]$Architecture = $env:TAURI_ENV_ARCH)
$ErrorActionPreference = "Stop"
if (-not $Architecture) { $Architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant() }
$arch = switch -Regex ($Architecture) { '^(x86_64|x64|amd64)$' {'x64'} '^(aarch64|arm64)$' {'arm64'} '^(i.86|x86|i686)$' {'x86'} default {throw "Unsupported VLC architecture: $Architecture"} }
$version = "3.0.23.1"
$hash = "Ji3p6cv6bn9FFgXS+M35nzWEvjzpZ5Kig/AvMl5iRAGv4z0QbeMDX+Knuoh2hStsVLb8AATig+LX2HLVey50/A=="
$target = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../target/vlc-bundle"))
$dest = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../target/vlc-active/$arch"))
$package = Join-Path $env:USERPROFILE ".nuget/packages/videolan.libvlc.windows/$version/videolan.libvlc.windows.$version.nupkg"
[IO.Directory]::CreateDirectory($target) | Out-Null
if (-not [IO.File]::Exists($package)) {
    $package = Join-Path $target "videolan.libvlc.windows.$version.nupkg"
    if (-not [IO.File]::Exists($package)) {
        $temp = "$package.$([Guid]::NewGuid()).download"
        try {
            Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/videolan.libvlc.windows/$version/videolan.libvlc.windows.$version.nupkg" -OutFile $temp -UseBasicParsing
            [IO.File]::Move($temp,$package)
        } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
    }
}
$sha = [Security.Cryptography.SHA512]::Create()
$bytes = [IO.File]::OpenRead($package)
try { if ([Convert]::ToBase64String($sha.ComputeHash($bytes)) -ne $hash) {throw "LibVLC checksum mismatch"} } finally {$bytes.Dispose();$sha.Dispose()}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\','/')
        if (-not $name.StartsWith("build/$arch/")) { continue }
        $relative = $name.Substring("build/$arch/".Length)
        if ($relative -notmatch '^(libvlc(core)?\.dll$|plugins/|lua/|hrtfs/)' -or $relative.EndsWith('/')) {continue}
        $file = [IO.Path]::GetFullPath((Join-Path $dest $relative))
        if (-not $file.StartsWith("$dest\",[StringComparison]::OrdinalIgnoreCase)) {throw "Invalid archive path"}
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$file,$true)
    }
} finally {$archive.Dispose()}
