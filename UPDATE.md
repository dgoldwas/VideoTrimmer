# Updates

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

## 2026-10-19

- Prepared the source project for the private GitHub repository.
- Added repository and build documentation, plus ignore rules for generated output and the local sample clip.
- Documented that FFmpeg and FFprobe are local runtime dependencies and are not stored in Git because each executable exceeds GitHub's 100 MB regular file limit.
- The app supported drag-and-drop or browse-to-open, timeline thumbnails, frame/timecode entry, stream-copy export, and an optional exact-frame re-encode mode.

## Packaging notes

The current source targets Windows and .NET 8 WinForms. A published self-contained executable still requires FFmpeg and FFprobe alongside it or available on `PATH`. No installer or automated release workflow is included yet.
