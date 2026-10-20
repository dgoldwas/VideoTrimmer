# Optional FFmpeg tools

This folder may contain local copies of `ffmpeg.exe` and `ffprobe.exe` for development or packaging. The executables are not tracked by Git because each is larger than 100 MB. Obtain a Windows FFmpeg distribution from a source you trust and review its licensing and notices. Alternatively, install FFmpeg separately and put its `bin` directory on `PATH`.

The application looks for each executable beside the app, in a `tools` subfolder beside the app, and then on `PATH`.
