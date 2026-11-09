# VideoTrimmer

A small Windows desktop app for cutting a video down to the parts you want to keep. VideoTrimmer is built with C# and WinForms on .NET 8. It uses FFmpeg and FFprobe as separate processes, so it can inspect and trim large files without loading the full video into memory.

## Features

- Open a video by browsing, drag and drop, or Explorer's **Open with** menu.
- Scrub a filmstrip. Drag the in and out handles, or type a timecode and a frame number together.
- The preview fills the window and follows the playhead.
- **Multi-cut:** keep several segments of one video and export them joined into one file. Split a segment at the playhead, start a new one, or delete one. Everything outside the kept segments is hatched out. The segment strip shows each segment and the total kept and cut time.
- **Smart timeline:** an audio waveform runs under the filmstrip. Silences (violet) and scene changes (amber) are found automatically in the background. Handles and the playhead snap to them while you drag.
- Export a fast stream copy, or re-encode for exact frames. Fast copy can start slightly early, on a keyframe.
- Exact mode offers High / Balanced / Small quality presets and an optional copy-audio setting.
- Preserve mapped streams in fast-copy mode (`-map 0`), including audio and subtitles supported by the selected output container.
- Installs to `C:\Program Files\VideoTrimmer` and updates itself from this repository's GitHub releases.

## Controls

- Click the filmstrip to move the playhead. Drag the handles to set the cut. Drag the highlighted span to slide it.
- Shift-click sets the in point. Alt-click sets the out point so the current frame is included.
- `I` and `O` do the same at the playhead. Left and Right step one frame. Shift plus an arrow steps one second. Home and End jump to the selection edges. `[` and `]` nudge the edge nearest the playhead.
- The mouse wheel steps one frame while the pointer is over the preview or the filmstrip.
- `S` splits the segment under the playhead. `N` starts a new segment at the playhead that runs to the next kept segment or the end. `Delete` removes the active segment.
- Click a chip in the segment strip, or double-click a dashed segment on the filmstrip, to make it the one the handles edit. The chip's `×` removes it. Dragging a segment into another one merges them when you let go.
- `,` and `.` jump the playhead to the previous and next scene change or silence edge. Hold Ctrl while dragging to place a handle without snapping, or turn **Snap to cuts** off in the toolbar.

To remove an ad break, for example: jump to the silence before it and press `S`. Jump to the silence after it and press `S` again. Then click the middle chip and press `Delete`.

## Install

Run `VideoTrimmer-<version>-Setup.exe` from the [latest release](https://github.com/dgoldwas/VideoTrimmer/releases). It installs to `C:\Program Files\VideoTrimmer` with a bundled FFmpeg, adds a Start menu entry, and offers VideoTrimmer in Explorer's **Open with** menu for common video types.

The version button at the right of the status bar shows the running version. VideoTrimmer checks GitHub for a newer release shortly after it starts and every six hours. When one exists, the button changes to **Update to …**. Click it to download the installer, check it against GitHub's SHA-256, close VideoTrimmer, install through a Windows permission prompt, and reopen with the same video. Click the version at any time to check now.

The repository is private, so the update check needs GitHub access. It uses `VIDEOTRIMMER_GITHUB_TOKEN`, `GH_TOKEN` or `GITHUB_TOKEN` when set, otherwise the GitHub CLI's sign-in (`gh auth login`).

## Run from source

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

## Releases

The project's `<Version>` (YYYY.MM.DD.##) is the only place the version is set. Bump it, then:

```powershell
.\build-installer.ps1            # publish, bundle FFmpeg, build artifacts\VideoTrimmer-<version>-Setup.exe
.\build-installer.ps1 -Install   # ...and install it into Program Files
.\build-installer.ps1 -Release   # ...and publish it as GitHub release v<version>, which installed copies pick up
```

`build.ps1` runs the `--check` self-test on the published exe and bundles a pinned, checksum-verified GPL FFmpeg build from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) into `tools\`. Exact-frame export needs libx264, which LGPL builds leave out. The installer needs [Inno Setup 6](https://jrsoftware.org/isinfo.php). The icon comes from `assets\make-icon.ps1`.

## Trimming behavior

Fast copy avoids video re-encoding and is quick, but it can only start cleanly on a keyframe. The output may therefore begin slightly before the selected time. Exact frames re-encodes video to honor the requested frame boundaries; it is slower and the output can differ in quality. Always review exported media before relying on it.

With several segments, fast copy copies each segment beside the output (in a hidden `.<name>.parts` folder that is removed afterwards) and joins them with FFmpeg's concat demuxer, so every segment can start on an earlier keyframe. Exact frames trims each segment by frame number and joins them in one re-encode. That re-encode keeps only the first video and audio track, and it always encodes the audio to AAC.

Scene changes come from FFmpeg's `scdet` filter run over a 160-pixel-wide decode. Silences are spans quieter than -40 dBFS for at least 0.4 seconds. Both passes only read the source file.

## Repository contents

- `MainForm.cs`, `Program.cs`, and `VideoTrimmer.csproj`: application entry, main window, and project definition.
- `Theme.cs`, `UiControls.cs`, `TimelineControl.cs`, `ExportModeCopy.cs`, `ExactExportOptions.cs`: dark theme, custom controls, filmstrip timeline, and export-mode copy/presets.
- `TimeMath.cs`, `FfmpegRunner.cs`, `MediaProbe.cs`, `StillCapture.cs`, `FilmstripService.cs`, `ExportService.cs`: frame math, FFmpeg helpers, probe, stills, filmstrip, and export plans.
- `CutList.cs`, `SmartAnalysis.cs`: multi-cut segments, and the waveform/silence/scene-change analysis.
- `UpdateService.cs`, `MainForm.Updates.cs`: GitHub release checks and the self-update flow.
- `build.ps1`, `build-installer.ps1`, `installer\VideoTrimmer.iss`, `assets\`: release build, installer, and icon.
- `tools/README.md`: notes about optional local FFmpeg executables.
- `UPDATE.md`: changelog and packaging notes.

Generated build folders, published binaries, and the local sample video are excluded from Git.
