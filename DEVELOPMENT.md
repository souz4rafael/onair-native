# onAIr Native — Development Notes

Use this file to resume development in a new session. Tell Copilot:
"I want to continue onAIr Native — read DEVELOPMENT.md in the repo."

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
- **Current version:** 1.0.4 — bump it in **two** places, they must stay in sync:
  `OnAirNative/ViewModels/AboutTabViewModel.cs` (`Version`) and
  `installer/onair-native.nsi` (`PRODUCT_VERSION`).
- **Releases:** https://github.com/souz4rafael/onair-native/releases (latest published: v1.0.3)
- **Stack:** WinUI 3 (Windows App SDK 2.1.3), .NET 8, NAudio 2.2.1, whisper.net 1.7.3,
  CommunityToolkit.Mvvm 8.3.2, System.Security.Cryptography.ProtectedData 8.0.0

---

## Architecture

Two windows, 10 services, MVVM pattern. The entry point is a **custom `Main`**
(`Program.cs`, with `DISABLE_XAML_GENERATED_MAIN=true`) so single-instance is resolved
before XAML boots.

```
Program.cs               — AppInstance.FindOrRegisterForKey("onAIr-native-main")
│                          → RedirectActivationToAsync + exit when another instance owns the key
└── App.xaml.cs          — service wiring, hotkey dispatch, OnRedirectedActivation
    ├── OverlayWindow        — transparent, frameless, always-on-top (hidden by default)
    │   └── OverlayViewModel — script, Q&A, scroll modes, voice
    └── ControllerWindow     — 5-tab control panel (main app window)
        └── ControllerViewModel
            ├── ScrollTabViewModel
            └── AiTabViewModel

Services/
├── ConfigService        — JSON persistence -> %LocalAppData%\onAIr Native\config.json
├── SecretProtector      — DPAPI (CurrentUser) encryption for API keys, `dpapi:v1:` prefix
├── WindowService        — DWM transparency, click-through, AoT, BringToFront
├── HotkeyService        — Win32 RegisterHotKey on background thread
├── AudioService         — WASAPI mic + loopback mixdown + RMS voice monitor
├── WhisperService       — whisper.net in-process + cloud API fallback
├── AiChatService        — 6 AI providers via HttpClient
├── TrayService          — Shell_NotifyIcon tray icon + context menu
├── StealthWindowService — EnumWindows window list
└── WindowEmbedService   — SetParent window embedding in stealth container
```

---

## Controller tabs

| Tab | Key features |
|-----|-------------|
| **Script** | Load .txt, Manual/Auto/Voice scroll, font size/opacity/color, save/reset settings |
| **Q&A** | Record button, 6 AI providers, Whisper model path, system prompt |
| **App Stealth** | Embed any Win32 app in WDA_EXCLUDEFROMCAPTURE container (interactive!) |
| **Settings** | Audio device selector, capture source (mic / system / both), voice threshold slider |
| **About** | Version, hotkeys, GitHub link |

**Footer:** Overlay visible/hidden toggle · Overlay locked/unlocked toggle · Hide controller from capture

---

## Global hotkeys

| Hotkey | Action |
|--------|--------|
| Ctrl+Alt+PgUp/PgDn | Scroll script |
| Ctrl+Alt+Home | Toggle Move Mode |
| Ctrl+Alt+R | Q&A record start/stop |
| Ctrl+Alt+M | Cycle mode Script ↔ Q&A |
| Ctrl+Alt+O | Open file picker |
| Ctrl+Alt+, | Bring the Controller to the front (`VK_OEM_COMMA`, id `ID_CONTROLLER`) |

Hotkeys are registered on a dedicated background thread that owns its own message loop
(`HotkeyService.HotkeyLoop`). IDs are contiguous (`ID_SCROLL_UP`..`ID_CONTROLLER`) and the
cleanup loop unregisters the whole range — keep new IDs inside it.

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
7. **WebView2 does not work in WS_EX_LAYERED windows** (the overlay) → Browser mode was removed
8. **Exceptions that escape a WndProc crash the CLR** with `ExecutionEngineException` → always try/catch in every WndProc
9. **`_populatingUi` flag is CRITICAL** → slider `ValueChanged` overwrites config during UI init → guard all handlers with `if (_populatingUi) return;`

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

Works well with: Win32, WPF, WinForms, older Electron
Limited for: Chrome/Edge/modern Chromium (DirectComposition surfaces bypass the container)

---

## Status

**Done**
- Single-instance lock (`Program.cs` + `OnRedirectedActivation`) — a second launch redirects the
  activation and exits with code 0, bringing the existing Controller forward.
- `Ctrl+Alt+,` Controller hotkey wired end-to-end (`VK_OEM_COMMA` → `WindowService.BringToFront`,
  which un-minimises via `IsIconic`/`SW_RESTORE` before `SetForegroundWindow`).
- Real mic + system-audio mix for `source = "both"` (see the audio section above).
- Browser mode fully removed — `Models/QuickLink.cs` deleted, `AppConfig.QuickLinks` dropped,
  stale XAML placeholders and doc-comments cleaned up.
- API keys encrypted at rest with DPAPI (`SecretProtector`).

**Deliberately not doing**
- `.txt` shell association ("Open with onAIr Native") — dropped, not worth the registry surface.

**Deferred**
- Automated tests and CI. There is no test project and no GitHub Actions workflow; validation is
  currently `dotnet build` plus manual smoke tests.

---

## Config location

`%LocalAppData%\onAIr Native\config.json`

Notable values:
- `voiceRmsThreshold`: 5.0 (lowered from 15 — easier to trigger voice scroll)
- `overlayProtected`: true (hidden from screen capture)
- `controllerProtected`: false
- `audioRecordingSource`: `microphone` | `system` | `both`

API keys are stored with a `dpapi:v1:` prefix; plain-text values from older builds are migrated
on the next save.

Diagnostic logs in same folder: `launch.log`, `overlay-init.log`, `controller-init.log`, `tray.log`, `crash.log`

---

## Release checklist

1. Bump `AboutTabViewModel.Version` and `PRODUCT_VERSION` in `installer/onair-native.nsi`.
2. `dotnet publish -c Release -r win-x64 --self-contained true -p:WindowsPackageType=None -o ..\dist\publish-current`
3. `& "C:\Program Files (x86)\NSIS\makensis.exe" installer\onair-native.nsi`
   (needs `installer/redist/WindowsAppRuntimeInstall-x64.exe` — see `installer/README.md`)
4. `gh release create vX.Y.Z` and attach the setup `.exe`.
