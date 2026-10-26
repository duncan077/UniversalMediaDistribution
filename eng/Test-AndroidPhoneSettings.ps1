<#
.SYNOPSIS
Checks Settings card bounds and the pinned Close button on an Android phone emulator.
.DESCRIPTION
Open Settings with a disposable test profile before running. The script scrolls
the current page, saves screenshots, and rejects horizontally clipped cards or
a moving/overlapping header. Screenshots can include visible account fields.
It does not build, install, resize the emulator, save preferences, or click actions.
Visual review is still needed for text wrapping and individual control spacing.
.EXAMPLE
./eng/Test-AndroidPhoneSettings.ps1 -Serial emulator-5554 -Pages 8
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^emulator-\d+$')]
    [string] $Serial,
    [string] $AdbPath = 'adb',
    [string] $CloseLabel = 'Close',
    [ValidateRange(1, 60)]
    [int] $Pages = 8,
    [string] $ArtifactsDirectory = ''
)

# Open Settings in a phone emulator before running this check. It only scrolls:
# no account data, provider selections, or saved preferences are changed.
$ErrorActionPreference = 'Stop'
if (-not $ArtifactsDirectory) {
    $ArtifactsDirectory = Join-Path $PSScriptRoot '../artifacts/phone-settings-check'
}
$ArtifactsDirectory = [IO.Path]::GetFullPath($ArtifactsDirectory)
New-Item -ItemType Directory -Force -Path $ArtifactsDirectory | Out-Null

function Invoke-SettingsAdb {
    param([string[]] $Arguments)
    $output = & $AdbPath -s $Serial @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ADB failed: $($Arguments -join ' ')"
    }
    return $output
}

function Get-NodeRectangle {
    param($Node)
    if ($Node.bounds -notmatch '^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$') {
        throw "Unexpected accessibility bounds: $($Node.bounds)"
    }
    return @{
        Left = [int]$Matches[1]; Top = [int]$Matches[2]
        Right = [int]$Matches[3]; Bottom = [int]$Matches[4]
    }
}

$firstCloseBounds = $null
for ($page = 1; $page -le $Pages; $page++) {
    $dumpResult = Invoke-SettingsAdb @('shell', 'uiautomator', 'dump', '/sdcard/umd-phone-settings-check.xml')
    if (($dumpResult -join ' ') -notmatch 'dumped to:') {
        throw 'Android could not capture the current UI. Wait for the emulator to settle, then retry.'
    }
    [xml]$ui = (Invoke-SettingsAdb @('shell', 'cat', '/sdcard/umd-phone-settings-check.xml')) -join "`n"
    $close = $ui.SelectNodes('//node') |
        Where-Object { $_.class -eq 'Button' -and $_.text -eq $CloseLabel } |
        Select-Object -First 1
    if (-not $close) { throw 'Open Settings before running this check; its Close button was not found.' }
    if ($null -eq $firstCloseBounds) { $firstCloseBounds = $close.bounds }
    if ($close.bounds -ne $firstCloseBounds) { throw 'The Close button moved while Settings scrolled.' }

    $scroll = $close.ParentNode.ParentNode.ChildNodes |
        Where-Object { $_.class -eq 'ScrollViewer' } | Select-Object -First 1
    if (-not $scroll) { throw 'Settings scrolling container was not found.' }
    $presenter = $scroll.SelectNodes('.//node') |
        Where-Object { $_.class -eq 'ScrollContentPresenter' } | Select-Object -First 1
    $content = $presenter.ChildNodes | Where-Object { $_.class -eq 'StackPanel' } | Select-Object -First 1
    if (-not $content) { throw 'Settings content was not found.' }
    $viewport = Get-NodeRectangle $scroll
    $contentBounds = Get-NodeRectangle $content
    $closeBounds = Get-NodeRectangle $close
    if ($closeBounds.Bottom -gt $viewport.Top) { throw 'Settings content overlaps the fixed header.' }

    $captureName = 'settings-{0:d2}.png' -f $page
    Invoke-SettingsAdb @('shell', 'screencap', '-p', '/sdcard/umd-phone-settings-check.png') | Out-Null
    Invoke-SettingsAdb @('pull', '/sdcard/umd-phone-settings-check.png', (Join-Path $ArtifactsDirectory $captureName)) | Out-Null

    $cards = @($content.ChildNodes | Where-Object { $_.class -eq 'Border' })
    if ($cards.Count -eq 0) { throw 'No visible Settings cards were found to validate.' }
    foreach ($card in $cards) {
        $bounds = Get-NodeRectangle $card
        if ($bounds.Left -lt ($contentBounds.Left - 2) -or $bounds.Right -gt ($contentBounds.Right + 2)) {
            throw "Settings card exceeds the horizontal content bounds on page $page. See $captureName."
        }
    }
    Write-Output "Page $page passed: $($cards.Count) visible cards fit; Close stays fixed."
    if ($page -lt $Pages) {
        $swipeX = [int](($viewport.Left + $viewport.Right) / 2)
        $swipeStart = [int]($viewport.Top + ($viewport.Bottom - $viewport.Top) * 0.85)
        $swipeEnd = [int]($viewport.Top + ($viewport.Bottom - $viewport.Top) * 0.25)
        Invoke-SettingsAdb @('shell', 'input', 'swipe', "$swipeX", "$swipeStart", "$swipeX", "$swipeEnd", '650') | Out-Null
    }
}
Write-Output "Settings geometry check passed on $Serial. Review captures in $ArtifactsDirectory for text wrapping and touch targets."
