# Test Android TV inputs on the PC

`eng/Test-AndroidTvOnPc.ps1` launches the real Windows desktop application with
its built-in television shell enabled. The window uses the same shared Avalonia
layout, focus rules, D-pad navigation, selection, and Back handling as the
Android TV build. No TV, emulator, APK, or ADB connection is required.

This harness covers shared UI flows. Android-only behavior such as the native
activity lifecycle, device decoders, Android WebView, and physical remote
firmware still needs a final device test.

## Clickable PC remote

Run:

```powershell
./eng/Test-AndroidTvOnPc.ps1
```

The harness opens a 1280x720 TV preview and a small always-on-top PC remote.
Click its D-pad, OK, Back, playback, or paging buttons. Screenshot and Inspect
focus buttons save evidence beneath `artifacts/android-tv-pc`.

Preview a phone layout with `-Shell touch -WindowWidth 320 -WindowHeight 568`;
use `844x390` for landscape. Sizes are logical Windows units (DPI scaling can
make screenshots larger in pixels). Preview mode permits small windows without
changing the normal desktop minimum size.

Small-screen and text-input regression checks:

```powershell
./eng/Test-AndroidTvOnPc.ps1 -Scenario tests/AndroidTv/PcScenarios/donation-responsive.json -WindowWidth 800 -WindowHeight 450
./eng/Test-AndroidTvOnPc.ps1 -Scenario tests/AndroidTv/PcScenarios/donation-responsive.json -Shell touch -WindowWidth 320 -WindowHeight 568
./eng/Test-AndroidTvOnPc.ps1 -Scenario tests/AndroidTv/PcScenarios/donation-remote-scroll.json -WindowWidth 800 -WindowHeight 450
./eng/Test-AndroidTvOnPc.ps1 -Scenario tests/AndroidTv/PcScenarios/text-input-no-keyboard.json
```

The donation body scrolls independently while Maybe later and Copy remain
visible. On TV, Up from the actions enters the scroll area; Up/Down scroll,
and the boundaries return to Close or Maybe later. Maybe later has initial
focus and is the default Enter action. Text editing without a software keyboard
keeps Left/Right for the caret and lets Up/Down return to page navigation.

You can also click the preview and use the PC keyboard directly:

- Arrow keys: D-pad
- Enter or Space: Select
- Escape: Back
- Page Up / Page Down: remote paging
- Media keys: playback commands, when the keyboard exposes them

The harness reuses the last desktop Debug build for fast launches. Use `-Build`
after changing application code; the first run builds automatically if no
desktop executable exists.

Typical edit/test loop (no Android recompilation or installation):

```powershell
./eng/Test-AndroidTvOnPc.ps1 -Build -Controller
```

Close the controller, edit the shared UI, and rerun. Omit `-Build` when only
changing a JSON scenario. This is a desktop rebuild, not hot reload.

By default it uses a persistent test profile at
`artifacts/android-tv-pc/profile`, separate from normal desktop data. Pass
`-UseDesktopProfile` to browse the existing desktop catalog, or `-DataDirectory`
to use a specific test profile.

## Quick key sequences

Send a sequence without opening the controller:

```powershell
./eng/Test-AndroidTvOnPc.ps1 `
  -Keys right,right,down,center,back `
  -CaptureEveryStep
```

Supported names are `up`, `down`, `left`, `right`, `center`, `select`, `ok`,
`enter`, `back`, `escape`, `space`, `home`, `menu`, `play_pause`, `play`,
`pause`, `stop`, `next`, `previous`, `page_up`, and `page_down`.

Use `-KeepOpen` to leave the preview running after a key sequence or scenario.

## Automated flows

Run the included smoke flow:

```powershell
./eng/Test-AndroidTvOnPc.ps1 `
  -Scenario tests/AndroidTv/PcScenarios/remote-smoke.json
```

Two deeper regression flows exercise the navigation rework:

```powershell
./eng/Test-AndroidTvOnPc.ps1 -Scenario tests/AndroidTv/PcScenarios/navigation-audit.json
./eng/Test-AndroidTvOnPc.ps1 -Scenario tests/AndroidTv/PcScenarios/sections-and-settings.json
```

`navigation-audit` checks Home, Movies, shelf movement, details, Back to the
same title, Anime, and Books. It needs a populated Movies catalog with multiple
cards/shelves and a title with navigable detail actions. `sections-and-settings`
checks Live TV channel movement, Lists, menu boundaries, and Settings closing
with both Back and OK; it needs at least two loaded Live TV channels. Neither
flow starts playback or changes settings. Providers still use the network, so
use a prepared test profile and adjust waits for slow providers. These are
interactive Windows-session tests, not headless CI tests.

Validate a scenario without building or opening the app:

```powershell
./eng/Test-AndroidTvOnPc.ps1 `
  -Scenario tests/AndroidTv/PcScenarios/remote-smoke.json `
  -ValidateOnly
```

Scenario files contain ordered `key`, `wait`, `screenshot`, `dump-ui`, and
`assert` actions:

```json
{
  "name": "open-first-card",
  "defaultDelayMs": 500,
  "steps": [
    { "name": "to-content", "action": "key", "key": "right" },
    { "name": "next-row", "action": "key", "key": "down" },
    {
      "name": "open",
      "action": "key",
      "key": "center",
      "waitMs": 800,
      "screenshot": true,
      "dumpUi": true,
      "assert": { "running": true }
    }
  ]
}
```

A `key` step accepts `repeat` (1-100) and `holdMs`. Every step accepts `waitMs`,
`screenshot`, `dumpUi`, and an `assert` object. Assertions support:

- `running`: whether the preview process must still be alive
- `uiContains`: a string or array expected in the Windows accessibility tree
- `focusedContains`: text or automation ID expected on the focused control
- `focusedSameAs`: compare with a focus bookmark from an earlier step
- `focusedDifferentFrom`: require an enabled focused control different from a bookmark
- `visibleWithinWindow`: automation ID or array of IDs whose full bounds must be on-screen
- `notVisible`: automation ID or array of IDs which must be absent or off-screen

`"omitKeyUp": true` on a key step deliberately simulates a remote whose release
event is lost. The navigation audit uses this for a menu-to-catalog transition.
`holdMs` delays release (it does not generate OS auto-repeat); the settings flow
holds OK to check that releasing it does not immediately close the new dialog.

Save a bookmark with `"rememberFocus": "opened-card"` on any step. Comparisons
use the automation ID and control type, falling back to the accessible name
when no ID exists, so recreated controls and renamed titles can match. Catalog
cards and entry actions expose stable IDs to distinguish
identically named actions. Two controls with identical accessible identities
cannot be distinguished by these comparisons; use unique IDs in custom flows.

Avalonia may expose some composite controls as a single accessibility element.
When a control is not individually visible to Windows UI Automation, use the
per-step screenshots and `running` assertions for that part of the flow.

Each automated run writes screenshots, accessibility snapshots, and
`result.json` (pass/fail, completed steps, duration, and failure reason) to its
own timestamped artifact directory. Failures also capture a screenshot and UI
snapshot when the window remains available. A failed assertion or
an unexpected app exit makes the script return a non-zero exit code for CI.

Add `-TraceInput` to record key-down/up and focus handoffs in the test profile's
`logs/application.log`. Tracing is off by default. The log records control types
and names, not text entered into fields; screenshots and UI dumps can still
contain profile information, so review artifacts before sharing them.

## Navigation behavior covered by the rework

- A fixed TV rail with an expanding overlay menu avoids sideways shelf reflow.
- Focus uses short transitions and a restrained poster scale.
- Right enters content; Left at the page edge returns to the menu.
- Back closes the current detail/modal before returning to the menu.
- Returning from details restores the selected card; provider updates retain
  the current episode action where available.
- Fresh remote gestures cancel stale delayed focus work. A missing key release
  has bounded recovery, and late provider results cannot reopen a closed title.

The PC tests do not certify native Android player/reader integration, decoding,
the Android keyboard, performance on low-powered hardware, or every content-
dependent screen. Keep a final real-TV smoke check for those boundaries.

## Web player OK / Enter

In the TV shell, OK / Enter on the embedded web player clicks the center of
its current viewport. It works with either the Avalonia player host or the
native WebView focused. Holding Enter does not repeat the click. Mouse/touch
clicks retain their original positions; direct/native video players and shell
buttons keep their existing behavior.

The Android path sends a native tap; the Windows PC preview sends WebView2
pointer input, including into cross-origin player frames. It does not move
the PC's cursor. Center-click behavior is only installed for the TV shell.

Run the opt-in Windows/WebView2 smoke test (opens a temporary PC window):

```powershell
dotnet run --project tests/UniversalMediaDistribution.WebView.SmokeTests
```

It serves two loopback-only pages on separate ephemeral ports and checks native
Enter, held-key suppression, centered coordinates after resizing, trusted
clicks inside a cross-origin iframe, and a disabled activation callback. It
also checks that scripted Enter events cannot trigger a native click and that
the native-click binding is inaccessible to the page's own JavaScript. The
test briefly brings its own window to the foreground to send physical Enter;
it refuses input if another window takes focus.

It uses the native adapter directly, without relaxing the production player's
remote-URL validation or opening a provider/profile. This is a Windows adapter
test, not certification of the Android WebView or every third-party player.
