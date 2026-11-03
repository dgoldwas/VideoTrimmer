# VideoTrimmer

A small Windows desktop app for selecting and exporting a section of a video. VideoTrimmer is built with C# and WinForms on .NET 8. It uses FFmpeg and FFprobe as separate processes, so it can inspect and trim large files without loading the full video into memory.

## Features

- Open a video by browsing or drag and drop.
- Scrub a filmstrip. Drag the in and out handles, or type a timecode and a frame number together.
- The preview fills the window and follows the playhead.
- Export a fast stream copy, or re-encode for exact frames. Fast copy can start slightly early, on a keyframe.
- Exact mode offers High / Balanced / Small quality presets and an optional copy-audio setting.
- Preserve mapped streams in fast-copy mode (`-map 0`), including audio and subtitles supported by the selected output container.

## Controls

- Click the filmstrip to move the playhead. Drag the handles to set the cut. Drag the highlighted span to slide it.
- Shift-click sets the in point. Alt-click sets the out point so the current frame is included.
- `I` and `O` do the same at the playhead. Left and Right step one frame. Shift plus an arrow steps one second. Home and End jump to the selection edges. `[` and `]` nudge the edge nearest the playhead.
- The mouse wheel steps one frame while the pointer is over the preview or the filmstrip.

## Run the app

Download or build a Windows version of FFmpeg that includes both `ffmpeg.exe` and `ffprobe.exe`. Put both executables beside `VideoTrimmer.exe`, in a `tools` subfolder beside it, or in a directory on `PATH`. Then launch `VideoTrimmer.exe`.

The FFmpeg executables used during development are intentionally not committed: each is over 100 MB. The app can also use a local FFmpeg installation. FFmpeg builds may have their own licensing terms; see [FFmpeg Legal](https://ffmpeg.org/legal.html) and the distribution's license notices.

## Build from source

Requirements: Windows, the .NET 8 SDK, and FFmpeg/FFprobe available locally as described above.

```powershell
dotnet build .\VideoTrimmer.csproj -c Release
dotnet publish .\VideoTrimmer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\win-x64
Copy-Item .\tools\ffmpeg.exe, .\tools\ffprobe.exe .\publish\win-x64\ -Force
```

The project targets `net8.0-windows` and requires Windows Forms. The portable folder is `publish\win-x64\` (gitignored): a single-file `VideoTrimmer.exe` with `ffmpeg.exe` and `ffprobe.exe` beside it. FFmpeg can also live in a `tools` subfolder or on `PATH`.

## Trimming behavior

Fast copy avoids video re-encoding and is quick, but it can only start cleanly on a keyframe. The output may therefore begin slightly before the selected time. Exact frames re-encodes video to honor the requested frame boundaries; it is slower and the output can differ in quality. Always review exported media before relying on it.

## Repository contents

- `MainForm.cs`, `Program.cs`, and `VideoTrimmer.csproj`: application entry, main window, and project definition.
- `Theme.cs`, `UiControls.cs`, `TimelineControl.cs`, `ExportModeCopy.cs`, `ExactExportOptions.cs`: dark theme, custom controls, filmstrip timeline, and export-mode copy/presets.
- `TimeMath.cs`, `FfmpegRunner.cs`, `MediaProbe.cs`, `StillCapture.cs`, `FilmstripService.cs`, `ExportService.cs`: frame math, FFmpeg helpers, probe, stills, filmstrip, and export args.
- `tools/README.md`: notes about optional local FFmpeg executables.
- `UPDATE.md`: changelog and packaging notes.

Generated build folders, published binaries, and the local sample video are excluded from Git.
