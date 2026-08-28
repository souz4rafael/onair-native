# onAIr

**onAIr** is a transparent, always-on-top teleprompter for Windows, built with **WinUI 3 / C#**.

Load a presentation script into the **Box** — a transparent floating window — keep it visible above
your screen share, and use AI to capture and answer client questions in real time. Fast in-process
Whisper transcription, bulletproof WASAPI audio, a card-based Controller UI with a System/Light/Dark
theme, in-app auto-update, and a unique **App Stealth** container to hide any running app from
screen capture.

**Authors:** Rafael Souza (Microsoft) · GitHub Copilot (Claude Sonnet 4.6)

---

## Screenshots

### Controller — Script

[![Controller Script tab](OnAirNative/Assets/screenshots/screenshot-controller-script.png)](OnAirNative/Assets/screenshots/screenshot-controller-script.png)

_The Controller is your presenter dashboard. Every tab is organised into cards. Choose scroll mode
(Manual / Auto / Voice — only the active mode's speed control is shown), adjust font size and Box
opacity, pick a font color, and load your `.txt` script. Large ▲▼ buttons are touch-friendly for a
secondary screen. The footer's Box controls (**Open Box** · **Lock Box** · **Hide Box**) sit on the
left, **Hide Controller** on the right._

---

### Controller — Q&A recording

[![Controller Q&A tab](OnAirNative/Assets/screenshots/screenshot-controller-qa.png)](OnAirNative/Assets/screenshots/screenshot-controller-qa.png)

_Press **● Record** (or `Ctrl+Alt+R`) to capture a client question. onAIr transcribes it via Whisper
and sends it to your chosen AI provider. The answer appears in the Box instantly. Configure chat +
transcription providers independently._

---

### Controller — App Stealth

[![Controller App Stealth tab](OnAirNative/Assets/screenshots/screenshot-controller-stealth.png)](OnAirNative/Assets/screenshots/screenshot-controller-stealth.png)

_Select any running Win32 app from the list and click **⊕ Embed in container**. The app is
re-parented into a `WDA_EXCLUDEFROMCAPTURE` container — you can interact with it normally, but the
client sees nothing during screen share. Ideal for hiding reference notes, internal docs, or pricing
tools._

---

### Controller — Settings

[![Controller Settings tab](OnAirNative/Assets/screenshots/screenshot-controller-settings.png)](OnAirNative/Assets/screenshots/screenshot-controller-settings.png)

_Choose your audio input device (for recording and voice scroll), configure the voice scroll
sensitivity threshold, and pick a **System / Light / Dark** theme for the Controller — it applies
instantly, no restart needed._

---

### The Box

[![The Box](OnAirNative/Assets/screenshots/screenshot-box.png)](OnAirNative/Assets/screenshots/screenshot-box.png)

_The Box is the transparent, always-on-top window your script scrolls in. It floats above any app —
including a shared screen or recording — while staying invisible to viewers by default._

---

## Features

### The Box
- **Transparent, frameless, always-on-top** — WinUI 3 + DWM compositor
- **Hidden from screen share by default** — `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`;
  toggle it off from the Controller footer's **Hide Box** button to let viewers of your shared
  screen/recording see it too
- **Click-through mode** — keyboard and mouse pass to the window underneath; toggle with the
  footer's **Lock Box** button or `Ctrl+Alt+Home`
- **Mode label** in header — shows current mode (Script / Q&A); no clickable pills to distract
- **Always reopens at the top-left corner (0,0)** on app launch, so it's never lost off-screen on a
  disconnected or rearranged multi-monitor setup — moving/hiding/showing it within a session still
  keeps its last position
- **Starts hidden** — only the Controller opens on launch; show the Box with **Open Box** when
  you're ready

### Script / Teleprompter
- Load any `.txt` file from the Controller or via `Ctrl+Alt+O`
- **Manual scroll** — `Ctrl+Alt+PgUp / PgDn`, step size tunable in the Controller (global, works
  even when Teams has focus)
- **Auto-scroll** — continuous smooth scroll; speed tunable in the Controller or via `Ctrl+Alt+. / ,`
- **Voice-activated scroll** — microphone RMS detection with its own independent speed control
  (separate from Auto-scroll speed); sensitivity/threshold adjustable in Settings
- **Only the active scroll mode's control is shown** — Manual/Auto/Voice each get their own
  dedicated speed control in the Controller instead of all three competing for space
- **Font size** — tunable in the Controller or via `Ctrl+Alt+= / -`, applies live to the Box
- **Font color presets** — White, Yellow, Green, Aqua, Orange, Pink — saved to config

### Q&A mode
- **Record** from the Controller's Q&A tab (or `Ctrl+Alt+R`)
- **Three capture sources** — microphone only, system audio only (WASAPI loopback), or **both
  mixed** into a single 16 kHz mono track (exactly the format Whisper wants, so nothing is
  resampled twice)
- **Whisper transcription** — in-process via `whisper.net` (fast, no subprocess) or cloud API
- **AI answer** — sent to your chosen LLM; displayed in the Box
- **6 chat providers** — Azure OpenAI · OpenAI · Groq · Anthropic Claude · Google Gemini · Mistral
- **Split providers** — use Groq for Whisper, Anthropic for chat, for example
- **System prompt + presentation context** — customise tone, language, persona per session

### Controller window
Every tab is organised into cards (bold, letter-spaced, accent-colored titles) that group related
controls instead of one long flat list:
- **Script tab**: file picker, scroll mode, per-mode speed control, font size/Box opacity/color,
  save/reset settings, virtual ▲▼ buttons
- **Q&A tab**: record button, status, provider selection, credential config, system prompt, Whisper
  model
- **App Stealth tab**: embed any Win32 window in a stealth container
- **Settings tab**: audio device + capture source selection, voice scroll sensitivity, System/Light/Dark
  theme picker
- **About tab**: version, hotkey reference, GitHub link, check-for-updates with one-click install
- **Single instance**: launching the app again just brings the existing Controller forward
- **Footer**: the 3 Box controls (**Open Box** · **Lock Box** · **Hide Box**) on the left, **Hide
  Controller** on the right

### App Stealth container
- Enumerate all visible windows (`EnumWindows`)
- Re-parent the selected window into a `WDA_EXCLUDEFROMCAPTURE` Win32 container
- Container has title bar, close button, and resize borders
- Embedded app runs normally — fully interactive
- Restores the window to its original state on release or app close

---

## Build prerequisites

1. **.NET 8 SDK** (x64) — [download](https://dotnet.microsoft.com/download)
2. **Windows 10 version 2004** (build 19041) or later — required for WinUI 3 and
   `WDA_EXCLUDEFROMCAPTURE`

```powershell
git clone https://github.com/souz4rafael/onair-native.git
cd onair-native

dotnet restore OnAirNative/OnAirNative.csproj
dotnet build   OnAirNative/OnAirNative.csproj -c Release
```

Run the output at:
```
OnAirNative/bin/Release/net8.0-windows10.0.19041.0/OnAirNative.exe
```

> **VS Code:** install the [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit)
> extension and open the repo folder. Use `Ctrl+Shift+B` to build.

---

## AI setup

Open **Controller → Q&A tab** → choose a provider → click **⚙ Configure provider…**

### Chat providers

| Provider | Where to get a key | Cost |
|---|---|---|
| **Azure OpenAI** | Azure Portal → your resource → Keys and Endpoint | Pay-per-use |
| **OpenAI** | [platform.openai.com](https://platform.openai.com) → API Keys | Pay-per-use |
| **Groq** | [console.groq.com](https://console.groq.com) → API Keys | **Free tier** |
| **Anthropic Claude** | [console.anthropic.com](https://console.anthropic.com) | Pay-per-use |
| **Google Gemini** | [aistudio.google.com](https://aistudio.google.com) | Free tier |
| **Mistral** | [console.mistral.ai](https://console.mistral.ai) | Pay-per-use |

### Transcription providers (Whisper)

Azure OpenAI, OpenAI and Groq support the Whisper API. If you use Anthropic/Gemini/Mistral for
chat, set a separate transcription provider.

**Groq is the easiest way to start** — free tier, no credit card.

---

## Whisper local model (optional)

For fully in-process, offline transcription, download a `.gguf` / `.bin` model from
[huggingface.co/ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp):

| Model | Size | Notes |
|---|---|---|
| `ggml-base.en.bin` | ~142 MB | Fastest, English only |
| `ggml-small.en.bin` | ~244 MB | Balanced |
| `ggml-medium.bin` | ~1.5 GB | Best accuracy, multilingual |

Set the path in **Controller → Q&A → Whisper local model path**.
Leave blank to use the cloud API.

---

## Configuration

Settings are saved to `%LocalAppData%\onAIr\config.json`.

API keys are encrypted at rest with **Windows DPAPI** (`CurrentUser` scope) before being written to
disk, so a stolen `config.json` is useless on another machine or under another user account.

> Upgrading from a version older than 1.1? Your settings previously lived under
> `%LocalAppData%\onAIr Native\config.json` — they're copied over automatically on first launch,
> and the old file is left untouched.

---

## Updating

Open **Controller → About tab**. It checks GitHub Releases automatically the first time you visit
the tab each session, or click **Check for Updates** any time. If a newer version is available,
click **⬇ Download & Install** — after a confirmation prompt, it downloads the installer, launches
it (Windows may ask for administrator permission), and closes onAIr so setup can complete.

---

## Keyboard shortcuts

All shortcuts are **global** — they work even when Teams, PowerPoint or Edge has focus.

| Shortcut | Action |
|---|---|
| `Ctrl+Alt+PgUp` | Scroll script up |
| `Ctrl+Alt+PgDn` | Scroll script down |
| `Ctrl+Alt+Home` | Lock / unlock Box (drag/resize) |
| `Ctrl+Alt+R` | Start / stop Q&A recording |
| `Ctrl+Alt+O` | Open script file picker |
| `Ctrl+Alt+]` | Increase Box opacity |
| `Ctrl+Alt+[` | Decrease Box opacity |
| `Ctrl+Alt+V` | Open / hide Box |
| `Ctrl+Alt+S` | Hide / show Box **in share** |
| `Ctrl+Alt+H` | Hide / show Controller **in share** |
| `Ctrl+Alt+U` | Release the App Stealth container |
| `Ctrl+Alt+.` | Increase Auto-scroll speed |
| `Ctrl+Alt+,` | Decrease Auto-scroll speed |
| `Ctrl+Alt+=` | Increase font size |
| `Ctrl+Alt+-` | Decrease font size |

---

## Project structure

```
onair-native/
├── OnAirNative.sln
└── OnAirNative/
    ├── OnAirNative.csproj          WinUI 3, unpackaged, x64, net8.0-windows10.0.19041.0
    ├── Program.cs                  Custom Main — single-instance guard + activation redirect
    ├── App.xaml / App.xaml.cs      Service wiring, hotkey dispatch, window lifetime
    ├── GlobalUsings.cs             Shared implicit usings
    ├── Win32/NativeMethods.cs      P/Invoke (SetWindowDisplayAffinity, RegisterHotKey,
    │                               EnumWindows, SetParent, Shell_NotifyIcon, …)
    ├── Models/AppConfig.cs         Root config model (6 providers + appearance + theme + window state)
    ├── Helpers/Converters.cs       XAML value converters
    ├── Services/
    │   ├── ConfigService.cs        JSON persistence to %LocalAppData%, legacy folder migration
    │   ├── SecretProtector.cs      DPAPI encryption for API keys at rest
    │   ├── WindowService.cs        Win32 transparency / click-through / always-on-top / focus
    │   ├── HotkeyService.cs        RegisterHotKey on background thread + message loop
    │   ├── AudioService.cs         NAudio WASAPI mic + loopback mixdown, RMS voice monitor
    │   ├── WhisperService.cs       whisper.net in-process + cloud API fallback
    │   ├── AiChatService.cs        6 AI providers via HttpClient
    │   ├── TrayService.cs          Shell_NotifyIcon system tray + context menu
    │   ├── StealthWindowService.cs EnumWindows window list + SetWindowDisplayAffinity
    │   ├── WindowEmbedService.cs   SetParent window embedding in stealth container
    │   └── UpdateService.cs        GitHub Releases check + installer download/launch
    ├── ViewModels/                 MVVM via CommunityToolkit.Mvvm
    │   ├── OverlayViewModel.cs     Box: script, Q&A, scroll modes, recording
    │   ├── ControllerViewModel.cs  Tab sub-VM orchestrator, theme, protection toggles
    │   ├── ScrollTabViewModel.cs   Scroll settings, file loading, opacity/font
    │   ├── AiTabViewModel.cs       Provider selection, credentials, test connection
    │   └── AboutTabViewModel.cs    Version, authors, GitHub link, update check/install
    ├── Views/
    │   ├── OverlayWindow.xaml      The Box: transparent window (mode label, Script + Q&A panels)
    │   ├── ControllerWindow.xaml   Controller (5 tabs + footer)
    │   └── Dialogs/
    │       └── ProviderConfigDialog.xaml   Credential editor per provider
    └── Assets/
        ├── app-icon.ico            App + tray icon
        └── screenshots/            README screenshots
```

---

## License

MIT
