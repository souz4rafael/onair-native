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
- **Current version:** 1.0.5 — bump it in **two** places, they must stay in sync:
  `OnAirNative/ViewModels/AboutTabViewModel.cs` (`Version`) and
  `installer/onair-native.nsi` (`PRODUCT_VERSION`).
- **Releases:** https://github.com/souz4rafael/onair-native/releases (latest published: v1.0.5)
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

**Footer:** Overlay visible/hidden toggle · Overlay locked/unlocked toggle · Overlay visible/hidden **in share** toggle (`OverlayProtected`) · Hide controller from capture

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
10. **Unpackaged windows don't inherit the exe icon** → an `<ApplicationIcon>` in the csproj alone is not enough; each `Window` must call `AppWindow.SetIcon(path)` itself (see `WindowService.SetWindowIcon`) or the taskbar/title bar/Alt-Tab show a generic icon

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
- Taskbar/title bar/Alt-Tab icon (`WindowService.SetWindowIcon`, called from both windows'
  `OnFirstActivated`) + `<ApplicationIcon>` in the csproj — unpackaged WinUI 3 windows do not pick
  up the exe's icon on their own, so it was never showing (generic icon) until now.
- Overlay screen-share protection is now toggleable at runtime: Controller footer button
  `📽/🙈 Overlay: visible/hidden in share` (`ControllerViewModel.OverlayProtected` →
  `OverlayProtectionChanged` event → `WindowService.SetContentProtection` on the overlay hwnd).
  Previously `OverlayProtected` was only read once at startup with no UI to change it.
- Full VC++ Redistributable app-local deployment, including `vcomp140.dll` (OpenMP, confirmed via
  PE import-table analysis to be a genuine static dependency of `ggml-whisper.dll`) — see the
  dependency note below.

**Deliberately not doing**
- `.txt` shell association ("Open with onAIr Native") — dropped, not worth the registry surface.

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

`%LocalAppData%\onAIr Native\config.json`

Notable values:
- `voiceRmsThreshold`: 5.0 (lowered from 15 — easier to trigger voice scroll)
- `overlayProtected`: true by default (hidden from screen capture); toggle at runtime via the
  Controller footer's `📽/🙈 Overlay: visible/hidden in share` button
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
