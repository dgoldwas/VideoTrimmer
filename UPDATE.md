# Updates

## 2026-11-09 (2026.11.09.01)

- **Multi-cut:** the timeline holds several kept segments. `S` splits at the playhead, `N` starts a new segment, and `Delete` removes the active one. A segment strip lists them as chips and shows the kept and cut totals. Cut regions are dimmed and hatched.
- Multi-segment export: fast copy copies each part and joins them with the concat demuxer. Exact frames uses one `trim`/`atrim` + `concat` re-encode. A single segment keeps the previous arguments exactly.
- **Smart timeline:** a waveform lane under the filmstrip, with silence spans (-40 dBFS, at least 0.4 s) and `scdet` scene changes found in the background. Handles snap to them, Ctrl overrides, and there is a **Snap to cuts** toggle. `,` and `.` jump between them.
- Installer: `build-installer.ps1` builds an Inno Setup installer for `C:\Program Files\VideoTrimmer`, with a pinned GPL FFmpeg (libx264), a Start menu entry, and an **Open with** registration. `-Install` and `-Release` install it or publish it to GitHub.
- Auto-update: the status bar's version button checks GitHub releases at startup and every six hours. It downloads and verifies the Setup.exe, installs after VideoTrimmer exits, and reopens the same video.
- Versioning: `<Version>` in the project (YYYY.MM.DD.##) is the single source. Added an app icon.
- `--check` now also covers segment merging, export plans, silence detection, scdet parsing, and release parsing.

## 2026-11-03

- Refactored the app into focused modules: `Theme`, `TimeMath`, `TimelineControl`, `UiControls`, and `FfmpegRunner`.
- Rebuilt the UI with a dark theme, custom controls, a filmstrip with in/out handles, and a preview that fills the free window space.
- Added keyboard and mouse shortcuts for setting points, stepping frames, sliding the selection, and nudging edges.
- Export shows progress and can be canceled. Fast-copy and exact-frame modes remain available.
- Accept a video path on the command line at startup. Added a `--check` self-test for timecode and frame math.
- Enabled Per-Monitor V2 high-DPI awareness.
- Updated the README feature and control docs to match the current UI.
- Still capture pipes JPEG from FFmpeg to memory (no temp files). Preview cache keys include frame width.
- Filmstrip cache keeps only the current strip size and clears release tracking on media change.
- Timeline paint uses bilinear scaling, cached brushes/pens, source-crop draws without per-slot clipping, and batched invalidates while thumbs load.
- Dispose replaced cancellation sources; skip in/out text-box writes while dragging the timeline; launch FFmpeg/FFprobe with `ArgumentList` instead of quoted argument strings.
- Extracted `MediaProbe`, `StillCapture`/`FrameCache`, `FilmstripService`, and `ExportService` from `MainForm`.
- Centralized Fast copy / Exact frames titles and explanations in `ExportModeCopy`.
- `FrameCache` tracks the live preview via `SetShown` so replace/trim cannot dispose it; covered by `--check`.
- Exact export adds High / Balanced / Small quality presets (CRF + x264 preset) and optional copy-audio; fast copy unchanged.

## 2026-10-19

- Prepared the source project for the private GitHub repository.
- Added repository and build documentation, plus ignore rules for generated output and the local sample clip.
- Documented that FFmpeg and FFprobe are local runtime dependencies and are not stored in Git because each executable exceeds GitHub's 100 MB regular file limit.
- The app supported drag-and-drop or browse-to-open, timeline thumbnails, frame/timecode entry, stream-copy export, and an optional exact-frame re-encode mode.

## Packaging notes

The current source targets Windows and .NET 8 WinForms. Releases are built with `build-installer.ps1` (see the README's Releases section) into `artifacts\` (gitignored). The older portable single-file build still works and is staged under `publish\win-x64\` with `ffmpeg.exe` and `ffprobe.exe` beside `VideoTrimmer.exe` (that folder is gitignored).

```powershell
dotnet publish .\VideoTrimmer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\win-x64
Copy-Item .\tools\ffmpeg.exe, .\tools\ffprobe.exe .\publish\win-x64\ -Force
```

GitHub releases are published from a local machine with `build-installer.ps1 -Release`; there is no CI release workflow.
