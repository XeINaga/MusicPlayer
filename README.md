# Build Requirements

- **Windows 10/11 x64** (the native components and the installer build only run on Windows)
- **.NET SDK 10.0** (tested with 10.0.3xx)
- **Windows 11 SDK 10.0.26100** (the project targets platform 10.0.26100.0;
  NuGet packages — Windows App SDK 1.8, FFmpegInteropX, TagLibSharp — are
  restored automatically on first build)
- **MinGW-w64 g++** (MSYS2 UCRT64, C++17) — compiles the desktop-lyrics
  overlay (`DesktopLyricsOverlay.exe`) and `QrcNative.dll` during the normal
  .NET build. Default toolchain path is `E:\mingw\msys2\ucrt64\bin\`; override
  with `-p:GxxPath=<path to mingw bin>\`
- **VC++ Redistributable** present in `System32` (`vcruntime140.dll` etc.) —
  the build copies it next to the exe so the output runs standalone
- For the MSI installer only:
  - **WiX v4 CLI** (`wix`, installed as a .NET global tool: `dotnet tool install --global wix`)
  - **Python 3** on PATH (regenerates `installer/files.wxs`)
  - `.NET Framework csc` (ships with Windows, no extra install)
  - `installer/ffmpeg-lite/ffmpeg.exe` — the trimmed FFmpeg 9.0.2 build used for
    loudness scanning. The binary is committed; if missing, rebuild it with the
    recipe in `installer/ffmpeg-lite/README.md`.

# Build

```bat
:: Debug build (output: bin\Debug\...\win-x64\)
dotnet build

:: Publish a runnable app folder into .\publish
dotnet publish -o publish

:: Release publish + MSI installer (installer\MusicPlayer.msi)
powershell -ExecutionPolicy Bypass -File installer/build_msi.ps1

:: Same, but with a MinGW toolchain outside the default location
:: (the trailing backslash matters for plain dotnet build/publish)
powershell -ExecutionPolicy Bypass -File installer/build_msi.ps1 -GxxPath "C:\msys2\ucrt64\bin"
```

The build automatically compiles both native components with g++, copies the
VC++ runtime, and trims unused locale folders from the output. The MSI is
framework-dependent: it expects the .NET Desktop Runtime 10.0 x64 on the
target machine.
