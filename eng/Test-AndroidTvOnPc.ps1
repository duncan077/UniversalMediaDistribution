[CmdletBinding()]
param(
    [Parameter()]
    [string] $Scenario = '',

    [Parameter()]
    [string[]] $Keys = @(),

    [Parameter()]
    [switch] $Controller,

    [Parameter()]
    [switch] $Build,

    [Parameter()]
    [switch] $KeepOpen,

    [Parameter()]
    [switch] $CaptureEveryStep,

    [Parameter()]
    [switch] $TraceInput,

    [Parameter()]
    [ValidateSet('tv', 'touch', 'desktop')]
    [string] $Shell = 'tv',

    [Parameter()]
    [switch] $UseDesktopProfile,

    [Parameter()]
    [string] $DataDirectory = '',

    [Parameter()]
    [string] $ArtifactsDirectory = '',

    [Parameter()]
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [Parameter()]
    [ValidateRange(320, 7680)]
    [int] $WindowWidth = 1280,

    [Parameter()]
    [ValidateRange(240, 4320)]
    [int] $WindowHeight = 720,

    [Parameter()]
    [ValidateRange(5, 120)]
    [int] $StartupTimeoutSeconds = 30,

    [Parameter()]
    [ValidateRange(0, 30000)]
    [int] $DefaultDelayMs = 500,

    [Parameter()]
    [switch] $ValidateOnly
)

$ErrorActionPreference = 'Stop'
$rememberedFocus = @{}
$repositoryRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::Combine($PSScriptRoot, '..'))
$desktopProject = [System.IO.Path]::Combine(
    $repositoryRoot,
    'src',
    'UniversalMediaDistribution.Desktop',
    'UniversalMediaDistribution.Desktop.csproj')
$desktopOutputDirectory = [System.IO.Path]::Combine(
    [System.IO.Path]::GetDirectoryName($desktopProject),
    'bin',
    $Configuration,
    'net11.0')
$desktopExecutable = [System.IO.Path]::Combine(
    $desktopOutputDirectory,
    'UniversalMediaDistribution.Desktop.exe')
$defaultScenario = [System.IO.Path]::Combine(
    $repositoryRoot,
    'tests',
    'AndroidTv',
    'PcScenarios',
    'remote-smoke.json')

# Virtual-key values sent to Avalonia's Windows input backend. These represent
# the PC keyboard equivalent of the remote keys consumed by MainView.
$keyCodes = @{
    up = 0x26
    down = 0x28
    left = 0x25
    right = 0x27
    center = 0x0D
    select = 0x0D
    ok = 0x0D
    enter = 0x0D
    back = 0x1B
    escape = 0x1B
    space = 0x20
    home = 0x24
    menu = 0x12
    play_pause = 0xB3
    play = 0xFA
    pause = 0x13
    stop = 0xB2
    next = 0xB0
    previous = 0xB1
    page_up = 0x21
    page_down = 0x22
}

function Get-SafeName {
    param([Parameter(Mandatory)][string] $Name)

    $safe = $Name.Trim() -replace '[^A-Za-z0-9._-]+', '-'
    $safe = $safe.Trim('-', '.')
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return 'capture'
    }
    return $safe
}

function Resolve-ScenarioPath {
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $defaultScenario
    }
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine((Get-Location).Path, $Path))
}

function Read-TvScenario {
    param([Parameter(Mandatory)][string] $Path)

    if (-not [System.IO.File]::Exists($Path)) {
        throw "TV scenario '$Path' was not found."
    }

    try {
        $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "TV scenario '$Path' is not valid JSON: $($_.Exception.Message)"
    }

    if ($null -eq $document.steps -or @($document.steps).Count -eq 0) {
        throw "TV scenario '$Path' must contain at least one step."
    }

    $knownActions = @('key', 'wait', 'screenshot', 'dump-ui', 'assert')
    $knownAssertions = @('running', 'uiContains', 'focusedContains',
        'focusedSameAs', 'focusedDifferentFrom', 'visibleWithinWindow', 'notVisible')
    $bookmarks = @{}
    if ($null -ne $document.defaultDelayMs -and
        ([int] $document.defaultDelayMs -lt 0 -or [int] $document.defaultDelayMs -gt 30000)) {
        throw 'Scenario defaultDelayMs must be between 0 and 30000.'
    }
    $number = 0
    foreach ($step in @($document.steps)) {
        $number++
        $action = ([string] $step.action).Trim().ToLowerInvariant()
        if ($knownActions -notcontains $action) {
            throw "Scenario step $number has unknown action '$($step.action)'."
        }
        if ($action -eq 'key') {
            $key = ([string] $step.key).Trim().ToLowerInvariant().Replace('-', '_')
            if (-not $keyCodes.ContainsKey($key)) {
                throw "Scenario step $number has unknown PC remote key '$($step.key)'."
            }
            if ($null -ne $step.repeat -and
                ([int] $step.repeat -lt 1 -or [int] $step.repeat -gt 100)) {
                throw "Scenario step $number has an invalid key repeat."
            }
        }
        foreach ($durationName in @('waitMs', 'durationMs', 'holdMs')) {
            $maximum = if ($durationName -eq 'holdMs') { 10000 } else { 120000 }
            if ($null -ne $step.$durationName -and
                ([int] $step.$durationName -lt 0 -or
                    [int] $step.$durationName -gt $maximum)) {
                throw "Scenario step $number has invalid $durationName."
            }
        }
        if ($null -ne $step.assert) {
            foreach ($property in $step.assert.PSObject.Properties) {
                if ($knownAssertions -notcontains $property.Name) {
                    throw "Scenario step $number has unknown assertion '$($property.Name)'."
                }
            }
            foreach ($comparison in @('focusedSameAs', 'focusedDifferentFrom')) {
                if ($null -ne $step.assert.$comparison -and
                    -not $bookmarks.ContainsKey([string] $step.assert.$comparison)) {
                    throw "Scenario step $number references an unknown focus bookmark."
                }
            }
        }
        if (-not [string]::IsNullOrWhiteSpace([string] $step.rememberFocus)) {
            $bookmarks[[string] $step.rememberFocus] = $true
        }
    }

    return $document
}

function Add-NativeTypes {
    if ('UmdTvPcNative' -as [type]) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class UmdTvPcNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    public static IntPtr KeyMessageData(int virtualKey, bool released)
    {
        uint scanCode = MapVirtualKey((uint)virtualKey, 4);
        uint data = 1u | ((scanCode & 0xffu) << 16);
        if ((scanCode & 0xff00u) != 0)
            data |= 1u << 24;
        if (released)
            data |= 0xc0000000u;
        return new IntPtr(unchecked((int)data));
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    private static extern bool GetWindowRectNative(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    public static bool GetWindowRect(IntPtr window, out Rect rect)
    {
        // PrintWindow renders physical pixels. PowerShell can be DPI-unaware,
        // so virtualized bounds would crop the right/bottom of the capture.
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { return GetWindowRectNative(window, out rect); }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PrintWindow(
        IntPtr window,
        IntPtr targetDeviceContext,
        uint flags);
}
'@
}

function Invoke-DesktopBuild {
    Write-Host "Building the desktop TV harness ($Configuration)..."
    & dotnet build $desktopProject --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Desktop build failed with exit code $LASTEXITCODE."
    }
}

function Start-TvPreview {
    param([Parameter(Mandatory)][string] $ProfileDirectory)

    if ($Build -or -not [System.IO.File]::Exists($desktopExecutable)) {
        Invoke-DesktopBuild
    }
    if (-not [System.IO.File]::Exists($desktopExecutable)) {
        throw "Desktop executable '$desktopExecutable' was not produced."
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $desktopExecutable
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.EnvironmentVariables['UMD_UI_SHELL'] = $Shell
    $startInfo.EnvironmentVariables['UMD_PC_UI_PREVIEW'] = '1'
    if ($TraceInput) {
        $startInfo.EnvironmentVariables['UMD_TV_INPUT_TRACE'] = '1'
    }
    if (-not $UseDesktopProfile) {
        $startInfo.EnvironmentVariables['UMD_APPLICATION_DATA_DIRECTORY'] =
            $ProfileDirectory
    }

    Write-Host "Launching the desktop app with the $Shell shell..."
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'The desktop TV preview process could not be started.'
    }
    return $process
}

function Wait-ForMainWindow {
    param([Parameter(Mandatory)][System.Diagnostics.Process] $Process)

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "The TV preview exited during startup with code $($Process.ExitCode)."
        }
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return $Process.MainWindowHandle
        }
        Start-Sleep -Milliseconds 100
    }

    throw "The TV preview did not create a window within $StartupTimeoutSeconds seconds."
}

function Set-TvWindowSize {
    param([Parameter(Mandatory)][IntPtr] $Window)

    $shown = 0x0040
    if (-not [UmdTvPcNative]::SetWindowPos(
            $Window,
            [IntPtr]::Zero,
            30,
            30,
            $WindowWidth,
            $WindowHeight,
            $shown)) {
        Write-Warning 'The TV preview window could not be resized.'
    }
    [UmdTvPcNative]::SetForegroundWindow($Window) | Out-Null
}

function Resolve-KeyCode {
    param([Parameter(Mandatory)][string] $Key)

    $normalized = $Key.Trim().ToLowerInvariant().Replace('-', '_')
    if (-not $keyCodes.ContainsKey($normalized)) {
        $known = ($keyCodes.Keys | Sort-Object) -join ', '
        throw "Unknown PC remote key '$Key'. Use one of: $known."
    }
    return [int] $keyCodes[$normalized]
}

function Send-TvKey {
    param(
        [Parameter(Mandatory)][IntPtr] $Window,
        [Parameter(Mandatory)][string] $Key,
        [ValidateRange(1, 100)][int] $Repeat = 1,
        [ValidateRange(0, 10000)][int] $HoldMs = 35,
        [switch] $OmitKeyUp
    )

    $virtualKey = Resolve-KeyCode -Key $Key
    for ($index = 0; $index -lt $Repeat; $index++) {
        if (-not [UmdTvPcNative]::PostMessage(
                $Window,
                0x0100,
                [IntPtr] $virtualKey,
                [UmdTvPcNative]::KeyMessageData($virtualKey, $false))) {
            throw "Could not send key-down '$Key' to the TV preview."
        }
        if ($HoldMs -gt 0) {
            Start-Sleep -Milliseconds $HoldMs
        }
        if (-not $OmitKeyUp -and -not [UmdTvPcNative]::PostMessage(
                $Window,
                0x0101,
                [IntPtr] $virtualKey,
                [UmdTvPcNative]::KeyMessageData($virtualKey, $true))) {
            throw "Could not send key-up '$Key' to the TV preview."
        }
    }
}

function Save-WindowScreenshot {
    param(
        [Parameter(Mandatory)][IntPtr] $Window,
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)][string] $Name
    )

    Add-Type -AssemblyName System.Drawing
    $rect = [UmdTvPcNative+Rect]::new()
    if (-not [UmdTvPcNative]::GetWindowRect($Window, [ref] $rect)) {
        throw 'Could not read the TV preview window bounds.'
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) {
        throw 'The TV preview window has invalid bounds.'
    }

    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $deviceContext = $graphics.GetHdc()
            try {
                # PW_RENDERFULLCONTENT asks DWM to render this HWND instead of
                # copying whatever happens to cover its screen rectangle.
                $captured = [UmdTvPcNative]::PrintWindow(
                    $Window,
                    $deviceContext,
                    0x00000002)
            }
            finally {
                $graphics.ReleaseHdc($deviceContext)
            }

            if (-not $captured) {
                [UmdTvPcNative]::SetForegroundWindow($Window) | Out-Null
                Start-Sleep -Milliseconds 100
                $graphics.CopyFromScreen(
                    $rect.Left,
                    $rect.Top,
                    0,
                    0,
                    [System.Drawing.Size]::new($width, $height))
            }
        }
        finally {
            $graphics.Dispose()
        }
        $path = [System.IO.Path]::Combine(
            $Directory,
            "$(Get-SafeName -Name $Name).png")
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    Write-Host "  screenshot: $path"
    return $path
}

function Get-UiSnapshot {
    param([Parameter(Mandatory)][IntPtr] $Window)

    Add-Type -AssemblyName UIAutomationClient
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Window)
    if ($null -eq $root) {
        throw 'Windows UI Automation could not inspect the TV preview.'
    }

    $elements = $null
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try {
            $elements = @($root) + @($root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition))
            break
        }
        catch {
            # Avalonia can replace a large virtualized subtree between the UIA
            # root lookup and FindAll. Refresh the root and retry the transient
            # COM failure instead of failing an otherwise healthy TV flow.
            if ($attempt -eq 2) {
                throw
            }
            Start-Sleep -Milliseconds 100
            $root = [System.Windows.Automation.AutomationElement]::FromHandle(
                $Window)
        }
    }
    $snapshot = foreach ($element in $elements) {
        try {
            [ordered]@{
                name = $element.Current.Name
                automationId = $element.Current.AutomationId
                controlType = $element.Current.ControlType.ProgrammaticName
                enabled = $element.Current.IsEnabled
                focusable = $element.Current.IsKeyboardFocusable
                focused = $element.Current.HasKeyboardFocus
                offscreen = $element.Current.IsOffscreen
                bounds = [ordered]@{
                    x = $element.Current.BoundingRectangle.X
                    y = $element.Current.BoundingRectangle.Y
                    width = $element.Current.BoundingRectangle.Width
                    height = $element.Current.BoundingRectangle.Height
                }
            }
        }
        catch {
            # A control can disappear while the tree is enumerated. It is not
            # part of the current snapshot and should not fail the flow.
        }
    }
    return @($snapshot)
}

function Save-UiSnapshot {
    param(
        [Parameter(Mandatory)][IntPtr] $Window,
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)][string] $Name
    )

    $path = [System.IO.Path]::Combine(
        $Directory,
        "$(Get-SafeName -Name $Name).ui.json")
    Get-UiSnapshot -Window $Window |
        ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $path -Encoding utf8
    Write-Host "  UI snapshot: $path"
    return $path
}

function Test-Assertions {
    param(
        [Parameter(Mandatory)] $Assertion,
        [Parameter(Mandatory)][System.Diagnostics.Process] $Process,
        [Parameter(Mandatory)][IntPtr] $Window,
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)][string] $Name
    )

    $Process.Refresh()
    if ($null -ne $Assertion.running -and
        [bool] $Assertion.running -eq $Process.HasExited) {
        throw "Expected preview running=$([bool] $Assertion.running), " +
            "but running=$(-not $Process.HasExited)."
    }

    $needsUi = $null -ne $Assertion.uiContains -or
        $null -ne $Assertion.focusedContains -or
        $null -ne $Assertion.focusedSameAs -or
        $null -ne $Assertion.focusedDifferentFrom -or
        $null -ne $Assertion.visibleWithinWindow -or
        $null -ne $Assertion.notVisible
    if (-not $needsUi) {
        return
    }

    $snapshot = @(Get-UiSnapshot -Window $Window)
    if ($null -ne $Assertion.notVisible) {
        foreach ($id in @($Assertion.notVisible)) {
            if (@($snapshot | Where-Object {
                $_.automationId -ceq $id -and -not $_.offscreen -and
                $_.bounds.width -gt 0 -and $_.bounds.height -gt 0
            }).Count -gt 0) { throw "Control '$id' is still visible." }
        }
    }
    if ($null -ne $Assertion.visibleWithinWindow) {
        $windowBounds = $snapshot[0].bounds
        foreach ($id in @($Assertion.visibleWithinWindow)) {
            $matches = @($snapshot | Where-Object { $_.automationId -ceq $id })
            if ($matches.Count -ne 1) { throw "Expected one visible control '$id'; found $($matches.Count)." }
            $element = $matches[0]
            $b = $element.bounds
            if ($element.offscreen -or $b.width -le 0 -or $b.height -le 0 -or
                $b.x -lt $windowBounds.x -or $b.y -lt $windowBounds.y -or
                $b.x + $b.width -gt $windowBounds.x + $windowBounds.width + 1 -or
                $b.y + $b.height -gt $windowBounds.y + $windowBounds.height + 1) {
                throw "Control '$id' is clipped or outside the preview window."
            }
        }
    }
    $allText = ($snapshot | ForEach-Object {
        "$($_.name) $($_.automationId) $($_.controlType)"
    }) -join [Environment]::NewLine
    foreach ($expected in @($Assertion.uiContains)) {
        if ($allText.IndexOf(
                [string] $expected,
                [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            Save-UiSnapshot -Window $Window -Directory $Directory -Name "$Name-failed" |
                Out-Null
            throw "The TV preview UI did not contain '$expected'."
        }
    }
    if ($null -ne $Assertion.focusedContains) {
        $focused = ($snapshot |
            Where-Object { $_.focused } |
            ForEach-Object { "$($_.name) $($_.automationId) $($_.controlType)" }) -join ' '
        if ($focused.IndexOf(
                [string] $Assertion.focusedContains,
                [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            Save-UiSnapshot -Window $Window -Directory $Directory -Name "$Name-failed" |
                Out-Null
            throw "Focused control did not contain '$($Assertion.focusedContains)'. " +
                "Actual focus: '$focused'."
        }
    }
    foreach ($comparison in @('focusedSameAs', 'focusedDifferentFrom')) {
        if ($null -eq $Assertion.$comparison) { continue }
        $bookmark = [string] $Assertion.$comparison
        if (-not $rememberedFocus.ContainsKey($bookmark)) {
            throw "Unknown focus bookmark '$bookmark'. Use rememberFocus on an earlier step."
        }
        $identity = Get-FocusIdentity -Snapshot $snapshot
        $same = $identity -ceq $rememberedFocus[$bookmark]
        if (($comparison -eq 'focusedSameAs' -and -not $same) -or
            ($comparison -eq 'focusedDifferentFrom' -and $same)) {
            throw "Focus assertion $comparison '$bookmark' failed. Actual: $identity"
        }
    }
}

function Get-FocusIdentity {
    param([Parameter(Mandatory)][object[]] $Snapshot)

    $focused = @($Snapshot | Where-Object { $_.focused -and $_.enabled })
    if ($focused.Count -ne 1) {
        throw "Expected exactly one enabled focused control; found $($focused.Count)."
    }
    $identity = if ([string]::IsNullOrWhiteSpace([string] $focused[0].automationId)) {
        @('name', $focused[0].name, $focused[0].controlType)
    }
    else {
        @('id', $focused[0].automationId, $focused[0].controlType)
    }
    return $identity |
        ConvertTo-Json -Compress
}

function Invoke-ScenarioStep {
    param(
        [Parameter(Mandatory)] $Step,
        [Parameter(Mandatory)][int] $Number,
        [Parameter(Mandatory)][System.Diagnostics.Process] $Process,
        [Parameter(Mandatory)][IntPtr] $Window,
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)][int] $ScenarioDelayMs
    )

    if ($Process.HasExited) {
        throw "The TV preview exited before scenario step $Number."
    }
    $action = ([string] $Step.action).Trim().ToLowerInvariant()
    $label = if ([string]::IsNullOrWhiteSpace([string] $Step.name)) {
        $action
    }
    else {
        [string] $Step.name
    }
    $artifactName = '{0:D2}-{1}' -f $Number, (Get-SafeName -Name $label)
    Write-Host ("[{0:D2}] {1}" -f $Number, $label)

    switch ($action) {
        'key' {
            $repeat = if ($null -eq $Step.repeat) { 1 } else { [int] $Step.repeat }
            $holdMs = if ($null -eq $Step.holdMs) { 35 } else { [int] $Step.holdMs }
            Send-TvKey `
                -Window $Window `
                -Key ([string] $Step.key) `
                -Repeat $repeat `
                -HoldMs $holdMs `
                -OmitKeyUp:([bool] $Step.omitKeyUp)
        }
        'wait' {
            $duration = if ($null -eq $Step.durationMs) {
                $ScenarioDelayMs
            }
            else {
                [int] $Step.durationMs
            }
            Start-Sleep -Milliseconds $duration
        }
        'screenshot' {
            Save-WindowScreenshot `
                -Window $Window `
                -Directory $Directory `
                -Name $artifactName | Out-Null
        }
        'dump-ui' {
            Save-UiSnapshot `
                -Window $Window `
                -Directory $Directory `
                -Name $artifactName | Out-Null
        }
        'assert' { }
    }

    $delay = if ($null -ne $Step.waitMs) {
        [int] $Step.waitMs
    }
    elseif ($action -eq 'wait') {
        0
    }
    else {
        $ScenarioDelayMs
    }
    if ($delay -gt 0) {
        Start-Sleep -Milliseconds $delay
    }

    if ($CaptureEveryStep -or [bool] $Step.screenshot) {
        Save-WindowScreenshot `
            -Window $Window `
            -Directory $Directory `
            -Name $artifactName | Out-Null
    }
    if ([bool] $Step.dumpUi) {
        Save-UiSnapshot `
            -Window $Window `
            -Directory $Directory `
            -Name $artifactName | Out-Null
    }
    if ($null -ne $Step.assert) {
        Test-Assertions `
            -Assertion $Step.assert `
            -Process $Process `
            -Window $Window `
            -Directory $Directory `
            -Name $artifactName
    }
    if (-not [string]::IsNullOrWhiteSpace([string] $Step.rememberFocus)) {
        $rememberedFocus[[string] $Step.rememberFocus] = Get-FocusIdentity `
            -Snapshot @(Get-UiSnapshot -Window $Window)
    }
}

function Show-PcRemoteController {
    param(
        [Parameter(Mandatory)][System.Diagnostics.Process] $Process,
        [Parameter(Mandatory)][IntPtr] $Window,
        [Parameter(Mandatory)][string] $Directory
    )

    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()
    $form = [System.Windows.Forms.Form]::new()
    $form.Text = 'Android TV PC Remote'
    $form.ClientSize = [System.Drawing.Size]::new(300, 455)
    $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $form.MaximizeBox = $false
    $form.TopMost = $true
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
    $workingArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $form.Location = [System.Drawing.Point]::new(
        [Math]::Max(0, $workingArea.Right - $form.Width - 20),
        40)

    $heading = [System.Windows.Forms.Label]::new()
    $heading.Text = "PC remote -> TV preview`r`nKeyboard also works in the preview"
    $heading.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
    $heading.SetBounds(20, 12, 260, 42)
    $form.Controls.Add($heading)

    $status = [System.Windows.Forms.Label]::new()
    $status.Text = 'Ready'
    $status.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
    $status.SetBounds(20, 397, 260, 35)
    $form.Controls.Add($status)

    $send = {
        param([string] $Key)
        try {
            if ($Process.HasExited) {
                $status.Text = 'Preview has exited'
                return
            }
            Send-TvKey -Window $Window -Key $Key
            $status.Text = "Sent: $Key"
        }
        catch {
            $status.Text = $_.Exception.Message
        }
    }

    $addButton = {
        param(
            [string] $Text,
            [int] $X,
            [int] $Y,
            [int] $Width,
            [int] $Height,
            [scriptblock] $Click
        )
        $button = [System.Windows.Forms.Button]::new()
        $button.Text = $Text
        $button.SetBounds($X, $Y, $Width, $Height)
        $button.Add_Click($Click)
        $form.Controls.Add($button)
    }

    & $addButton 'Up' 110 65 80 48 { & $send 'up' }
    & $addButton 'Left' 25 118 80 48 { & $send 'left' }
    & $addButton 'OK' 110 118 80 48 { & $send 'center' }
    & $addButton 'Right' 195 118 80 48 { & $send 'right' }
    & $addButton 'Down' 110 171 80 48 { & $send 'down' }
    & $addButton 'Back' 25 237 120 42 { & $send 'back' }
    & $addButton 'Play / Pause' 155 237 120 42 { & $send 'play_pause' }
    & $addButton 'Page up' 25 286 120 42 { & $send 'page_up' }
    & $addButton 'Page down' 155 286 120 42 { & $send 'page_down' }
    & $addButton 'Screenshot' 25 343 120 42 {
        try {
            $name = 'manual-' + [DateTime]::Now.ToString('HHmmss')
            Save-WindowScreenshot -Window $Window -Directory $Directory -Name $name |
                Out-Null
            $status.Text = 'Screenshot saved'
        }
        catch {
            $status.Text = $_.Exception.Message
        }
    }
    & $addButton 'Inspect focus' 155 343 120 42 {
        try {
            $name = 'manual-' + [DateTime]::Now.ToString('HHmmss')
            $snapshot = @(Get-UiSnapshot -Window $Window)
            Save-UiSnapshot -Window $Window -Directory $Directory -Name $name |
                Out-Null
            $focused = $snapshot | Where-Object { $_.focused } | Select-Object -First 1
            $status.Text = if ($null -eq $focused) {
                'No accessible focus reported'
            }
            else {
                "Focus: $($focused.name)"
            }
        }
        catch {
            $status.Text = $_.Exception.Message
        }
    }

    $timer = [System.Windows.Forms.Timer]::new()
    $timer.Interval = 500
    $timer.Add_Tick({
        if ($Process.HasExited) {
            $timer.Stop()
            $form.Close()
        }
    })
    $timer.Start()
    try {
        [void] $form.ShowDialog()
    }
    finally {
        $timer.Dispose()
        $form.Dispose()
    }
}

$scenarioDocument = $null
if (-not [string]::IsNullOrWhiteSpace($Scenario) -or $ValidateOnly) {
    $scenarioPath = Resolve-ScenarioPath -Path $Scenario
    $scenarioDocument = Read-TvScenario -Path $scenarioPath
    $scenarioName = if ([string]::IsNullOrWhiteSpace([string] $scenarioDocument.name)) {
        [System.IO.Path]::GetFileNameWithoutExtension($scenarioPath)
    }
    else {
        [string] $scenarioDocument.name
    }
    Write-Host "Validated TV scenario '$scenarioName' ($(@($scenarioDocument.steps).Count) steps)."
}
if ($ValidateOnly) {
    return
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The PC TV input harness currently requires Windows.'
}

Add-NativeTypes
$runName = if ($null -ne $scenarioDocument) {
    Get-SafeName -Name $scenarioName
}
elseif ($Keys.Count -gt 0) {
    'keys'
}
else {
    'controller'
}
$artifactRoot = if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    [System.IO.Path]::Combine($repositoryRoot, 'artifacts', 'android-tv-pc')
}
else {
    [System.IO.Path]::GetFullPath($ArtifactsDirectory)
}
$timestamp = [DateTime]::Now.ToString('yyyyMMdd-HHmmss')
$runDirectory = [System.IO.Path]::Combine($artifactRoot, "$timestamp-$runName")
[System.IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$profileDirectory = if (-not [string]::IsNullOrWhiteSpace($DataDirectory)) {
    [System.IO.Path]::GetFullPath($DataDirectory)
}
else {
    [System.IO.Path]::Combine($artifactRoot, 'profile')
}
if (-not $UseDesktopProfile) {
    [System.IO.Directory]::CreateDirectory($profileDirectory) | Out-Null
}

$previewProcess = $null
$passed = $false
$failure = $null
$completedSteps = 0
$window = [IntPtr]::Zero
$runTimer = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $previewProcess = Start-TvPreview -ProfileDirectory $profileDirectory
    $window = Wait-ForMainWindow -Process $previewProcess
    Set-TvWindowSize -Window $window
    Start-Sleep -Milliseconds 1500

    if ($null -ne $scenarioDocument) {
        $scenarioDelay = if ($null -eq $scenarioDocument.defaultDelayMs) {
            $DefaultDelayMs
        }
        else {
            [int] $scenarioDocument.defaultDelayMs
        }
        $stepNumber = 0
        foreach ($step in @($scenarioDocument.steps)) {
            $stepNumber++
            Invoke-ScenarioStep `
                -Step $step `
                -Number $stepNumber `
                -Process $previewProcess `
                -Window $window `
                -Directory $runDirectory `
                -ScenarioDelayMs $scenarioDelay
            $completedSteps++
        }
    }

    foreach ($key in $Keys) {
        Write-Host "Sending $key"
        Send-TvKey -Window $window -Key $key
        Start-Sleep -Milliseconds $DefaultDelayMs
        if ($CaptureEveryStep) {
            Save-WindowScreenshot `
                -Window $window `
                -Directory $runDirectory `
                -Name "key-$key" | Out-Null
        }
    }

    if ($Controller -or ($null -eq $scenarioDocument -and $Keys.Count -eq 0)) {
        Show-PcRemoteController `
            -Process $previewProcess `
            -Window $window `
            -Directory $runDirectory
    }

    if (-not $previewProcess.HasExited) {
        $passed = $true
    }
}
catch {
    $failure = $_.Exception.Message
    if ($window -ne [IntPtr]::Zero -and $null -ne $previewProcess -and
        -not $previewProcess.HasExited) {
        try {
            Save-WindowScreenshot -Window $window -Directory $runDirectory -Name 'failure' | Out-Null
            Save-UiSnapshot -Window $window -Directory $runDirectory -Name 'failure' | Out-Null
        }
        catch {
            Write-Warning "Could not capture failure evidence: $($_.Exception.Message)"
        }
    }
    throw
}
finally {
    $runTimer.Stop()
    $result = [ordered]@{
        scenario = if ($null -eq $scenarioDocument) { $null } else { $scenarioName }
        shell = $Shell
        window = "${WindowWidth}x${WindowHeight}"
        processId = if ($null -eq $previewProcess) { $null } else { $previewProcess.Id }
        passed = $passed
        completedSteps = $completedSteps
        durationMs = $runTimer.ElapsedMilliseconds
        failure = $failure
        artifacts = $runDirectory
        profile = if ($UseDesktopProfile) { 'desktop' } else { $profileDirectory }
    }
    $result | ConvertTo-Json |
        Set-Content `
            -LiteralPath ([System.IO.Path]::Combine($runDirectory, 'result.json')) `
            -Encoding utf8

    if ($null -ne $previewProcess -and
        -not $previewProcess.HasExited -and
        -not $KeepOpen) {
        $previewProcess.CloseMainWindow() | Out-Null
        if (-not $previewProcess.WaitForExit(5000)) {
            $previewProcess.Kill()
            $previewProcess.WaitForExit()
        }
    }
}

if (-not $passed) {
    throw 'The Android TV PC flow did not complete successfully.'
}
Write-Host ''
Write-Host "Android TV PC flow passed. Artifacts: $runDirectory"
