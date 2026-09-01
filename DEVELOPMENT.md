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
  # exe: OnAirNative\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\OnAirNative.exe
  ```
  The project is x64-only (`<Platforms>x64</Platforms>` in the .csproj, self-contained win-x64
  deploy), and the .csproj now forces `$(Platform)=x64` even when a build doesn't specify it
  explicitly — see the "Build output: two output folders" pitfall below for why that matters.
- **Current version:** 1.2.0 — bump it in **three** places, they must stay in sync:
  `OnAirNative/ViewModels/AboutTabViewModel.cs` (`Version`),
  `installer/onair-native.nsi` (`PRODUCT_VERSION`), and this file's version line.
- **Releases:** https://github.com/souz4rafael/onair-native/releases (latest published: v1.1.0 —
  v1.2.0 not yet released as of this doc update)
- **Stack:** WinUI 3 (Windows App SDK 2.1.3), .NET 8, NAudio 2.2.1, whisper.net 1.7.3,
  CommunityToolkit.Mvvm 8.3.2, System.Security.Cryptography.ProtectedData 8.0.0, plus two sibling
  companion projects: `streamdeck-plugin/` (Node/TS, Elgato Stream Deck plugin) and
  `mcp-server/` (C# console app, Model Context Protocol server) — see "Remote control" below.

---

## Terminology

User-facing vocabulary, since it's a common source of confusion if you're skimming code vs. UI:

- **The TP** — short for **Teleprompter**, what the UI calls the transparent, always-on-top
  teleprompter window (called "the Box" before this rename — see the "Status" section below).
  The underlying class is still `OverlayWindow`/`OverlayViewModel` internally (renaming the
  class/file names, XAML `x:Name`s, config field names like `OverlayProtected`, and hotkey action
  identifiers was deliberately **not** done — no user-visible benefit, high regression risk, and it
  would have broken existing installs' persisted `config.json` field names). Only user-facing
  strings (button labels, tooltips, About-tab text, docs) say "TP".
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
    ├── OverlayWindow        — the TP: transparent, frameless, always-on-top (hidden by default)
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

Tab selector is a custom segmented "pill" bar (not `NavigationView` — replaced in v1.2.0), 5
`RadioButton`s styled as pills: the selected tab shows icon+text and grows to fit (`Auto` grid
column), the other 4 collapse to icon-only and shrink to match — see "Pill segmented tab bar"
below for the two real WinUI bugs this surfaced.

| Tab | Key features |
|-----|-------------|
| **Script** | Load .txt, Manual/Auto/Voice scroll (one mode-specific speed control shown at a time), font size/TP opacity/color (+ custom hex, populated by clicking any preset swatch), font family picker, save/reset settings — organised into cards |
| **Q&A** | Record button, chat/transcription provider selection (2 dropdowns) + Test connection, Whisper model path, system prompt — organised into cards. Provider credentials are configured from Settings → AI PROVIDERS, not here (see below) |
| **App Stealth** | Embed any Win32 app in WDA_EXCLUDEFROMCAPTURE container (interactive!) — single card |
| **Settings** | Audio device selector + live mic level test, capture source (mic / system / both), 6 AI provider cards (configure/test independently), voice threshold slider, System/Light/Dark theme picker, Remote Control (Stream Deck + MCP) server toggle — organised into cards |
| **About** | Version, hotkeys, GitHub link, update check/install — Updates and Keyboard Shortcuts organised into cards |

**Footer:** a 2-column layout — the 3 TP controls (**Open TP** visible/hidden ·
**Lock TP** locked/unlocked · **Hide TP** visible/hidden in share, `OverlayProtected`) on the
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
| Ctrl+Alt+Home | Lock / unlock TP (Move Mode) |
| Ctrl+Alt+R | Q&A record start/stop |
| Ctrl+Alt+O | Open file picker |
| Ctrl+Alt+] / [ | Increase / decrease TP opacity (`ControllerWindow.AdjustOpacity`) |
| Ctrl+Alt+V | Open / hide TP (`ControllerWindow.ToggleOverlayVisibility`) |
| Ctrl+Alt+S | Hide / show TP in share (`ToggleOverlayCaptureProtection`) |
| Ctrl+Alt+H | Hide / show Controller in share (`ToggleControllerCaptureProtection`) |
| Ctrl+Alt+U | Release the App Stealth container, if embedded (`ReleaseStealthContainer`) |
| Ctrl+Alt+. / , | Increase / decrease Auto-scroll speed (`ControllerWindow.AdjustScrollSpeed`) |
| Ctrl+Alt+= / - | Increase / decrease font size (`ControllerWindow.AdjustFontSize`) |
| Ctrl+Alt+Up / Down | Increase / decrease Voice scroll speed, Voice mode (`ControllerWindow.AdjustVoiceScrollSpeed`) |
| Ctrl+Alt+Right / Left | Increase / decrease scroll step, Manual mode (`ControllerWindow.AdjustScrollStep`) |
| Ctrl+Alt+' / ; | Increase / decrease Voice scroll sensitivity (`ControllerWindow.AdjustVoiceThreshold`) |

Removed in an earlier pass: Ctrl+Alt+M (cycle Script↔Q&A) and the *original* binding of
Ctrl+Alt+, (bring Controller to front) — both dropped per request, and
`OverlayViewModel.CycleMode()` was deleted as dead code along with them.
`WindowService.BringToFront` itself is untouched and still used by the tray's "Show
Controller" menu item and double-click. Ctrl+Alt+, was later reassigned to Decrease
Auto-scroll speed — freed keys get reused rather than left permanently retired.

Hotkeys are registered on a dedicated background thread that owns its own message loop
(`HotkeyService.HotkeyLoop`). IDs are contiguous (`ID_SCROLL_UP`..`ID_VOICE_THRESHOLD_DOWN`)
and the cleanup loop unregisters the whole range — keep new IDs inside it.

The toggle-style hotkeys (TP visibility, TP/Controller capture protection) flip the
corresponding footer `ToggleButton.IsChecked` rather than duplicating logic — this reuses
the existing `Checked`/`Unchecked` handlers so the hotkey and a manual click can never fall
out of sync. The Lock/Unlock footer button follows the same rule: its `PropertyChanged` sync
(for Ctrl+Alt+Home) sets `LockToggle.IsChecked` instead of only updating its label —
previously a hotkey-driven lock/unlock left `IsChecked` stale, so the *next manual click*
silently re-affirmed the current state instead of flipping it. The opacity/scroll-speed/
font-size/voice-scroll-speed/scroll-step "Adjust*" methods all follow one more shared pattern:
compute the new value, set the ViewModel property (which persists to config and pushes the
change to the TP), then sync the on-screen slider under the `_populatingUi` guard so it
doesn't fire a second, redundant write. **`AdjustVoiceThreshold` is the one exception** —
`VoiceRmsThreshold` (a `double`) isn't backed by a ViewModel property, so it writes straight
to `App.Config.Current.Appearance.VoiceRmsThreshold`, updates the `VoiceThresholdValue` label
text and calls `App.Config.Save()` itself, mirroring `VoiceThresholdSlider_ValueChanged`'s
existing direct-write pattern rather than the ViewModel one.

Six new hotkeys were added for Stream Deck dial mapping (Voice scroll speed, Manual scroll
step, Voice scroll sensitivity) using Ctrl+Alt+Up/Down/Left/Right and Ctrl+Alt+'/; — chosen
because directional keys map naturally to a physical dial twist. Trade-off accepted knowingly:
on some machines, Ctrl+Alt+Arrow is also bound by legacy Intel/NVIDIA graphics driver control
panels to rotate the display. `RegisterHotKey`'s return value isn't checked or logged, so if
that binding claims the combo first, onAIr's arrow hotkeys simply won't register — no error,
no crash, they just silently don't fire. Hasn't been observed in testing on this dev machine;
worth knowing if a user reports Ctrl+Alt+Up/Down/Left/Right doing nothing. All 6 new hotkeys
were verified end-to-end (real `SendKeys`-simulated key combos + UI Automation slider reads):
Ctrl+Alt+Right/Left correctly moved and clamped `ScrollStepSlider` (20↔400 range, step 20),
Ctrl+Alt+Up/Down correctly moved `VoiceScrollSpeedSlider` (1-100 range, step 5), and
Ctrl+Alt+'/; correctly moved `VoiceThresholdSlider` and its label (1-50 range, step 2.0) —
plus a regression check confirmed a pre-existing hotkey (Ctrl+Alt+]) still works after the
ID range/cleanup-loop change.

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

Don't confuse the mechanism above (how far/fast the script moves while a mode is active) with
*tuning the setting itself* — all three config fields, plus Voice's sensitivity threshold, now
have their own dedicated global hotkeys too (`ScrollStep`: Ctrl+Alt+Right/Left, `ScrollSpeed`:
Ctrl+Alt+./,, `VoiceScrollSpeed`: Ctrl+Alt+Up/Down, `VoiceRmsThreshold`: Ctrl+Alt+'/;) — see
"Global hotkeys" above.

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
| **Script** | SCRIPT FILE · SCROLL MODE · SCROLL STEP (PX)/AUTO-SCROLL SPEED/VOICE SCROLL SPEED (only the active mode's card is shown) · APPEARANCE (font size/TP opacity/color) · VIRTUAL SCROLL BUTTONS |
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

## Live transcript preview + the local-Whisper-never-loaded bug

While recording a Q&A question with a local Whisper model configured, the Box now shows a
live, still-growing "Live preview: …" line a few seconds behind what's actually been said —
implemented as `OverlayViewModel._livePreviewTimer` (2.5s tick) calling the new
`AudioService.PeekRecordedAudio()` (a lock-guarded snapshot of the in-progress recording
buffer, flushed but not stopped) through `WhisperService.TranscribeAsync` (reused as-is), with
`LivePreviewText` bound to a new `TextBlock` in `OverlayWindow.xaml`. Only runs when
`WhisperService.IsLocalModelLoaded` — re-transcribing on a timer against a cloud API would burn
through rate limits/cost for no benefit. Cleared and stopped the moment recording stops
(`ToggleRecordingAsync`), right before the real, full-buffer transcription replaces it.

**Design iteration — trailing window → full growing buffer.** The first version windowed
`PeekRecordedAudio` to a trailing slice (18s, then 8s) of audio, re-transcribing only that
slice each tick, to keep per-tick cost roughly constant regardless of recording length. In
practice this made the preview jump between disjoint fragments and erase whatever was shown a
moment before, instead of reading like an accumulating transcript — not what "live preview"
implies. Switched to re-transcribing the *entire* buffer captured so far on every tick
(`PeekRecordedAudio()` now takes no window parameter, just returns the whole flushed buffer —
already a complete valid WAV, so the windowing's `WaveFileReader`/`WaveFileWriter` re-encoding
was removed entirely, not just disabled). Cost now grows with recording length instead of
staying constant, but for this feature's actual use case — a few seconds to maybe half a
minute of a spoken question — that's the right trade-off for a preview that behaves like
users expect.

**Model-speed ceiling, confirmed with real hardware data.** A live preview is only useful if a
tick's transcription finishes faster than the recording itself — a late-arriving result is
discarded once recording has stopped (`if (result.Success && IsRecording)` in
`LivePreviewTick`), so a too-slow model means the preview simply never appears, and only the
final post-stop transcription (unaffected either way) shows up. Confirmed on real (CPU-only,
no GPU) dev hardware: `ggml-medium.bin` (1.5GB) took 5+ minutes to transcribe ~106s of audio in
one test — hopelessly too slow for a live preview of a 5-20s question. Retested after
switching to smaller models: **`tiny` and `base` worked well without needing quantized
variants**; `small` is presumably borderline (untested); `medium`/`large` confirmed
impractical for this feature specifically (the *final*, post-recording transcription is
unaffected by any of this — it's not time-critical the same way). This is now documented for
users in the README's "Whisper local model" section.

**Refined cost/benefit, from further real-world testing:** the best combination turned out to
be `ggml-tiny` **unquantized** plus `ggml-base` with **q5_1** quantization — quantizing `tiny`
wasn't worth the accuracy trade-off (already small/fast enough), but `q5_1` on `base` gave a
meaningfully smaller/faster model without a noticeable accuracy hit. Also added to the README.

**The bug that made all of the above impossible to see at first: `WhisperModelPath` was never
actually loaded.** `WhisperService.LoadModelAsync(path)` existed and worked correctly, but
nothing in the entire codebase ever called it — `AiTabViewModel.WhisperModelPath` was faithfully
read from and written to `config.json` on every change, but that's all that ever happened to
it. `WhisperService.IsLocalModelLoaded` was therefore always `false` regardless of a
configured path, so the app silently used the cloud API forever, even for users who'd set a
local model path expecting in-process transcription. Fixed by giving `AiTabViewModel` a
`WhisperService` dependency (threaded through `ControllerViewModel`'s constructor) and calling
`LoadModelAsync` from `OnWhisperModelPathChanged`, debounced 600ms so typing a path
character-by-character doesn't try to load a multi-hundred-MB file on every keystroke — and
also called once on startup, since assigning `WhisperModelPath` from the loaded config in the
constructor fires the same partial method. Added a `WhisperModelStatus` observable
("Loading model…" / "✓ Model loaded" / "⚠ File not found" / "⚠ Failed to load model") surfaced
in a new `TextBlock` under the Q&A tab's Whisper model path box, so this is visible instead of
silently failing the same way again in the future.

**A native crash this surfaced, unrelated to the two bugs above.** Once local models were
actually loading, concurrent access to whisper.net's native `WhisperProcessor` — a live-preview
tick's transcription racing the final post-recording transcription — crashed the whole process
natively: no managed exception, nothing in `crash.log`, nothing in `launch.log`'s
`UnhandledException` handler, just silent process death. `WhisperProcessor` isn't safe to
invoke concurrently from two callers on the same instance. Fixed with a `SemaphoreSlim(1, 1)`
gate around the entire body of `WhisperService.TranscribeAsync`, serializing every call —
local or cloud — through it; there's no benefit to parallelising the cloud HTTP path either.
`AudioService.PeekRecordedAudio` also picked up an `ObjectDisposedException` catch for the same
underlying race (recording can stop and dispose `_writer`/`_buffer` between the `_recording`
check and the buffer read, since a live-preview tick runs concurrently with the UI thread that
owns recording start/stop).

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

## Remote control: Stream Deck plugin + MCP server

Two independent client apps remote-control onAIr through the exact same local server —
`OnAirNative/Services/RemoteControlService.cs`, a loopback-only (`127.0.0.1`, port 47823)
`HttpListener`-based WebSocket server, gated by a single toggle in Settings → **REMOTE CONTROL**
(`AppConfig.RemoteControlEnabled`). No pairing token — the trust boundary is "any process running
as this Windows user", same as the global hotkeys themselves.

### Protocol

Newline-delimited JSON per WebSocket text frame:

```
client → onAIr:
  {"op":"command","action":"ToggleOverlayVisibility"}     — fires a HotkeyAction (toggle/one-shot)
  {"op":"adjust","action":"IncreaseOpacity"}               — same as "command", relative step
  {"op":"getState"}                                        — triggers a broadcast to all clients
  {"op":"set","id":"1","field":"FontSize","value":24}      — absolute setter (MCP-only need)
  {"op":"loadScript","id":"1","path":"C:\\scripts\\a.txt"} — load by path, no file-picker UI
  {"op":"getScriptText","id":"1"}
  {"op":"listFonts","id":"1"}

onAIr → client:
  {"op":"state","data":{ ...RemoteState fields... }}       — broadcast to ALL clients (on connect,
                                                              after every action, and a 2s safety
                                                              timer for changes made via mouse click)
  {"op":"result","id":"1","success":true|false,"error":"...","data":...}
                                                              — reply to the ONE requesting client,
                                                              only for set/loadScript/getScriptText/
                                                              listFonts (command/adjust/getState
                                                              stay fire-and-forget, unchanged
                                                              from the original Stream Deck design)
```

`command`/`adjust`/`getState` reuse the existing `HotkeyAction` enum (`HotkeyService.cs`) — the
same vocabulary shared with physical global hotkeys, dispatched through `App.ExecuteAction`. The
`set`/`loadScript`/`getScriptText`/`listFonts` ops were added for the MCP server (v1.2.0) because
`HotkeyAction` only has parameterless relative Increase/Decrease actions — useless for an LLM
tool call like "set font size to 24". Each setter in `ControllerWindow.SetRemoteField(field,
JsonElement value)` re-applies the SAME validation/clamp the real UI control uses (reads the
live slider `Minimum`/`Maximum`, the hex-color regex, the installed-fonts list), so a value
accepted remotely is guaranteed consistent with what the Settings/Appearance UI would allow.

### Stream Deck plugin (`streamdeck-plugin/`)

Node/TypeScript, official `@elgato/streamdeck` SDK v2, UUID `com.souz4rafael.onair`. 16 actions:
5 toggles (Open/Hide TP, Lock/Unlock TP, Hide TP in Share, Hide Controller in Share, Start/Stop
Recording), 1 clickable AI Status tile (provider + Whisper local/cloud + recording — press
triggers `RecheckWhisperModel`), 4 momentary actions (Release Stealth, Open File, Scroll Up/Down),
6 dial-capable actions (`Controllers: ["Keypad","Encoder"]` so they also work as plain buttons —
Opacity, Font Size, Auto-Scroll Speed, Voice Scroll Speed, Manual Scroll Step, Voice Sensitivity).

Build/package workflow:
```powershell
cd streamdeck-plugin
npm install
npm run build                       # rollup -> com.souz4rafael.onair.sdPlugin/bin/plugin.js
streamdeck validate com.souz4rafael.onair.sdPlugin
streamdeck pack com.souz4rafael.onair.sdPlugin -o dist -f
# then copy dist\com.souz4rafael.onair.streamDeckPlugin -> ..\OnAirNative\Assets\onair-remote.streamDeckPlugin
```
The `.csproj`'s `<Content Include="Assets\onair-remote.streamDeckPlugin">` bundles that packaged
file so Settings → REMOTE CONTROL → **Install Stream Deck Plugin** hands it to the Stream Deck
app (`Process.Start` with `UseShellExecute=true` on the registered `.streamDeckPlugin` file
association) without requiring Node.js on the end user's machine.

Real bugs found/fixed building this (all confirmed on real Stream Deck+ hardware): a genuine
naming mixup ("Hide TP" was wired to the wrong action), `ShowTitle` is a per-STATE manifest
property (not per-action) so the in-app "hide title" toggle only ever affected whichever state
was visible at edit time — fixed by defaulting `"ShowTitle": false` on every state plus a
`setState()` memoization fix in `toggle-action-base.ts` (was calling `setState` unconditionally
every 2s, which is itself what re-triggered the title). **Known remaining issue** (deferred,
worked around manually by clearing the title field in the Stream Deck UI): the title still
reappears on 3 specific dual-state toggles (Unlock TP/Show TP/Show Controller) despite the fix —
root cause not yet found; possibly a Stream Deck profile-caching quirk independent of the
manifest.

### MCP server (`mcp-server/`)

C# console app (`ModelContextProtocol` NuGet v2.2.0, official SDK, `net8.0`), stdio transport —
lets any MCP-aware AI client (Claude Desktop, VS Code Copilot Chat, etc.) control onAIr via
natural language. Standalone process, NOT embedded in `OnAirNative.exe` — connects to
`RemoteControlService` purely as a WebSocket client (`OnAirClient.cs`, `ClientWebSocket`,
request/response correlation via a client-generated `id` + `ConcurrentDictionary` of
`TaskCompletionSource`s), exactly like the Stream Deck plugin does from the Node side.

21 tools in `OnAirTools.cs` (`onair_get_state`, `onair_toggle_tp`, `onair_load_script`,
`onair_set_font_color`, `onair_set_scroll_mode`, etc. — full list mirrored in
`McpToolsDialog.xaml.cs`'s `Tools` array, which must stay in sync when a tool is
added/removed/renamed). Every tool is individually toggleable by the user (Settings → REMOTE
CONTROL → **MCP Tools & Setup…** dialog) — enforced by `ToolGate.cs`, which re-reads
`AppConfig.McpDisabledTools` straight from `config.json` on every single tool call (not cached),
so a toggle flipped in onAIr takes effect on the MCP server's very next call without restarting
that long-lived stdio process. A disabled tool always returns a clear "disabled in Settings"
message rather than silently no-oping.

**Security**: never expose provider API keys or raw `config.json` beyond the already-public
`RemoteState` fields — verified by grep during implementation; `RemoteControlService` itself
never sends credentials over the wire.

Build/package workflow — framework-dependent (not self-contained; fine since onAIr already
requires the .NET 8 x64 runtime to run at all):
```powershell
cd mcp-server
dotnet publish -c Release -o publish --self-contained false
# then copy publish\* (minus OnAirMcp.pdb) -> ..\OnAirNative\Assets\mcp-server\
```
Bundled the same way as the Stream Deck plugin (`<Content Include="Assets\mcp-server\**">` in
the `.csproj`) so **MCP Tools & Setup…**'s "Copy MCP Config" button can always point at a real,
working absolute path (`<app dir>\Assets\mcp-server\OnAirMcp.dll`) regardless of dev build vs.
installed copy. **Remember to republish + recopy after ANY mcp-server code change** — a stale
bundled copy silently keeps old behavior (this bit once during development: `ToolGate` was
added to the source but the bundled `.dll` was built before that, so gating appeared broken
when testing the bundled copy specifically, even though the dev build was already correct).

Tested via `npx @modelcontextprotocol/inspector --cli dotnet <path> -- --method tools/call
--tool-name <name>` (no GUI needed) — every tool, both success and validation-error paths, plus
graceful behavior when onAIr isn't running (clear error, no crash).

---

## AI provider settings redesign (v1.2.0)

**Real bug fixed**: the old single `ProviderConfigDialog` (opened via a "Configure provider…"
button in the Q&A tab) read/wrote whichever provider was selected in the **Chat provider**
dropdown — with Chat=Groq but Transcription=OpenAI, there was no way to ever configure OpenAI's
key without first switching the chat dropdown to OpenAI (an unwanted side effect just to edit a
credential), since Chat and Transcription are two independent selections
(`AiTabViewModel.SelectedChatProviderIndex`/`SelectedTranscriptionProviderIndex`).

Fixed by decoupling configuration from selection entirely:
- `ProviderConfigDialog` now takes an explicit `providerKey` constructor parameter instead of
  reading `_config.Current.Provider` — the provider to edit is fixed at construction time.
- Settings tab gained 6 separate provider cards (Azure/OpenAI/Groq/Anthropic/Gemini/Mistral),
  each showing a "✓ Configured" / "Not configured" status + a **Configure** button that opens
  the dialog for that specific provider, independent of either dropdown.
- The dialog itself gained its own **Test connection** button, calling
  `AiChatService.TestConnectionAsync(providerKey, snapshot)` — already provider-parameterized,
  it just wasn't being called that way. The snapshot is built from the CURRENTLY TYPED field
  values (`BuildConfigFromCurrentFields()`), not the saved config, so testing reflects what's on
  screen right now rather than stale saved credentials; Cancel truly discards unsaved edits since
  the snapshot is a throwaway `AppConfig`, never assigned into `_config.Current`.
- The Q&A tab's "Configure provider…" button was removed entirely; it now just has the two
  selection dropdowns + the existing **Test connection** button (tests whichever provider is
  currently selected for chat — unchanged behavior).

## Mic level test (Settings tab)

A **🎙 Test Microphone** toggle button + live RMS level meter in the Audio Source card, reusing
`AudioService.StartVoiceMonitor`/`StopVoiceMonitor` — the exact same plumbing behind the Script
tab's Voice scroll mode indicator — rather than a second capture path. `AudioService` only
supports one monitor at a time, so the test refuses to start (shows a warning instead) while
Voice scroll mode's own monitor is already running, rather than silently stealing it. Auto-stops
when navigating away from the Settings tab or closing the Controller window.

## Color swatch → hex box (v1.2.0)

Clicking a preset color swatch (White/Yellow/Green/Aqua/Orange/Pink) still applies it immediately
(unchanged), but now also populates the editable `CustomColorBox` hex field with that value (was
previously a separate read-only `FontColorIndicator` TextBlock, now removed) — lets you use a
preset as a starting point and fine-tune the exact shade from there instead of only seeing a
static readout.

---

## Critical WinUI 3 2.1.x quirks

1. `Window.Resources` does not exist → use `Grid.Resources` on root element
2. `{x:Bind}` does not work on `Window` → use code-behind `PropertyChanged` handler
3. `IsChecked="True"` on RadioButton/ToggleButton in XAML → `XamlParseException` → set in code-behind after `InitializeComponent()`
4. `Slider Minimum/Maximum` in XAML → `XamlParseException` → set in code-behind
5. `[LibraryImport]` needs `EntryPoint="GetWindowLongW"` for A/W variant Win32 functions
6. `StringBuilder` not supported in `[LibraryImport]` → use `[DllImport]` for `GetWindowText`
7. **WebView2 does not work in WS_EX_LAYERED windows** (the TP) → Browser mode was removed
8. **Exceptions that escape a WndProc crash the CLR** with `ExecutionEngineException` → always try/catch in every WndProc
9. **`_populatingUi` flag is CRITICAL** → slider `ValueChanged` overwrites config during UI init → guard all handlers with `if (_populatingUi) return;`
10. **Unpackaged windows don't inherit the exe icon** → an `<ApplicationIcon>` in the csproj alone is not enough; each `Window` must call `AppWindow.SetIcon(path)` itself (see `WindowService.SetWindowIcon`) or the taskbar/title bar/Alt-Tab show a generic icon
11. **A root `Grid`/`Page` with no explicit `Background` renders solid black** when nothing else covers it — harmless-looking in a permanently-dark app, but breaks a runtime Light/Dark theme switch (NavigationView's top pane and any gaps around content stay black while cards correctly re-theme). Always set an explicit `Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"` (or similar) on the window's root element. Also: don't set `RequestedTheme` on both the root **and** a child `NavigationView` — that split the NavView's own chrome from its Content onto two different themes; set it only on the root and let it cascade (see "Theme picker" section above)
12. **`VisualStateManager.VisualStateGroups` MUST be nested INSIDE a `ControlTemplate`'s root element**, not placed as an XAML sibling after the root closes — a sibling placement compiles fine and throws no error, but silently no-ops: none of the VisualStates (`Checked`, `PointerOver`, etc.) ever actually apply. Found building the v1.2.0 pill tab bar's custom `RadioButton` template — the selected tab's accent-color background never rendered at all until the VSG was moved to be the template root `Border`'s first property-element child (matching exactly how every real WinUI default control template structures it)
13. **Two `VisualStateGroup`s must never target the SAME property** — they're fully independent state machines; when one group transitions to a state with no matching Setter for that property, it silently reverts the property to its base XAML value with zero awareness of what another group's Setter currently wants there. Same pill tab bar: `CommonStates`'s `PointerOver`/`Normal` and `CheckStates`'s `Checked` both set `Border.Background` — moving the mouse away from an already-selected tab (`PointerOver` → `Normal`) wiped out the selection color, even though the tab was still logically checked. Fix: give each group a disjoint property (`Background` for selection, `BorderBrush` for hover)
14. **`Control`'s base style applies a `MinWidth="120"`** even underneath a fully custom `Style`+`ControlTemplate` that never mentions `MinWidth` itself — silently forces any small custom control (e.g. an icon-only "pill" segment meant to be ~36px) up to 120px. Must explicitly set `MinWidth="0"` (and `MinHeight="0"`) in the custom `Style` to override it
15. **A plain `Border`/`Grid`/`StackPanel` has NO UI Automation peer in WinUI** — querying it by `AutomationId` always returns "not found" regardless of its actual `Visibility`, even when it's genuinely on screen. Don't use that as an "is this visible" check when testing/automating; query a concrete CHILD control instead (a `TextBox`, `Button`, named `TextBlock`, etc.)

---

## Pill segmented tab bar (v1.2.0)

Replaced `NavigationView`'s top pane with a custom segmented "pill" control across the 5 main
tabs. Selected tab shows icon+text and grows to fit (`GridLength.Auto` column, driven purely by
content — no manual column-width bookkeeping in code); the other 4 collapse to icon-only and
shrink to match. `RepositionThemeTransition` on the pill `Grid` animates the resulting reflow.
Building this surfaced 3 genuine, non-obvious WinUI 3 bugs — see items 12–15 in "Critical WinUI 3
2.1.x quirks" above for the technical detail (VisualStateManager placement, disjoint
VisualStateGroup properties, the inherited `MinWidth=120`, and the StackPanel-has-no-UIA-peer
false alarm that cost debugging time before being recognized as not a bug).

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
- TP screen-share protection is toggleable at runtime: Controller footer's **Hide TP** button
  (`ControllerViewModel.OverlayProtected` → `OverlayProtectionChanged` event →
  `WindowService.SetContentProtection` on the TP's hwnd).
- TP always reopens at the primary monitor's top-left corner (`0,0`) on app launch instead of
  restoring the last saved X/Y (`OverlayWindow.OnFirstActivated` / `SaveGeometry`). A saved position
  from a disconnected monitor or a different multi-monitor arrangement could put it fully off-screen
  with no visible way to find it. Size is still restored, and moving/hiding/showing the TP within a
  single running session still keeps its position (only re-launching the app resets it to `0,0`).
- In-app update check + one-click install from the About tab (`UpdateService` +
  `AboutTabViewModel.CheckForUpdatesCommand` / `DownloadAndInstallCommand`) — see the dedicated
  section above.
- Hotkey set reworked: dropped Cycle Mode (Ctrl+Alt+M) and the original Bring Controller
  Forward binding of Ctrl+Alt+,; added Increase/Decrease Opacity (Ctrl+Alt+]/[), Release
  Stealth Container (Ctrl+Alt+U), toggles for TP Visibility (Ctrl+Alt+V), TP Capture
  Protection (Ctrl+Alt+S), and Controller Capture Protection (Ctrl+Alt+H).
- Per-mode scroll controls (Manual/Auto/Voice each get their own, only one shown at a
  time), a decoupled + fixed Voice scroll speed, a live font-size bug fix, a full card UI
  redesign across every Controller tab, and 4 more hotkeys (Ctrl+Alt+./,/=/- for Auto-scroll
  speed and font size) — see the "Scroll modes" and "Card UI" sections above.
- Footer toggles collapsed into a single row (3 TP controls left, Hide Controller right) with
  static labels + tooltips, and a System/Light/Dark theme picker added to the Settings tab — see
  the "Theme picker" section above, including the root-Grid-background rendering bug it surfaced.
- **v1.1.0 (released):** full "Box"/"onAIr" terminology rebrand across UI and docs, then a further
  "Box" → "TP" (Teleprompter) rename (same user-facing-strings-only pattern, no internal
  class/config-field renames — see "Terminology" above); config folder migrated to
  `%LocalAppData%\onAIr\` (auto-migrates existing installs); live transcript preview while
  recording a Q&A question (local Whisper only, growing-buffer redesign, plus a real fix for
  `WhisperModelPath` never actually being loaded into `WhisperService` and a native-crash fix for
  concurrent whisper.net calls — see "Live transcript preview" above); 6 new global hotkeys
  (Ctrl+Alt+Up/Down/Right/Left/'/;) for settings that previously had none, added ahead of the
  Stream Deck plugin so every dial-style setting has a keyboard fallback.
- **v1.2.0 (this session, not yet released):** the entire "Remote control" feature set —
  `RemoteControlService`, the Stream Deck plugin (16 actions, full icon redesign, several real
  bugfixes), and the new MCP server (21 tools, per-tool enable/disable, "MCP Tools & Setup…"
  dialog) — see "Remote control: Stream Deck plugin + MCP server" above. Plus: a custom pill
  segmented tab bar replacing `NavigationView` (surfaced 3 genuine WinUI 3 bugs — see quirks list
  above); the AI provider settings redesign (6 independent per-provider cards in Settings,
  decoupled from the Chat/Transcription dropdowns — a real bug fix, not just a refactor); a live
  mic level test in the Settings tab; and preset color swatches now populate the editable hex box
  instead of a separate read-only label.
- **Known, deferred issue:** the Stream Deck plugin's title-reappearing bug (see "Remote control"
  above) is only partially fixed — 3 specific dual-state toggle keys (Unlock TP/Show TP/Show
  Controller) still show their title once activated, worked around manually for now by the user
  clearing the title field in the Stream Deck app itself. Root cause not yet found.

**Deliberately not doing**
- `.txt` shell association ("Open with onAIr") — dropped, not worth the registry surface.
- Silent/unattended auto-update — see the "In-app update check" section above for why.
- Renaming internal class/file/namespace/config-field names to match the "TP" terminology — see
  the "Terminology" section near the top of this file.

**Deferred**
- Automated tests and CI. There is no test project and no GitHub Actions workflow; validation is
  currently `dotnet build` plus manual smoke tests (and, for the MCP server, `npx
  @modelcontextprotocol/inspector --cli`).
- The Stream Deck title-reappearing bug above.

---

## Build output: two output folders (a real trap, fixed)

The `.csproj` declares `<Platforms>x64</Platforms>` (the app is x64-only — self-contained win-x64
deploy for WinUI 3), but for a while nothing forced MSBuild's resolved `$(Platform)` to actually
be `x64` when a build didn't pass it explicitly. MSBuild's own default is `AnyCPU`, and since
`AnyCPU != x64`, that produces a **second, completely separate output folder**:
`bin\Debug\net8.0-windows10.0.19041.0\win-x64\` (no `x64` segment) sitting right next to the real
one, `bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\` — both contain a runnable
`OnAirNative.exe`, and neither errors or warns about the other's existence.

**This bit us for real**: an early `dotnet build OnAirNative\OnAirNative.csproj` (no `-p:Platform`,
matching what this very file's build instructions used to say) landed in the no-`x64` folder, and
every subsequent `dotnet build OnAirNative.sln` (which *does* resolve `x64` correctly, since the
.sln records per-project platform mappings) kept updating the *other* one. Result: testing via a
shortcut/manually-launched exe in the stale folder showed old behavior (missing hotkeys, old
terminology) long after the source had moved on — with a fully valid, launchable exe and no error
of any kind to suggest why.

**Fixed** by adding `<Platform Condition="'$(Platform)' == '' Or '$(Platform)' == 'AnyCPU'">x64</Platform>`
to the `.csproj`, so *any* build front-end (bare `dotnet build` on the `.csproj`, `dotnet build` on
the `.sln`, Visual Studio with "Any CPU" left selected in the platform dropdown, raw `MSBuild.exe`)
now resolves to the same `bin\x64\Debug\...` output. The stale `bin\Debug`/`obj\Debug`/`bin\Release`/
`obj\Release` (no-platform) folders were deleted; verified both `dotnet build OnAirNative.csproj`
and `dotnet build OnAirNative.sln` now write to the identical path.

**Rule of thumb going forward**: the only correct dev/test exe path is
`OnAirNative\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\OnAirNative.exe`. If a build or a
test run ever behaves like it's ignoring recent changes, check for a stray `bin\Debug` (no `x64`)
folder before assuming the code change itself is broken.

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
- `overlayProtected`: true by default (TP hidden from screen capture); toggle at runtime via the
  Controller footer's **Hide TP** button
- `controllerProtected`: false
- `audioRecordingSource`: `microphone` | `system` | `both`

API keys are stored with a `dpapi:v1:` prefix; plain-text values from older builds are migrated
on the next save.

---

## Release checklist

1. Bump `AboutTabViewModel.Version` and `PRODUCT_VERSION` in `installer/onair-native.nsi`.
2. If `streamdeck-plugin/` or `mcp-server/` changed since the last release, rebuild + recopy their
   bundled assets FIRST (a stale bundled copy silently keeps old behavior — bit once already):
   - `streamdeck-plugin/`: `npm run build` → `streamdeck pack com.souz4rafael.onair.sdPlugin -o dist -f`
     → copy `dist\com.souz4rafael.onair.streamDeckPlugin` → `OnAirNative\Assets\onair-remote.streamDeckPlugin`
   - `mcp-server/`: `dotnet publish -c Release -o publish --self-contained false` → copy
     `publish\*` (minus `.pdb`) → `OnAirNative\Assets\mcp-server\`
3. `dotnet publish -c Release -r win-x64 --self-contained true -p:WindowsPackageType=None -o ..\dist\publish-current`
4. `& "C:\Program Files (x86)\NSIS\makensis.exe" installer\onair-native.nsi`
   (needs `installer/redist/WindowsAppRuntimeInstall-x64.exe` — see `installer/README.md`)
5. `gh release create vX.Y.Z` and attach the setup `.exe`.

**Asset retention policy:** by default, older releases' installer `.exe` assets get stripped
(keeping only the latest) to save space — but this is a judgment call per release, not an automatic
rule. When in doubt, ask before deleting a previous release's installer asset.
