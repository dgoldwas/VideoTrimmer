# VideoTrimmer

A small Windows desktop app for selecting and exporting a section of a video. VideoTrimmer is built with C# and WinForms on .NET 8. It uses FFmpeg and FFprobe as separate processes, so it can inspect and trim large files without loading the full video into memory.

## Features

- Open a video by browsing or drag and drop.
- Inspect duration and frame rate and scrub a timeline with thumbnail previews.
- Set start and end points by frame number or `MM:SS:FF` timecode.
- Export with stream copy for a fast, lossless remux where keyframe boundaries permit.
- Use **Exact frame mode** to re-encode video for frame-accurate boundaries. This takes longer and may be lossy.
- Preserve mapped streams in stream-copy mode (`-map 0`), including audio and subtitles supported by the selected output container.

## Run the app

Download or build a Windows version of FFmpeg that includes both `ffmpeg.exe` and `ffprobe.exe`. Put both executables beside `VideoTrimmer.exe`, in a `tools` subfolder beside it, or in a directory on `PATH`. Then launch `VideoTrimmer.exe`.

The FFmpeg executables used during development are intentionally not committed: each is over 100 MB. The app can also use a local FFmpeg installation. FFmpeg builds may have their own licensing terms; see [FFmpeg Legal](https://ffmpeg.org/legal.html) and the distribution's license notices.

## Build from source

Requirements: Windows, the .NET 8 SDK, and FFmpeg/FFprobe available locally as described above.

```powershell
dotnet build .\VideoTrimmer.csproj -c Release
dotnet publish .\VideoTrimmer.csproj -c Release -r win-x64 --self-contained true
```

The project targets `net8.0-windows` and requires Windows Forms. `dotnet publish` output is written under `bin\Release\net8.0-windows\win-x64\publish`. Place `ffmpeg.exe` and `ffprobe.exe` next to the published executable, or ensure they are on `PATH`.

## Trimming behavior

Stream copy avoids video re-encoding and is fast, but it can only start cleanly on a keyframe. The output may therefore begin slightly before the selected time. Exact frame mode re-encodes video to honor the requested frame boundaries; it is slower and the output can differ in quality. Always review exported media before relying on it.

## Repository contents

- `MainForm.cs`, `Program.cs`, and `VideoTrimmer.csproj`: application source and project definition.
- `tools/README.md`: notes about optional local FFmpeg executables.
- `UPDATE.md`: project history and current packaging notes.

Generated build folders, published binaries, and the local sample video are excluded from Git.
