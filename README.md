# onAIr

**onAIr** is a transparent, always-on-top teleprompter for Windows, built with **WinUI 3 / C#**.

Load a presentation script into the **TP** — a transparent floating window — keep it visible above
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
(Manual / Auto / Voice — only the active mode's speed control is shown), adjust font size and TP
opacity, pick a font color, and load your `.txt` script. Large ▲▼ buttons are touch-friendly for a
secondary screen. The footer's TP controls (**Open TP** · **Lock TP** · **Hide TP**) sit on the
left, **Hide Controller** on the right._

---

### Controller — Q&A recording

[![Controller Q&A tab](OnAirNative/Assets/screenshots/screenshot-controller-qa.png)](OnAirNative/Assets/screenshots/screenshot-controller-qa.png)

_Press **● Record** (or `Ctrl+Alt+R`) to capture a client question. onAIr transcribes it via Whisper
and sends it to your chosen AI provider. The answer appears in the TP instantly. Configure chat +
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

### The TP

[![The TP](OnAirNative/Assets/screenshots/screenshot-box.png)](OnAirNative/Assets/screenshots/screenshot-box.png)

_The TP is the transparent, always-on-top window your script scrolls in. It floats above any app —
including a shared screen or recording — while staying invisible to viewers by default._

---

## Features

### The TP
- **Transparent, frameless, always-on-top** — WinUI 3 + DWM compositor
- **Hidden from screen share by default** — `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`;
  toggle it off from the Controller footer's **Hide TP** button to let viewers of your shared
  screen/recording see it too
- **Click-through mode** — keyboard and mouse pass to the window underneath; toggle with the
  footer's **Lock TP** button or `Ctrl+Alt+Home`
- **Mode label** in header — shows current mode (Script / Q&A); no clickable pills to distract
- **Always reopens at the top-left corner (0,0)** on app launch, so it's never lost off-screen on a
  disconnected or rearranged multi-monitor setup — moving/hiding/showing it within a session still
  keeps its last position
- **Starts hidden** — only the Controller opens on launch; show the TP with **Open TP** when
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
- **Font size** — tunable in the Controller or via `Ctrl+Alt+= / -`, applies live to the TP
- **Font color presets** — White, Yellow, Green, Aqua, Orange, Pink — saved to config

### Q&A mode
- **Record** from the Controller's Q&A tab (or `Ctrl+Alt+R`)
- **Three capture sources** — microphone only, system audio only (WASAPI loopback), or **both
  mixed** into a single 16 kHz mono track (exactly the format Whisper wants, so nothing is
  resampled twice)
- **Whisper transcription** — in-process via `whisper.net` (fast, no subprocess) or cloud API
- **Live preview** — while recording with a local Whisper model loaded, the TP shows a rolling,
  still-forming transcript of what you've said so far, a few seconds behind; see the model-size
  note below
- **AI answer** — sent to your chosen LLM; displayed in the TP
- **6 chat providers** — Azure OpenAI · OpenAI · Groq · Anthropic Claude · Google Gemini · Mistral
- **Split providers** — use Groq for Whisper, Anthropic for chat, for example
- **System prompt + presentation context** — customise tone, language, persona per session

### Controller window
Every tab is organised into cards (bold, letter-spaced, accent-colored titles) that group related
controls instead of one long flat list, selected via a segmented "pill" tab bar (the active tab
shows icon+text and grows; the rest collapse to icon-only):
- **Script tab**: file picker, scroll mode, per-mode speed control, font size/TP opacity/color
  (click a preset swatch to load its hex into the editable custom-color box), font family picker,
  save/reset settings, virtual ▲▼ buttons
- **Q&A tab**: record button, status, chat/transcription provider selection + test connection,
  system prompt, Whisper model
- **App Stealth tab**: embed any Win32 window in a stealth container
- **Settings tab**: audio device + capture source selection + live mic level test, one card per
  AI provider (configure credentials and test each independently — see "AI providers" below),
  voice scroll sensitivity, System/Light/Dark theme picker, Remote Control (Stream Deck + MCP)
  toggle and setup
- **About tab**: version, hotkey reference, GitHub link, check-for-updates with one-click install
- **Single instance**: launching the app again just brings the existing Controller forward
- **Footer**: the 3 TP controls (**Open TP** · **Lock TP** · **Hide TP**) on the left, **Hide
  Controller** on the right

### AI providers
Configuring a provider's credentials is fully independent of which provider you're currently
*using* for chat or transcription — no more switching the chat dropdown just to edit a different
provider's key:
- **6 provider cards in Settings** (Azure OpenAI · OpenAI · Groq · Anthropic Claude · Google
  Gemini · Mistral), each showing a configured/not-configured status and a **Configure** button
  that opens that provider's own credential editor + a **Test connection** button
- **Q&A tab just picks which ones to use** — two dropdowns (chat provider, transcription
  provider) plus a **Test connection** button for whichever is currently selected
- **Split providers** — e.g. Groq for chat, OpenAI for transcription — configure both
  independently in Settings, then pick each one in the Q&A tab's dropdowns

### App Stealth container
- Enumerate all visible windows (`EnumWindows`)
- Re-parent the selected window into a `WDA_EXCLUDEFROMCAPTURE` Win32 container
- Container has title bar, close button, and resize borders
- Embedded app runs normally — fully interactive
- Restores the window to its original state on release or app close

### Remote control (Stream Deck & AI assistants)
A local-only WebSocket server (Settings → REMOTE CONTROL, **on** by default — toggle to disable)
lets other apps on your PC control onAIr, never reachable from the network:
- **Elgato Stream Deck plugin** (`streamdeck-plugin/`) — 16 actions: toggle TP/lock/hide-in-share
  ×2/recording, an AI status tile, 4 momentary actions (release stealth, open file, scroll
  up/down), and 6 dial-capable actions (opacity, font size, scroll speed ×2, scroll step, voice
  sensitivity). Install via Settings → REMOTE CONTROL → **Install Stream Deck Plugin**.
- **MCP server** (`mcp-server/`) — lets any Model Context Protocol client (Claude Desktop, VS
  Code Copilot Chat, etc.) control onAIr with natural language: "open the teleprompter", "set the
  font color to blue", "load my demo script". 21 tools, each individually toggleable via
  Settings → REMOTE CONTROL → **MCP Tools & Setup…**, which also has a one-click **Copy MCP
  Config** button for registering the server with your AI client.

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
| `ggml-tiny.en.bin` | ~75 MB | Fastest, English only |
| `ggml-base.en.bin` | ~142 MB | Fast, English only |
| `ggml-small.en.bin` | ~244 MB | Balanced |
| `ggml-medium.bin` | ~1.5 GB | Best accuracy, multilingual |

Set the path in **Controller → Q&A → Whisper local model path**.
Leave blank to use the cloud API.

> **Live preview and model size:** the live preview re-transcribes the whole in-progress
> recording on a timer (see above), so it needs a model that transcribes faster than you talk.
> On CPU, `tiny`/`base` keep up comfortably (no quantization needed); `small` is borderline;
> `medium`/`large` are noticeably too slow for the preview to ever catch up during a short
> Q&A recording — the *final* transcription after you stop still works fine with any model
> size, it's only the live, still-recording preview that needs a lighter one.
>
> **Best cost/benefit (real-world tested):** `ggml-tiny` **unquantized** and `ggml-base` with
> **q5_1** quantization. Quantized variants (`q5_1`, `q5_0`, `q8_0`, etc.) trade a little accuracy
> for a smaller download and faster inference — worth it for `base`, not necessary for `tiny`.

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
| `Ctrl+Alt+Home` | Lock / unlock TP (drag/resize) |
| `Ctrl+Alt+R` | Start / stop Q&A recording |
| `Ctrl+Alt+O` | Open script file picker |
| `Ctrl+Alt+]` | Increase TP opacity |
| `Ctrl+Alt+[` | Decrease TP opacity |
| `Ctrl+Alt+V` | Open / hide TP |
| `Ctrl+Alt+S` | Hide / show TP **in share** |
| `Ctrl+Alt+H` | Hide / show Controller **in share** |
| `Ctrl+Alt+U` | Release the App Stealth container |
| `Ctrl+Alt+.` | Increase Auto-scroll speed |
| `Ctrl+Alt+,` | Decrease Auto-scroll speed |
| `Ctrl+Alt+=` | Increase font size |
| `Ctrl+Alt+-` | Decrease font size |
| `Ctrl+Alt+Up` | Increase Voice scroll speed (Voice mode) |
| `Ctrl+Alt+Down` | Decrease Voice scroll speed (Voice mode) |
| `Ctrl+Alt+Right` | Increase scroll step (Manual mode) |
| `Ctrl+Alt+Left` | Decrease scroll step (Manual mode) |
| `Ctrl+Alt+'` | Increase Voice scroll sensitivity |
| `Ctrl+Alt+;` | Decrease Voice scroll sensitivity |

> **Note:** on some machines, Ctrl+Alt+Arrow is also bound by legacy Intel/NVIDIA graphics
> driver control panels to rotate the display. If that binding claims the combo first, onAIr's
> arrow shortcuts above simply won't register (no error, no crash) — let us know if that happens.

---

## Project structure

```
onair-native/
├── OnAirNative.sln
├── OnAirNative/
│   ├── OnAirNative.csproj          WinUI 3, unpackaged, x64, net8.0-windows10.0.19041.0
│   ├── Program.cs                  Custom Main — single-instance guard + activation redirect
│   ├── App.xaml / App.xaml.cs      Service wiring, hotkey dispatch, window lifetime
│   ├── GlobalUsings.cs             Shared implicit usings
│   ├── Win32/NativeMethods.cs      P/Invoke (SetWindowDisplayAffinity, RegisterHotKey,
│   │                               EnumWindows, SetParent, Shell_NotifyIcon, …)
│   ├── Models/AppConfig.cs         Root config model (6 providers + appearance + theme + window state)
│   ├── Helpers/Converters.cs       XAML value converters
│   ├── Services/
│   │   ├── ConfigService.cs        JSON persistence to %LocalAppData%, legacy folder migration
│   │   ├── SecretProtector.cs      DPAPI encryption for API keys at rest
│   │   ├── WindowService.cs        Win32 transparency / click-through / always-on-top / focus
│   │   ├── HotkeyService.cs        RegisterHotKey on background thread + message loop
│   │   ├── RemoteControlService.cs Loopback WebSocket server — Stream Deck plugin + MCP server
│   │   ├── AudioService.cs         NAudio WASAPI mic + loopback mixdown, RMS voice monitor
│   │   ├── WhisperService.cs       whisper.net in-process + cloud API fallback
│   │   ├── AiChatService.cs        6 AI providers via HttpClient
│   │   ├── TrayService.cs          Shell_NotifyIcon system tray + context menu
│   │   ├── StealthWindowService.cs EnumWindows window list + SetWindowDisplayAffinity
│   │   ├── WindowEmbedService.cs   SetParent window embedding in stealth container
│   │   └── UpdateService.cs        GitHub Releases check + installer download/launch
│   ├── ViewModels/                 MVVM via CommunityToolkit.Mvvm
│   │   ├── OverlayViewModel.cs     TP: script, Q&A, scroll modes, recording
│   │   ├── ControllerViewModel.cs  Tab sub-VM orchestrator, theme, protection toggles
│   │   ├── ScrollTabViewModel.cs   Scroll settings, file loading, opacity/font
│   │   ├── AiTabViewModel.cs       Provider selection, credentials, test connection
│   │   └── AboutTabViewModel.cs    Version, authors, GitHub link, update check/install
│   ├── Views/
│   │   ├── OverlayWindow.xaml      The TP: transparent window (mode label, Script + Q&A panels)
│   │   ├── ControllerWindow.xaml   Controller (5-tab pill bar + footer)
│   │   └── Dialogs/
│   │       ├── ProviderConfigDialog.xaml   Credential editor + test, per provider
│   │       └── McpToolsDialog.xaml         MCP tool enable/disable + client config snippet
│   └── Assets/
│       ├── app-icon.ico                    App + tray icon
│       ├── onair-remote.streamDeckPlugin   Bundled, packaged Stream Deck plugin
│       ├── mcp-server/                     Bundled, published MCP server (OnAirMcp.dll + deps)
│       └── screenshots/                    README screenshots
├── streamdeck-plugin/               Elgato Stream Deck plugin (Node/TypeScript)
│   ├── com.souz4rafael.onair.sdPlugin/   manifest.json, icons, dial layout
│   ├── src/                              onair-client.ts, plugin.ts, actions/
│   └── gen_icons.py                      Programmatic icon generation
└── mcp-server/                      onAIr MCP server (C# console app)
    ├── OnAirMcp.csproj             ModelContextProtocol + Microsoft.Extensions.Hosting
    ├── Program.cs                  stdio transport, tool discovery
    ├── OnAirClient.cs              WebSocket client to RemoteControlService
    ├── OnAirTools.cs               21 MCP tools (onair_get_state, onair_toggle_tp, …)
    └── ToolGate.cs                 Per-tool enable/disable, reads config.json directly
```

---

## License

MIT
