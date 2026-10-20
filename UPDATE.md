# Updates

## 2026-10-19

- Prepared the source project for the private GitHub repository.
- Added repository and build documentation, plus ignore rules for generated output and the local sample clip.
- Documented that FFmpeg and FFprobe are local runtime dependencies and are not stored in Git because each executable exceeds GitHub's 100 MB regular file limit.
- The current app supports drag-and-drop or browse-to-open, timeline thumbnails, frame/timecode entry, stream-copy export, and an optional exact-frame re-encode mode.

## Packaging notes

The current source targets Windows and .NET 8 WinForms. A published self-contained executable still requires FFmpeg and FFprobe alongside it or available on `PATH`. No installer or automated release workflow is included yet.
