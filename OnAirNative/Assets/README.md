# onAIr Native Assets

Place the following files in this folder:

- `app-icon.ico`  — Application icon (used by installer + taskbar)
- `app-icon.png`  — 256×256 PNG version (used by tray)
- `tray-icon.png` — 16×16 or 32×32 PNG for system tray

You can export these from the original Electron app:
  `github.com/souz4rafael/onair/tree/master/assets`

## Whisper Models

Whisper.net uses `.bin` (ggml) model files. Download from:
  https://huggingface.co/ggerganov/whisper.cpp

Recommended model sizes:
- `ggml-base.en.bin`   ~142 MB — fastest, English only
- `ggml-small.en.bin`  ~244 MB — balanced
- `ggml-medium.bin`    ~1.5 GB — best accuracy, multilingual

Place model files anywhere and configure the path in Controller → AI tab.
Model files are excluded from git via `.gitignore`.
