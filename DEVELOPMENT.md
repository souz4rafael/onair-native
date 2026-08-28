# onAIr — Development Notes

Use this file to resume development in a new session. Tell Copilot:
"I want to continue onAIr — read DEVELOPMENT.md in the repo."

---

## Repo & build

- **Repo:** https://github.com/souz4rafael/onair-native
- **Local:** `C:\Users\rafasouza\OneDrive - Microsoft\Documents\Microsoft Scout\OnAirNative\`
- **Build:**
  ```powershell
  cd "...\Microsoft Scout\OnAirNative"
  dotnet build OnAirNative\OnAirNative.csproj -c Debug
  # exe: OnAirNative\bin\Debug\net8.0-windows10.0.19041.0\OnAirNative.exe
  ```
- **Current version:** 1.1.0 — bump it in **two** places, they must stay in sync:
  `OnAirNative/ViewModels/AboutTabViewModel.cs` (`Version`) and
  `installer/onair-native.nsi` (`PRODUCT_VERSION`).
- **Releases:** https://github.com/souz4rafael/onair-native/releases (latest published: v1.1.0)
- **Stack:** WinUI 3 (Windows App SDK 2.1.3), .NET 8, NAudio 2.2.1, whisper.net 1.7.3,
  CommunityToolkit.Mvvm 8.3.2, System.Security.Cryptography.ProtectedData 8.0.0

---

## Terminology

User-facing vocabulary, since it's a common source of confusion if you're skimming code vs. UI:

- **The Box** — what the UI calls the transparent, always-on-top teleprompter window. The
  underlying class is still `OverlayWindow`/`OverlayViewModel` internally (renaming the class/file
  names, XAML `x:Name`s, config field names like `OverlayProtected`, and hotkey action identifiers
  was deliberately **not** done — no user-visible benefit, high regression risk, and it would have
  broken existing installs' persisted `config.json` field names). Only user-facing strings (button
  labels, tooltips, About-tab text, docs) say "Box".
- **onAIr** — the app's display name, everywhere a human reads it (About tab, installer, tray,
  docs). The technical project name/namespace/exe filename (`OnAirNative`) is intentionally
  unchanged — Mission Control and other tooling reference the static `OnAirNative.exe` path, and
  renaming it would break that.

---

## Architecture

Two windows, 11 services, MVVM pattern. The entry point is a **custom `Main`**
(`Program.cs`, with `DISABLE_XAML_GENERATED_MAIN=true`) so single-instance is resolved
before XAML boots.

```
Program.cs               — AppInstance.FindOrRegisterForKey("onAIr-native-main")
│                          → RedirectActivationToAsync + exit when another instance owns the key
└── App.xaml.cs          — service wiring, hotkey dispatch, OnRedirectedActivation
    ├── OverlayWindow        — the Box: transparent, frameless, always-on-top (hidden by default)
    │   └── OverlayViewModel — script, Q&A, scroll modes, voice
    └── ControllerWindow     — 5-tab control panel (main app window)
        └── ControllerViewModel
            ├── ScrollTabViewModel
            ├── AiTabViewModel
            └── AboutTabViewModel — version, hotkeys, update check/install

Services/
├── ConfigService        — JSON persistence -> %LocalAppData%\onAIr\config.json
│                          (auto-migrates from the pre-1.1 "onAIr Native" folder, see below)
├── SecretProtector      — DPAPI (CurrentUser) encryption for API keys, `dpapi:v1:` prefix
├── WindowService        — DWM transparency, click-through, AoT, BringToFront
├── HotkeyService        — Win32 RegisterHotKey on background thread
├── AudioService         — WASAPI mic + loopback mixdown + RMS voice monitor
├── WhisperService       — whisper.net in-process + cloud API fallback
├── AiChatService        — 6 AI providers via HttpClient
├── TrayService          — Shell_NotifyIcon tray icon + context menu
├── StealthWindowService — EnumWindows window list
├── WindowEmbedService   — SetParent window embedding in stealth container
└── UpdateService        — GitHub Releases check + installer download/launch
```

---

## Controller tabs

| Tab | Key features |
|-----|-------------|
| **Script** | Load .txt, Manual/Auto/Voice scroll (one mode-specific speed control shown at a time), font size/Box opacity/color, save/reset settings — organised into cards |
| **Q&A** | Record button, 6 AI providers, Whisper model path, system prompt — organised into cards |
| **App Stealth** | Embed any Win32 app in WDA_EXCLUDEFROMCAPTURE container (interactive!) — single card |
| **Settings** | Audio device selector, capture source (mic / system / both), voice threshold slider, System/Light/Dark theme picker — organised into cards |
| **About** | Version, hotkeys, GitHub link, update check/install — Updates and Keyboard Shortcuts organised into cards |

**Footer:** a 2-column layout — the 3 Box controls (**Open Box** visible/hidden ·
**Lock Box** locked/unlocked · **Hide Box** visible/hidden in share, `OverlayProtected`) on the
left, **Hide Controller** (Controller capture protection) right-aligned on its own. All 4 labels
are static (icon + fixed word) — only the pressed/checked visual reflects current state, no dynamic
text swap. Labels are short enough to fit one row at the default Controller window width
(600×640 logical, bumped from 520 to make room); full descriptions live in each button's
`ToolTipService.ToolTip`.

---

## Global hotkeys

| Hotkey | Action |
|--------|--------|
| Ctrl+Alt+PgUp/PgDn | Scroll script (Manual mode) |
| Ctrl+Alt+Home | Lock / unlock Box (Move Mode) |
| Ctrl+Alt+R | Q&A record start/stop |
| Ctrl+Alt+O | Open file picker |
| Ctrl+Alt+] / [ | Increase / decrease Box opacity (`ControllerWindow.AdjustOpacity`) |
| Ctrl+Alt+V | Open / hide Box (`ControllerWindow.ToggleOverlayVisibility`) |
| Ctrl+Alt+S | Hide / show Box in share (`ToggleOverlayCaptureProtection`) |
| Ctrl+Alt+H | Hide / show Controller in share (`ToggleControllerCaptureProtection`) |
| Ctrl+Alt+U | Release the App Stealth container, if embedded (`ReleaseStealthContainer`) |
| Ctrl+Alt+. / , | Increase / decrease Auto-scroll speed (`ControllerWindow.AdjustScrollSpeed`) |
| Ctrl+Alt+= / - | Increase / decrease font size (`ControllerWindow.AdjustFontSize`) |

Removed in an earlier pass: Ctrl+Alt+M (cycle Script↔Q&A) and the *original* binding of
Ctrl+Alt+, (bring Controller to front) — both dropped per request, and
`OverlayViewModel.CycleMode()` was deleted as dead code along with them.
`WindowService.BringToFront` itself is untouched and still used by the tray's "Show
Controller" menu item and double-click. Ctrl+Alt+, was later reassigned to Decrease
Auto-scroll speed — freed keys get reused rather than left permanently retired.

Hotkeys are registered on a dedicated background thread that owns its own message loop
(`HotkeyService.HotkeyLoop`). IDs are contiguous (`ID_SCROLL_UP`..`ID_FONT_SIZE_DOWN`)
and the cleanup loop unregisters the whole range — keep new IDs inside it.

The toggle-style hotkeys (Box visibility, Box/Controller capture protection) flip the
corresponding footer `ToggleButton.IsChecked` rather than duplicating logic — this reuses
the existing `Checked`/`Unchecked` handlers so the hotkey and a manual click can never fall
out of sync. The Lock/Unlock footer button follows the same rule: its `PropertyChanged` sync
(for Ctrl+Alt+Home) sets `LockToggle.IsChecked` instead of only updating its label —
previously a hotkey-driven lock/unlock left `IsChecked` stale, so the *next manual click*
silently re-affirmed the current state instead of flipping it. The opacity/scroll-speed/
font-size "Adjust*" methods all follow one more shared pattern: compute the new value, set
the ViewModel property (which persists to config and pushes the change to the Box), then
sync the on-screen slider under the `_populatingUi` guard so it doesn't fire a second,
redundant write.

**Verifying a new hotkey actually works — a caveat about this dev machine:** synthetic
keyboard input (`keybd_event`/`SendInput`) for automated end-to-end hotkey testing has
degraded on this dev machine during past sessions — `RegisterHotKey` still succeeds for
every combo (confirmed via temporary diagnostic logging) and direct UI Automation
manipulation of the same controls still works and can be used to verify the underlying
logic, but simulated key **presses** sometimes stop reaching the app's `WndProc` even for
hotkeys previously proven to work. This looks environmental (likely security software
throttling repeated synthetic input over a long automation session), not a code regression.
If a *newly added* hotkey doesn't fire in a fresh dev/debug session, verify with a **real
key press** before assuming a code bug.

---

## In-app update check (About tab)

`UpdateService` (`Services/UpdateService.cs`) queries the GitHub Releases API —
`GET /repos/souz4rafael/onair-native/releases/latest` — no auth token needed since the
repo is public (just a `User-Agent` header, which GitHub requires even for anonymous
requests). It parses `tag_name` (stripping the `v` prefix), compares it against
`AboutTabViewModel.Version` via `System.Version`, and finds the first `.exe` asset for
the download URL.

**Flow:**
1. Auto-check fires once per session, the first time the About tab is opened
   (`ControllerWindow.NavView_SelectionChanged`, same lazy-load pattern as the App
   Stealth window list / Settings audio devices). A manual **Check for Updates**
   button re-runs it any time.
2. If newer, the **⬇ Download & Install vX.Y.Z** button appears. Clicking it shows a
   confirmation `ContentDialog` (built in code — not a XAML dialog — since it's a
   one-off yes/no) warning that the app will close and Windows may prompt for admin.
3. On confirm, `UpdateService.DownloadInstallerAsync` streams the asset to
   `%TEMP%` with progress reported back to `AboutTabViewModel.DownloadProgress`.
4. `UpdateService.LaunchInstaller` starts the downloaded `onAIr-Setup-X.Y.Z.exe`
   via `UseShellExecute = true` — the NSIS installer's own manifest
   (`RequestExecutionLevel admin`) is what triggers the UAC prompt, no `runas` verb
   needed.
5. `AboutTabViewModel.InstallerLaunched` fires → `ControllerWindow` closes itself,
   which runs the existing `OnClosed` cleanup (save config, dispose services, `Exit()`).
   No explicit delay is needed before closing: the installer shows its own Welcome /
   Directory / Install wizard pages first, so by the time it actually tries to
   overwrite `OnAirNative.exe` our process has long since exited and released the file.

**Not implemented:** silent/unattended background auto-install. This app is used live
during presentations and screen shares, so an update interrupting a session
unannounced would be worse than a manual "update available" nudge — the check is
automatic, the install is always one explicit click + confirmation away.

---

## Scroll modes: per-mode controls, Voice speed, and a font-size bug

Each of the three scroll modes has its own independent speed/step setting, and the
Script tab shows **only** the one that applies to the currently selected mode
(`ControllerWindow.ScrollModeChanged`) instead of showing all of them (or, previously,
Auto's slider next to Manual's with no way to tell which was "live"):

| Mode | Control | Config field | Mechanism |
|------|---------|---------------|-----------|
| Manual | Scroll step (px) | `ScrollStep` | Applied once per `Ctrl+Alt+PgUp/PgDn` press or ▲▼ button click |
| Auto | Auto-scroll speed | `ScrollSpeed` | `OverlayViewModel._autoTimer`, continuous 50 ms tick |
| Voice | Voice scroll speed | `VoiceScrollSpeed` | `OverlayViewModel._voiceTimer`, continuous 50 ms tick, gated on `IsVoiceActive` |

**Voice mode used to be much weaker than Auto even at max settings.** It shared `ScrollSpeed`
with Auto *and* only actually scrolled once every 3 microphone `DataAvailable` callbacks (a
debounce baked into the old `OnVoiceRms`). Both problems are fixed by giving Voice its own
`VoiceScrollSpeed` and its own continuous timer (`StartVoiceScroll`/`VoiceScrollTick`), same
shape as Auto's — `OnVoiceRms` now only updates `IsVoiceActive`/`MicLevel`, the timer does the
actual scrolling every tick while voice is detected. Starting a Q&A recording while in Voice
mode now also stops `_voiceTimer` (not just the audio monitor) — otherwise, if voice happened
to be "active" at that exact moment, the timer would keep scrolling forever since nothing was
left to ever flip `IsVoiceActive` back to `false`.

**Font size wasn't applying live from the Controller.** `ScrollTabViewModel.OnFontSizeChanged`
always correctly wrote to config *and* to `OverlayViewModel.FontSize`, but
`OverlayWindow.xaml.cs`'s `PropertyChanged` switch had no case for `FontSize` — only `FontColor`
did — so nothing ever pushed the new value onto `ScriptTextBlock.FontSize`. Fixed by adding the
missing case, plus seeding `ScriptTextBlock.FontSize` from the ViewModel at startup (next to the
existing `ScriptText` seed) so a persisted custom size is honoured on relaunch, not just the
XAML default of 22.

### Card UI (all tabs + footer)

`ControllerWindow.xaml`'s root `Grid.Resources` defines two reusable styles:

- `CardBorderStyle` — a `Border` using the standard WinUI3
  `CardBackgroundFillColorDefaultBrush`/`CardStrokeColorDefaultBrush` theme resources.
- `CardTitleStyle` — a `TextBlock` style for card headers: `FontWeight="Bold"`, `FontSize="12"`,
  `CharacterSpacing="40"` (tracked-out caps look), `Foreground="{ThemeResource
  AccentTextFillColorPrimaryBrush}"`. WinUI3's `TextBlock` has no `CharacterCasing` property (unlike
  WPF), so card titles are typed as literal uppercase strings in XAML (e.g. `"SCRIPT FILE"`), not
  produced by a style setter.

Every tab is organised into cards:

| Tab | Cards |
|-----|-------|
| **Script** | SCRIPT FILE · SCROLL MODE · SCROLL STEP (PX)/AUTO-SCROLL SPEED/VOICE SCROLL SPEED (only the active mode's card is shown) · APPEARANCE (font size/Box opacity/color) · VIRTUAL SCROLL BUTTONS |
| **Q&A** | RECORDING · AI PROVIDER (chat + transcription + configure/test) · PROMPTS (system prompt + presentation context) · WHISPER MODEL |
| **Settings** | AUDIO SOURCE (recording source + input/output device + refresh) · VOICE SCROLL SENSITIVITY · THEME |
| **App Stealth** | APP STEALTH — single card, the whole embed flow is one cohesive task |
| **About** | Identity block (app name/version/authors/link) stays uncarded — the "onAIr" heading is header enough — followed by UPDATES and KEYBOARD SHORTCUTS cards |

The footer's button rows are also wrapped in a `CardBorderStyle` border. Save/Reset on the Script
tab stays a plain button row (an action bar, not a settings group) rather than its own card.

### Theme picker (System/Light/Dark)

Settings tab → THEME card, 3 `RadioButton`s (no explicit `GroupName` — they group by shared parent,
same as the Script tab's scroll-mode radios) bound through `ControllerViewModel.Theme` (persisted as
`AppConfig.Theme`: `"System"` | `"Light"` | `"Dark"`) → `ThemeChanged` event → `ControllerWindow.
ApplyTheme()`, which sets `((FrameworkElement)Content).RequestedTheme`. Applied once in the
constructor (right after `InitializeComponent()`, before `Activated`/first paint) so a saved
Light/Dark preference shows with no flash on launch, and again on every radio change for a live
switch with no restart needed.

**The hard part wasn't the theme switch — it was a pre-existing rendering bug it exposed.** The
root `<Grid>` in `ControllerWindow.xaml` never had an explicit `Background`. In dark mode this was
invisible: an unthemed/uncomposed WinUI 3 surface with nothing drawn falls back to solid black,
which just happened to look like intentional dark chrome. Switching to Light theme exposed it —
the `NavigationView` top-pane strip and any gaps around the cards stayed solid black while the
cards themselves (which *do* set `Background` via `CardBorderStyle`) correctly turned light,
producing dark-on-black invisible tab labels. Fixed by adding
`Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"` to the root Grid — a plain
(non-Acrylic) brush that responds to `ActualTheme` like any other `ThemeResource`. Two dead ends
before landing on this, kept here so they aren't retried: (1) setting `NavView.RequestedTheme`
directly (in addition to the root's) — this actually made it *worse*, producing a split-brain state
where the NavView chrome and its own Content (the ScrollViewer with all the tab panels) ended up on
two different themes, so don't set `RequestedTheme` on `NavView` itself, only on the window's root
content; (2) `NavigationView.PaneBackground` — doesn't exist on Windows App SDK 2.1.3's
`NavigationView` (`Background` is the only settable brush, but it wasn't the actual bug either).

### Footer reorg + Box/onAIr rebrand (v1.1.0)

Two independent asks landed together:

1. **Footer layout.** The 3 Box toggles (previously 2 rows of 2, with the 4th Controller-capture
   toggle sharing a row) moved into a single `Grid` with `ColumnDefinition="*,Auto"`: a left-aligned
   horizontal `StackPanel` holding the 3 Box buttons in column 0, and the Controller's capture
   toggle alone in column 1 (naturally right-aligned by the `Auto` column). All 4 button labels
   became **static** (`"📦 Open Box"`, `"🔒 Lock Box"`, `"🙈 Hide Box"`, `"🙈 Hide Controller"`) instead
   of swapping text on check/uncheck — matching how the Controller-capture button already worked.
   The `Sync*Toggle` methods were simplified to only set `IsChecked` (they used to also rewrite
   `.Content`); full descriptions moved to `ToolTipService.ToolTip` on each button.
2. **Terminology rename.** Every user-facing string that said "overlay" now says "Box" (footer,
   Script tab's opacity slider label, About tab's shortcut list, tray menu). Every user-facing
   string that said "onAIr Native" now says "onAIr" (About tab heading, installer product name/
   shortcuts/Add-Remove-Programs entry, tray tooltip/menu, update-confirmation dialog). See the
   "Terminology" section near the top of this file for what was **not** renamed and why.
3. **Config folder migration.** `ConfigService` now reads/writes
   `%LocalAppData%\onAIr\config.json` instead of `%LocalAppData%\onAIr Native\config.json`. On
   first run after upgrading, if the new path has no config yet but the old one does, it's copied
   over (not moved — the old file is left in place, untouched) so existing installs keep their
   settings and encrypted API keys without any user action. Diagnostic logs (`launch.log`,
   `crash.log`, `controller-init.log`, `overlay-init.log`, `tray.log`) simply start fresh under the
   new folder — no migration needed, they're transient debug output, not settings.
4. **Original-project references removed.** The README's Electron-comparison table, the
   "native spinoff of onAIr v1.3.0 (Electron)" tagline, the "config.json format compatible with the
   Electron app" note, and the shared-license backlink were all dropped — this project no longer
   references its predecessor anywhere in the docs or UI (the unused `AboutTabViewModel.BaseApp`
   dead property that held "Based on onAIr v1.3.0 (Electron)" was deleted for the same reason).

---

## Audio capture & the mic + system mix

`AudioService.StartRecordingAsync(source)` handles three sources:

| `source` | Behaviour |
|----------|-----------|
| `microphone` | Single `WasapiCapture`, written in the device's native format |
| `system` | Single `WasapiLoopbackCapture`, native format |
| `both` | Real-time mixdown of both, normalised to **16 kHz mono 16-bit PCM** |

The mix path (`StartMixedRecording`) is the delicate one:

- Each leg feeds a `BufferedWaveProvider` (10 s, `DiscardOnBufferOverflow`), then goes through
  `ToMixFormat`: stereo → mono via `StereoToMonoSampleProvider`, >2 channels via
  `MultiplexingSampleProvider`, sample-rate conversion via `WdlResamplingSampleProvider`,
  and a `VolumeSampleProvider` at `MixLegGain = 0.8f` so summing two sources can't clip.
- Both legs land in a `MixingSampleProvider` with **`ReadFully = true`**. This is mandatory:
  WASAPI loopback delivers **no buffers at all while nothing is playing**, so the silent leg
  must be zero-filled instead of stalling the mix.
- For the same reason the drain cannot be paced by "bytes available" — `PumpMixer` uses a
  `Stopwatch` and reads exactly as many samples as wall-clock time says should exist, in 200 ms
  chunks with a 50 ms sleep. Draining faster than real time would empty the buffers and record
  silence.
- Output goes through `SampleToWaveProvider16` into a `WaveFileWriter`. 16 kHz mono 16-bit is
  exactly what Whisper wants, so nothing gets resampled a second time downstream.
- `StopRecordingAsync` stops both captures, waits 150 ms for in-flight buffers, then cancels and
  awaits the pump before flushing — only the pump ever touches `_writer`, so there's no lock.

Verified end-to-end with a throwaway harness: a 440 Hz tone played during a 4 s "both" take
produced a 16 kHz/mono/16-bit WAV of 4.15 s with peak 0.240 (0.3 tone × 0.5 downmix × 2
channels × 0.8 gain) — correct format, correct pacing, no clipping.

---

## Critical WinUI 3 2.1.x quirks

1. `Window.Resources` does not exist → use `Grid.Resources` on root element
2. `{x:Bind}` does not work on `Window` → use code-behind `PropertyChanged` handler
3. `IsChecked="True"` on RadioButton/ToggleButton in XAML → `XamlParseException` → set in code-behind after `InitializeComponent()`
4. `Slider Minimum/Maximum` in XAML → `XamlParseException` → set in code-behind
5. `[LibraryImport]` needs `EntryPoint="GetWindowLongW"` for A/W variant Win32 functions
6. `StringBuilder` not supported in `[LibraryImport]` → use `[DllImport]` for `GetWindowText`
7. **WebView2 does not work in WS_EX_LAYERED windows** (the Box) → Browser mode was removed
8. **Exceptions that escape a WndProc crash the CLR** with `ExecutionEngineException` → always try/catch in every WndProc
9. **`_populatingUi` flag is CRITICAL** → slider `ValueChanged` overwrites config during UI init → guard all handlers with `if (_populatingUi) return;`
10. **Unpackaged windows don't inherit the exe icon** → an `<ApplicationIcon>` in the csproj alone is not enough; each `Window` must call `AppWindow.SetIcon(path)` itself (see `WindowService.SetWindowIcon`) or the taskbar/title bar/Alt-Tab show a generic icon
11. **A root `Grid`/`Page` with no explicit `Background` renders solid black** when nothing else covers it — harmless-looking in a permanently-dark app, but breaks a runtime Light/Dark theme switch (NavigationView's top pane and any gaps around content stay black while cards correctly re-theme). Always set an explicit `Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"` (or similar) on the window's root element. Also: don't set `RequestedTheme` on both the root **and** a child `NavigationView` — that split the NavView's own chrome from its Content onto two different themes; set it only on the root and let it cascade (see "Theme picker" section above)

---

## App Stealth (key innovation)

`WindowEmbedService.Embed(targetHwnd, title, x, y, w, h)`:
1. Saves target window style + rect
2. Creates plain Win32 container (`WS_CAPTION | WS_SYSMENU | WS_THICKFRAME`)
3. Applies `WDA_EXCLUDEFROMCAPTURE` + always-on-top to container
4. Strips chrome from target (`~WS_CAPTION & ~WS_THICKFRAME`)
5. `SetParent(targetHwnd, containerHwnd)` → fills client area
6. On `WM_SIZE` → `MoveWindow` to resize embedded window
7. On `WM_CLOSE` / `Dispose()` → restores original parent, style, position

Works well with: Win32, WPF, WinForms, older Electron apps
Limited for: Chrome/Edge/modern Chromium (DirectComposition surfaces bypass the container)

---

## Status

**Recently added / changed**
- Full VC++ Redistributable app-local deployment, including `vcomp140.dll` (OpenMP, confirmed via
  PE import-table analysis to be a genuine static dependency of `ggml-whisper.dll`) — see the
  dependency note below.
- Taskbar/title bar/Alt-Tab icon (`WindowService.SetWindowIcon`, called from both windows'
  `OnFirstActivated`) + `<ApplicationIcon>` in the csproj — unpackaged WinUI 3 windows do not pick
  up the exe's icon on their own.
- Box screen-share protection is toggleable at runtime: Controller footer's **Hide Box** button
  (`ControllerViewModel.OverlayProtected` → `OverlayProtectionChanged` event →
  `WindowService.SetContentProtection` on the Box's hwnd).
- Box always reopens at the primary monitor's top-left corner (`0,0`) on app launch instead of
  restoring the last saved X/Y (`OverlayWindow.OnFirstActivated` / `SaveGeometry`). A saved position
  from a disconnected monitor or a different multi-monitor arrangement could put it fully off-screen
  with no visible way to find it. Size is still restored, and moving/hiding/showing the Box within a
  single running session still keeps its position (only re-launching the app resets it to `0,0`).
- In-app update check + one-click install from the About tab (`UpdateService` +
  `AboutTabViewModel.CheckForUpdatesCommand` / `DownloadAndInstallCommand`) — see the dedicated
  section above.
- Hotkey set reworked: dropped Cycle Mode (Ctrl+Alt+M) and the original Bring Controller
  Forward binding of Ctrl+Alt+,; added Increase/Decrease Opacity (Ctrl+Alt+]/[), Release
  Stealth Container (Ctrl+Alt+U), toggles for Box Visibility (Ctrl+Alt+V), Box Capture
  Protection (Ctrl+Alt+S), and Controller Capture Protection (Ctrl+Alt+H).
- Per-mode scroll controls (Manual/Auto/Voice each get their own, only one shown at a
  time), a decoupled + fixed Voice scroll speed, a live font-size bug fix, a full card UI
  redesign across every Controller tab, and 4 more hotkeys (Ctrl+Alt+./,/=/- for Auto-scroll
  speed and font size) — see the "Scroll modes" and "Card UI" sections above.
- Footer toggles collapsed into a single row (3 Box controls left, Hide Controller right) with
  static labels + tooltips, and a System/Light/Dark theme picker added to the Settings tab — see
  the "Theme picker" section above, including the root-Grid-background rendering bug it surfaced.
- v1.1.0: full "Box"/"onAIr" terminology rebrand across UI and docs, config folder migrated to
  `%LocalAppData%\onAIr\` (auto-migrates existing installs), all references to the original
  onAIr Electron project removed from the docs — see "Footer reorg + Box/onAIr rebrand" above.

**Deliberately not doing**
- `.txt` shell association ("Open with onAIr") — dropped, not worth the registry surface.
- Silent/unattended auto-update — see the "In-app update check" section above for why.
- Renaming internal class/file/namespace/config-field names to match the "Box" terminology — see
  the "Terminology" section near the top of this file.

**Deferred**
- Automated tests and CI. There is no test project and no GitHub Actions workflow; validation is
  currently `dotnet build` plus manual smoke tests.

---

## Dependency checklist (why "installs fine, doesn't open" happens)

The app is unpackaged + self-contained, so nothing beyond Windows 10 2004+ should be required —
but three separate native layers each need their own files bundled, and missing any one of them
reproduces the "installer runs, app never opens (or Whisper silently fails)" symptom with no
visible error:

1. **.NET 8 runtime** — `SelfContained=true`, bundled automatically by `dotnet publish --self-contained true`.
2. **Windows App SDK (WinUI 3) native runtime** — `WindowsAppSDKSelfContained=true` in the csproj
   deploys `Microsoft.WindowsAppRuntime.dll` / `.Bootstrap.dll` app-local instead of depending on
   the machine-wide `Microsoft.WindowsAppRuntime.2` MSIX framework package. On managed/corporate
   machines the elevated installer can register that package for the *admin* account that approved
   UAC rather than the logged-on user, or sideloading can be blocked by policy — either way you get
   a `XamlParseException` before the first window shows. The installer *also* runs
   `WindowsAppRuntimeInstall-x64.exe --quiet --force` as a belt-and-suspenders fallback, but the
   self-contained deploy is what actually fixes it on locked-down machines.
3. **VC++ Redistributable (native DLLs)** — WinUI 3's native components, and `whisper.dll` /
   `ggml-whisper.dll` (whisper.net's native inference engine), are compiled against the MSVC
   runtime and do **not** bundle it themselves. The `CopyVCRuntime` MSBuild target in
   `OnAirNative.csproj` copies these app-local after every build/publish:
   `vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll`, `msvcp140_1.dll`, `msvcp140_2.dll`,
   `msvcp140_atomic_wait.dll`, `msvcp140_codecvt_ids.dll`, `concrt140.dll`, `vcomp140.dll`.
   - The `msvcp140_*`/`concrt140` satellite DLLs don't show up in any static import table (verified
     with `pefile`) — `msvcp140.dll` loads them via `LoadLibrary` on demand for specific STL features
     (threads, atomics, codecvt), so they're bundled defensively per Microsoft's own redistribution
     guidance.
   - `vcomp140.dll` (OpenMP) **is** a confirmed static import of `ggml-whisper.dll` — without it,
     Whisper transcription fails to initialise on a machine without the VC++ Redistributable already
     installed. It ships in a separate redist subfolder (`Microsoft.VC143.OpenMP`, not
     `Microsoft.VC143.CRT`), which is why it's easy to miss if you only copy the CRT folder.
   - Source: `$(VCToolsRedistDir)` when a full VS install is present, else the running machine's own
     `System32` copy — so **build on a machine that has the VC++ Redistributable installed**
     (a bare .NET SDK/Windows SDK Build Tools install may be missing some of these files, which
     would silently under-bundle them for everyone who installs the app).

If you ever suspect a new missing native dependency, don't guess — scan the publish output's import
tables (`pip install pefile`, then walk every `.dll`/`.exe` under the publish folder collecting
`IMAGE_DIRECTORY_ENTRY_IMPORT` DLL names) and diff against what's actually bundled.

---

## Config location

`%LocalAppData%\onAIr\config.json`

On first launch after upgrading from a version older than 1.1, if this file doesn't exist yet but
`%LocalAppData%\onAIr Native\config.json` (the pre-1.1 folder name) does, it's copied over
automatically (`ConfigService` constructor) — the old file is left in place, untouched, as a safety
net. Diagnostic logs do **not** migrate (transient debug output, not settings): `launch.log`,
`overlay-init.log`, `controller-init.log`, `tray.log`, `crash.log` in the same folder simply start
fresh.

Notable values:
- `voiceRmsThreshold`: 5.0 (lowered from 15 — easier to trigger voice scroll; this is the
  *detection sensitivity*, not scroll speed)
- `voiceScrollSpeed`: 50 (1-100, independent from `scrollSpeed`/Auto mode — how fast Voice
  mode scrolls once triggered)
- `theme`: `"System"` by default — `"System"` | `"Light"` | `"Dark"`
- `overlayProtected`: true by default (Box hidden from screen capture); toggle at runtime via the
  Controller footer's **Hide Box** button
- `controllerProtected`: false
- `audioRecordingSource`: `microphone` | `system` | `both`

API keys are stored with a `dpapi:v1:` prefix; plain-text values from older builds are migrated
on the next save.

---

## Release checklist

1. Bump `AboutTabViewModel.Version` and `PRODUCT_VERSION` in `installer/onair-native.nsi`.
2. `dotnet publish -c Release -r win-x64 --self-contained true -p:WindowsPackageType=None -o ..\dist\publish-current`
3. `& "C:\Program Files (x86)\NSIS\makensis.exe" installer\onair-native.nsi`
   (needs `installer/redist/WindowsAppRuntimeInstall-x64.exe` — see `installer/README.md`)
4. `gh release create vX.Y.Z` and attach the setup `.exe`.

**Asset retention policy:** by default, older releases' installer `.exe` assets get stripped
(keeping only the latest) to save space — but this is a judgment call per release, not an automatic
rule. When in doubt, ask before deleting a previous release's installer asset.
