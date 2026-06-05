# onAIr Native

> Native Windows WinUI 3 spinoff of [onAIr v1.3.0](https://github.com/souz4rafael/onair) — the transparent teleprompter overlay for presentations.

**Authors:** Rafael Souza (Microsoft) · GitHub Copilot (Claude Sonnet 4.6)

---

## Why native?

| Feature | Electron v1.3 | onAIr Native |
|---------|---------------|--------------|
| Transcription | Whisper via cloud API | whisper.net in-process (~10× faster) |
| Audio capture | WebView2 sandbox (unreliable loopback) | NAudio WASAPI (mic + system audio) |
| Global hotkeys | electron-globalShortcut (occasionally fails) | Win32 RegisterHotKey (bulletproof) |
| Content protection | Electron setContentProtection | Win32 SetWindowDisplayAffinity |
| Install size | ~150 MB | ~80 MB |
| Cold start | ~4 s | ~1 s |

---

## Features

### Overlay window
- Transparent, frameless, always-on-top (WinUI 3 + DWM)
- **Click-through mode** — keyboard/mouse pass to the window underneath; toggle with Ctrl+Alt+Home
- **Three modes** via pill tabs: Script · Q&A · Browser
- **Invisible to screen capture** (SetWindowDisplayAffinity WDA_EXCLUDEFROMCAPTURE)

### Script mode
- Load any `.txt` file
- Manual scroll (Ctrl+Alt+PgUp/PgDn)
- Auto-scroll (continuous, configurable speed)
- Voice-activated scroll (microphone RMS detection)

### Q&A mode (Ctrl+Alt+R)
- Captures audio from microphone or system audio (WASAPI loopback)
- Transcribes via **whisper.net** (in-process) or cloud API (Azure / OpenAI / Groq)
- Sends question to the configured AI provider, displays answer in overlay
- 6 AI providers: Azure OpenAI · OpenAI · Groq · Anthropic Claude · Google Gemini · Mistral

### Browser mode
- Embedded WebView2 inside the overlay
- URL controlled from Controller window
- Up to 10 editable quick links

### Controller window
- **Scroll tab**: file picker, scroll mode, speed/step sliders, large ▲▼ touch buttons
- **AI tab**: provider selection, credential dialog (⚙ Configure), system prompt, presentation context
- **Browser tab**: URL bar, quick link management
- **About tab**: version, hotkey reference
- Footer: hide Controller from screen share toggle

---

## Build prerequisites

1. **Visual Studio 2022** (17.9+) with:
   - .NET desktop development
   - Windows App SDK C# templates
2. **.NET 8 SDK** (x64)
3. **Windows 10 version 2004** (19041) or later (required by WinUI 3)

```powershell
# Restore packages (run from repo root)
dotnet restore OnAirNative/OnAirNative.csproj
```

Open `OnAirNative.sln` in Visual Studio and press F5.

---

## Whisper local model (optional)

For in-process transcription, download a ggml model from
[huggingface.co/ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp):

```
ggml-base.en.bin   ~142 MB  Fastest, English only
ggml-small.en.bin  ~244 MB  Balanced
ggml-medium.bin    ~1.5 GB  Best accuracy, multilingual
```

Set the path in **Controller → AI → Whisper local model path**.  
If left empty, transcription falls back to the cloud API.

---

## Configuration

Settings are saved to `%LocalAppData%\onAIr Native\config.json` and are format-compatible
with the Electron app's `config.json` (you can copy credentials across).

---

## Keyboard shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+Alt+PgUp | Scroll up |
| Ctrl+Alt+PgDn | Scroll down |
| Ctrl+Alt+Home | Toggle Move Mode (click-through on/off) |
| Ctrl+Alt+R | Start/stop Q&A recording |
| Ctrl+Alt+M | Cycle overlay mode (Script → Q&A → Browser) |
| Ctrl+Alt+O | Open script file picker |

---

## Project structure

```
OnAirNative/
├── OnAirNative.sln
└── OnAirNative/
    ├── OnAirNative.csproj        WinUI 3, unpackaged, x64
    ├── App.xaml / App.xaml.cs    Entry point, service wiring, hotkey dispatch
    ├── Win32/NativeMethods.cs    P/Invoke declarations
    ├── Models/AppConfig.cs       Root config model (all 6 providers)
    ├── Services/
    │   ├── ConfigService.cs      JSON persistence
    │   ├── WindowService.cs      Win32 transparency / click-through / AoT
    │   ├── HotkeyService.cs      RegisterHotKey on bg thread
    │   ├── AudioService.cs       NAudio WASAPI capture
    │   ├── WhisperService.cs     whisper.net + API fallback
    │   └── AiChatService.cs      6 AI providers via HttpClient
    ├── ViewModels/               MVVM (CommunityToolkit.Mvvm)
    ├── Views/
    │   ├── OverlayWindow.xaml    Transparent overlay
    │   ├── ControllerWindow.xaml Controller (4 tabs)
    │   └── Dialogs/              Provider config dialog
    └── Assets/                   Icons + Whisper model README
```

---

## License

MIT — same as [onAIr v1.3.0](https://github.com/souz4rafael/onair/blob/master/LICENSE).
