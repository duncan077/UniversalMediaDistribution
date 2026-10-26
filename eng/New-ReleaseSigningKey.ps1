[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string] $KeyId = 'umd-release-1',

    [Parameter()]
    [string] $OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::Combine($PSScriptRoot, '..'))
$keyRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    [System.IO.Path]::Combine($repositoryRoot, 'artifacts', 'release-keys')
}
else {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}

[System.IO.Directory]::CreateDirectory($keyRoot) | Out-Null
$privateKeyPath = [System.IO.Path]::Combine(
    $keyRoot,
    "$KeyId.private.pkcs8.pem")
$publicKeyPath = [System.IO.Path]::Combine(
    $keyRoot,
    "$KeyId.public.spki.base64")

if ([System.IO.File]::Exists($privateKeyPath) -or
    [System.IO.File]::Exists($publicKeyPath)) {
    throw "A signing key named '$KeyId' already exists in '$keyRoot'."
}

$curve = [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256
$ecdsa = [System.Security.Cryptography.ECDsa]::Create($curve)
if ($null -eq $ecdsa) {
    throw 'The runtime could not create the nistP256 release signing key.'
}
try {
    $privateKey = $ecdsa.ExportPkcs8PrivateKeyPem()
    $publicKey = [Convert]::ToBase64String(
        $ecdsa.ExportSubjectPublicKeyInfo())

    [System.IO.File]::WriteAllText(
        $privateKeyPath,
        $privateKey,
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText(
        $publicKeyPath,
        "$publicKey`n",
        [System.Text.UTF8Encoding]::new($false))
}
finally {
    $ecdsa.Dispose()
}

Write-Host "Created release signing key '$KeyId'."
Write-Host "Keep this private file secret and outside source control:"
Write-Host "  $privateKeyPath"
Write-Host "Configure desktop clients with this public file's value:"
Write-Host "  UMD_UPDATE_SIGNING_KEY_ID=$KeyId"
Write-Host "  UMD_UPDATE_SIGNING_PUBLIC_KEY=<contents of $publicKeyPath>"
