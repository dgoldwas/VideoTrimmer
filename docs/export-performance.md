# Segment export investigation

The comparison used installed HandBrake 1.11.2, its saved QuickBounce preset, a controlled synthetic source, and the user's 4K network movie and saved frame ranges. Benchmarks used bounded probes and seeks; the source movie was never copied or converted in full.

## Confirmed causes

1. **An attached JPEG poster stalled same-container re-encoding.** The old mapping (`-map 0 -map -0:d`) tried to encode the poster as a second H.264 video through the HDR filter. A two-second selected-range run produced no output before being stopped at 60 seconds. Repeating the original command with only that poster excluded completed in 9.57 seconds. Exact exports now select `0:V:0`, retain audio/subtitle/font streams as applicable, and copy subtitles/fonts separately.
2. **Sparse subtitle streams caused read-ahead and misleading progress.** Adding an input duration limit to the revised command reduced the two-second run from 9.76 to 3.95 seconds. Reported output time changed from 94.519 to 2.000 seconds. Exact cuts now use `-ss start -t length -i source -t length`; progress is derived from encoded video frames.
3. **The saved HandBrake preset does less work.** QuickBounce uses x264 veryfast, RF 24, a 1920×1080 limit and colorspace conversion off. VideoTrimmer previously used medium, CRF 23, source resolution and mandatory SDR tone mapping. These are not equivalent quality or processing settings. VideoTrimmer now offers an explicit 1080p limit and uses veryfast for Balanced, retaining source resolution as the default.

## Actual-source measurements

The source is HEVC 3840×2160, 24000/1001 fps, 10-bit BT.2020/PQ, Dolby Vision profile 8 with an HDR10-compatible base layer. It has TrueHD 7.1, AC3 5.1, two SRT tracks, and the attached poster.

The first saved cut is 249 frames (10.385375 seconds). With revised stream mapping, input limits, DV-aware libplacebo and x264 veryfast CRF 23:

| Output | Wall time |
| --- | ---: |
| Source resolution (3840×2160) | 10.50 s |
| 1080p, resized before tone mapping | 7.99 s |

Both outputs contain all 249 frames, both audio tracks converted to 48 kHz stereo AAC at 192 kbps, and both SRT tracks. Only the poster is excluded. These are single-run timings on the local RTX 4070 machine over SMB; caching, drivers and system load affect them. CPU HDR10-base and DV-aware measurements use different grading paths and must not be described as identical images.

## HandBrake's seek strategy

HandBrake seeks to a preceding keyframe and discards the short lead-in, the same strategy used by FFmpeg input-side `-ss`. It does not need to decode the preceding hours of the movie. Replacing the seek strategy was not the needed fix.

Primary sources pinned to the installed version:

- [HandBrake reader: segment start and seek](https://github.com/HandBrake/HandBrake/blob/1.11.2/libhb/reader.c#L179)
- [HandBrake FFmpeg demuxer seek](https://github.com/HandBrake/HandBrake/blob/1.11.2/libhb/stream.c#L6608)
- [HandBrake colorspace and tone-map pipeline](https://github.com/HandBrake/HandBrake/blob/1.11.2/libhb/colorspace.c#L117)

Detailed local logs, commands, source metadata and test outputs are under `artifacts/handbrake-investigation/` and are not committed or shipped.
