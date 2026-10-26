[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SmokeDirectory,
    [string] $Image = 'umd-linux-runtime:linux-x64'
)

$ErrorActionPreference = 'Stop'
$smoke = (Resolve-Path -LiteralPath $SmokeDirectory).Path
$network = 'umd-smoke-' + [Guid]::NewGuid().ToString('N')
# GLib's resolver needs a configured non-loopback interface. An internal
# network supplies one without allowing these local fixtures onto the Internet.
& docker network create --internal $network | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated Linux test network.' }
try {
    & docker run --rm --network $network --user 65534:65534 `
        --add-host umd-fixture.example.org:127.0.0.1 `
        --mount "type=bind,source=$smoke,target=/smoke,readonly" `
        $Image sh /smoke-desktop.sh
    if ($LASTEXITCODE -ne 0) { throw 'Linux desktop integration checks failed.' }
}
finally {
    & docker network rm $network | Out-Null
}
