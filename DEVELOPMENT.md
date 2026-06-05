# onAIr Native — Development Notes (session 2026-06-05)

Use this file to resume development in a new session. Tell Copilot:
"I want to continue onAIr Native — read DEVELOPMENT.md in the repo."

---

## Repo & build

- **Repo:** https://github.com/souz4rafael/onair-native
- **Local:** `C:\Users\rafasouza\OneDrive - Microsoft\Documents\Clawpilot\OnAirNative\`
- **Build:**
  ```powershell
  cd "...\OnAirNative\OnAirNative"
  dotnet build -c Debug
  # exe: bin\Debug\net8.0-windows10.0.19041.0\OnAirNative.exe
  ```
- **Release v1.0.0:** https://github.com/souz4rafael/onair-native/releases/tag/v1.0.0 (28 MB zip)
- **Stack:** WinUI 3 (Windows App SDK 2.1.3), NAudio 2.2.1, whisper.net 1.7.3, CommunityToolkit.Mvvm 8.3.2

---

## Architecture

Two windows, 8 services, MVVM pattern.

```
App.xaml.cs
├── OverlayWindow        — transparent, frameless, always-on-top (hidden by default)
│   └── OverlayViewModel — script, Q&A, scroll modes, voice
└── ControllerWindow     — 5-tab control panel (main app window)
    └── ControllerViewModel
        ├── ScrollTabViewModel
        └── AiTabViewModel

Services/
├── ConfigService        — JSON persistence -> %LocalAppData%\onAIr Native\config.json
├── WindowService        — DWM transparency, click-through, AoT
├── HotkeyService        — Win32 RegisterHotKey on background thread
├── AudioService         — WASAPI capture + loopback + RMS voice monitor
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
| **Settings** | Audio device selector, voice threshold slider |
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

## Pending (1 item)

**File association + single-instance lock**
- `HandleActivation()` already implemented in `App.xaml.cs`
- Missing: registry entry for `.txt` right-click "Open with onAIr Native"
- Missing: `AppInstance.FindOrRegisterForKey("onAIr-native-main")` + `RedirectActivationToAsync`

---

## Config location

`%LocalAppData%\onAIr Native\config.json`

Key values set today:
- `voiceRmsThreshold`: 5.0 (lowered from 15 — easier to trigger voice scroll)
- `overlayProtected`: true (hidden from screen capture)
- `controllerProtected`: false

Diagnostic logs in same folder: `launch.log`, `overlay-init.log`, `controller-init.log`, `tray.log`, `crash.log`
