# Installer (NSIS)

Builds `onAIr-Native-Setup-<version>.exe`, a self-contained installer for onAIr Native.

## Prerequisites

1. **NSIS 3.x** — `winget install -e --id NSIS.NSIS`
2. **Windows App Runtime redistributable** — required, but NOT committed (it's ~111 MB).
   Download it into `installer/redist/` before building:

   ```powershell
   New-Item -ItemType Directory -Force installer\redist | Out-Null
   Invoke-WebRequest `
     -Uri "https://aka.ms/windowsappsdk/2.1/latest/windowsappruntimeinstall-x64.exe" `
     -OutFile "installer\redist\WindowsAppRuntimeInstall-x64.exe"
   ```

3. **A fresh publish** of the app at `dist/publish-current`:

   ```powershell
   cd OnAirNative
   dotnet publish -c Release -r win-x64 --self-contained true -p:WindowsPackageType=None -o ..\dist\publish-current
   ```

## Build

```powershell
& "C:\Program Files (x86)\NSIS\makensis.exe" installer\onair-native.nsi
```

## What the installer does

- Installs the app (framework-dependent against the Windows App SDK, .NET self-contained) to `Program Files\onAIr Native`.
- Runs `WindowsAppRuntimeInstall-x64.exe --quiet --force` so the Windows App Runtime is present (without it the app throws `XamlParseException` on launch).
- Creates Start Menu + Desktop shortcuts and an Add/Remove Programs uninstall entry.

The resulting `.exe` ships via GitHub Releases; it is not committed to the repo.
