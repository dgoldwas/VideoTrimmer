using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace VideoTrimmer;

enum PlaybackEnd { Stopped, Finished, Failed }

/// <summary>
/// Plays the kept segments back to back, the way the export will join them. Each segment is decoded by its own
/// FFmpeg run (raw frames at preview size, and 48 kHz PCM for NAudio). The runs are fetched ahead of playback so
/// the joins do not stall. The audio output is the clock that frames are shown against.
/// </summary>
sealed class EditPlayer : IDisposable
{
    const int AudioRate = 48000;
    const int FrameBudgetBytes = 64 * 1024 * 1024;
    static readonly TimeSpan AudioAhead = TimeSpan.FromSeconds(2);

    readonly FfmpegRunner runner;
    readonly PictureBox target;
    readonly System.Windows.Forms.Timer tick = new() { Interval = 10 };
    readonly Bitmap?[] surfaces = new Bitmap?[2];
    int surfaceIndex;
    Session? session;

    public EditPlayer(FfmpegRunner runner, PictureBox target)
    {
        this.runner = runner;
        this.target = target;
        tick.Tick += (_, _) => OnTick();
    }

    readonly record struct Piece(long In, long Out, double Offset);
    readonly record struct Frame(byte[] Pixels, double Time, long Source);

    sealed class Session
    {
        public required CancellationTokenSource Cancel;
        public required ConcurrentQueue<Frame> Frames;
        public required SemaphoreSlim Space;
        public required double Total;
        public required int FrameBytes;
        public BufferedWaveProvider? Audio;
        public WaveOutEvent? Output;
        public readonly Stopwatch Clock = new();
        public volatile bool VideoDone;
        public volatile bool AudioDone;
        public volatile Exception? Failure;
        public bool Started;
    }

    public bool IsPlaying => session is not null;

    /// <summary>The source frame now on screen.</summary>
    public event Action<long>? FrameShown;
    public event Action<PlaybackEnd, string?>? Ended;

    /// <summary>Starts at <paramref name="startFrame"/>, or at the next kept frame when it falls in a cut.</summary>
    public bool Play(string input, IReadOnlyList<Segment> segments, double fps, long startFrame, Size size, bool hasAudio)
    {
        Stop();
        if (runner.Ffmpeg is null || fps <= 0) return false;
        var pieces = Plan(segments, startFrame, fps);
        if (pieces.Count == 0) return false;
        EnsureSurfaces(size);
        var frameBytes = size.Width * size.Height * 4;
        var current = new Session
        {
            Cancel = new CancellationTokenSource(),
            Frames = new ConcurrentQueue<Frame>(),
            Space = new SemaphoreSlim(Math.Clamp(FrameBudgetBytes / frameBytes, 12, 90)),
            Total = pieces[^1].Offset + (pieces[^1].Out - pieces[^1].In) / fps,
            FrameBytes = frameBytes,
        };
        if (hasAudio)
        {
            current.Audio = new BufferedWaveProvider(new WaveFormat(AudioRate, 16, 2))
            {
                BufferDuration = TimeSpan.FromSeconds(6),
                ReadFully = true,
                DiscardOnBufferOverflow = false,
            };
            try
            {
                current.Output = new WaveOutEvent { DesiredLatency = 120 };
                current.Output.Init(current.Audio);
            }
            catch (Exception)
            {
                // No audio device: play silently against the stopwatch.
                current.Output?.Dispose();
                current.Output = null;
                current.Audio = null;
            }
        }

        session = current;
        _ = ProduceVideoAsync(current, input, pieces, fps, size);
        if (current.Audio is not null) _ = ProduceAudioAsync(current, input, pieces, fps);
        else current.AudioDone = true;
        tick.Start();
        return true;
    }

    public void Stop() => Finish(PlaybackEnd.Stopped, null);

    void Finish(PlaybackEnd end, string? error)
    {
        var current = session;
        if (current is null) return;
        session = null;
        tick.Stop();
        try { current.Cancel.Cancel(); } catch (ObjectDisposedException) { }
        current.Output?.Stop();
        current.Output?.Dispose();
        while (current.Frames.TryDequeue(out var frame))
            ArrayPool<byte>.Shared.Return(frame.Pixels);
        Ended?.Invoke(end, error);
    }

    void OnTick()
    {
        var current = session;
        if (current is null) return;
        if (current.Failure is { } failure)
        {
            Finish(PlaybackEnd.Failed, failure.Message);
            return;
        }

        if (!current.Started)
        {
            // Wait for a little of each stream so the first frames and the first sound start together.
            var videoReady = current.VideoDone || current.Frames.Count >= 6;
            var audioReady = current.Audio is null || current.AudioDone || current.Audio.BufferedDuration >= TimeSpan.FromMilliseconds(300);
            if (!videoReady || !audioReady) return;
            current.Started = true;
            current.Clock.Start();
            current.Output?.Play();
        }

        var now = Now(current);
        Frame? show = null;
        while (current.Frames.TryPeek(out var next) && next.Time <= now)
        {
            if (!current.Frames.TryDequeue(out next)) break;
            current.Space.Release();
            if (show is { } skipped) ArrayPool<byte>.Shared.Return(skipped.Pixels);
            show = next;
        }

        if (show is { } frame)
        {
            Paint(frame, current.FrameBytes);
            ArrayPool<byte>.Shared.Return(frame.Pixels);
            FrameShown?.Invoke(frame.Source);
        }

        if (current.VideoDone && current.Frames.IsEmpty && now >= current.Total)
            Finish(PlaybackEnd.Finished, null);
    }

    static double Now(Session current)
    {
        if (current.Output is { } output && current.Audio is { } audio)
            return output.GetPosition() / (double)audio.WaveFormat.AverageBytesPerSecond;
        return current.Clock.Elapsed.TotalSeconds;
    }

    void Paint(Frame frame, int frameBytes)
    {
        surfaceIndex ^= 1;
        var bitmap = surfaces[surfaceIndex];
        if (bitmap is null) return;
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            Marshal.Copy(frame.Pixels, 0, data.Scan0, frameBytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        target.Image = bitmap;
    }

    void EnsureSurfaces(Size size)
    {
        if (surfaces[0]?.Size == size) return;
        foreach (var old in surfaces)
        {
            if (old is null) continue;
            if (target.Image == old) target.Image = null;
            old.Dispose();
        }

        surfaces[0] = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        surfaces[1] = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
    }

    async Task ProduceVideoAsync(Session current, string input, IReadOnlyList<Piece> pieces, double fps, Size size)
    {
        var token = current.Cancel.Token;
        try
        {
            foreach (var piece in pieces)
            {
                var count = piece.Out - piece.In;
                string[] args =
                [
                    "-nostdin", "-hide_banner", "-loglevel", "error",
                    "-ss", Seconds(piece.In / fps),
                    "-i", input,
                    "-map", "0:v:0", "-an", "-sn", "-dn",
                    "-frames:v", count.ToString(CultureInfo.InvariantCulture),
                    "-vf", $"scale={size.Width}:{size.Height}:flags=bilinear,setsar=1,format=bgr0",
                    "-f", "rawvideo", "pipe:1",
                ];
                await runner.StreamAsync(runner.Ffmpeg!, args, async (stdout, ct) =>
                {
                    for (long i = 0; i < count; i++)
                    {
                        await current.Space.WaitAsync(ct).ConfigureAwait(false);
                        var pixels = ArrayPool<byte>.Shared.Rent(current.FrameBytes);
                        try
                        {
                            await stdout.ReadExactlyAsync(pixels.AsMemory(0, current.FrameBytes), ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            ArrayPool<byte>.Shared.Return(pixels);
                            current.Space.Release();
                            if (ct.IsCancellationRequested) throw;
                            // The segment ran out of frames early (end of file); go on to the next one.
                            return;
                        }

                        current.Frames.Enqueue(new Frame(pixels, piece.Offset + i / fps, piece.In + i));
                    }
                }, null, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            current.Failure = ex;
        }
        finally
        {
            current.VideoDone = true;
        }
    }

    async Task ProduceAudioAsync(Session current, string input, IReadOnlyList<Piece> pieces, double fps)
    {
        var token = current.Cancel.Token;
        var audio = current.Audio!;
        var block = new byte[16384];
        try
        {
            foreach (var piece in pieces)
            {
                var seconds = (piece.Out - piece.In) / fps;
                // Exactly the segment's length, so the next segment's sound starts with its first frame.
                var expected = (long)Math.Round(seconds * AudioRate) * 4;
                long written = 0;
                string[] args =
                [
                    "-nostdin", "-hide_banner", "-loglevel", "error",
                    "-ss", Seconds(piece.In / fps),
                    "-i", input,
                    "-map", "0:a:0", "-vn", "-sn", "-dn",
                    "-t", Seconds(seconds),
                    "-ac", "2", "-ar", AudioRate.ToString(CultureInfo.InvariantCulture),
                    "-f", "s16le", "pipe:1",
                ];
                try
                {
                    await runner.StreamAsync(runner.Ffmpeg!, args, async (stdout, ct) =>
                    {
                        int read;
                        while ((read = await stdout.ReadAsync(block, ct).ConfigureAwait(false)) > 0)
                        {
                            var take = (int)Math.Min(read, expected - written);
                            if (take <= 0) continue;
                            await WaitForRoom(audio, ct).ConfigureAwait(false);
                            audio.AddSamples(block, 0, take);
                            written += take;
                        }
                    }, null, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A segment whose audio can't be decoded plays as silence rather than stopping playback.
                }

                Array.Clear(block);
                while (written < expected)
                {
                    var take = (int)Math.Min(block.Length, expected - written);
                    await WaitForRoom(audio, token).ConfigureAwait(false);
                    audio.AddSamples(block, 0, take);
                    written += take;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            current.AudioDone = true;
        }
    }

    static async Task WaitForRoom(BufferedWaveProvider audio, CancellationToken token)
    {
        while (audio.BufferedDuration > AudioAhead)
            await Task.Delay(25, token).ConfigureAwait(false);
    }

    static List<Piece> Plan(IReadOnlyList<Segment> segments, long startFrame, double fps)
    {
        var pieces = new List<Piece>();
        if (segments.Count == 0) return pieces;
        // From the last kept frame (or past it), play the edit from the top.
        var first = segments.ToList().FindIndex(s => s.Out - 1 > startFrame);
        if (first < 0)
        {
            first = 0;
            startFrame = segments[0].In;
        }

        var offset = 0.0;
        for (var i = first; i < segments.Count; i++)
        {
            var start = i == first ? Math.Max(segments[i].In, startFrame) : segments[i].In;
            if (segments[i].Out <= start) continue;
            pieces.Add(new Piece(start, segments[i].Out, offset));
            offset += (segments[i].Out - start) / fps;
        }

        return pieces;
    }

    static string Seconds(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        Stop();
        tick.Dispose();
        foreach (var surface in surfaces)
            surface?.Dispose();
    }

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        Segment[] kept = [new(0, 60), new(300, 330)];
        var fromGap = Plan(kept, 100, 30);
        Expect(fromGap.Count == 1 && fromGap[0] == new Piece(300, 330, 0), "a playhead in a cut starts at the next segment");
        var fromMiddle = Plan(kept, 30, 30);
        Expect(fromMiddle.Count == 2 && fromMiddle[0] == new Piece(30, 60, 0) && Math.Abs(fromMiddle[1].Offset - 1) < 1e-9, "play from inside a segment, then join the next");
        var fromEnd = Plan(kept, 329, 30);
        Expect(fromEnd.Count == 2 && fromEnd[0].In == 0, "at the end, play the edit from the top");
        return errors;
    }
}
