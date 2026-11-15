using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using VideoTrimmer;

static class ProjectExportVerification
{
    public static async Task Run(string project, string folder)
    {
        Directory.CreateDirectory(folder);
        var saved = CutList.Load(File.ReadAllText(project));
        var input = saved.Video ?? throw new Exception("The test project needs a video path.");
        var runner = new FfmpegRunner();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        var media = await MediaProbe.ProbeAsync(runner, input, token);
        var segments = CutList.Rescale(saved.Segments, saved.Fps, media.Fps).ToArray();
        var options = ExactExportOptions.Default with
        {
            Color = media.Color, Size = ExportSize.Hd1080,
            UseLibplacebo = media.Color.DolbyProfile > 0 && await HdrExport.SupportsPlaceboAsync(runner, token),
        };
        var results = new List<object>();
        var separate = ExportService.PlanSeparate(input, Path.Combine(folder, "separate.mkv"), segments, media.Fps, true, options);
        await Check("separate cuts", separate, segments.Select(s => s.Length).ToArray(), media.AudioCodecs.Count, media.Subtitles.Count);
        var joined = ExportService.Plan(input, Path.Combine(folder, "joined.mkv"), segments, media.Fps, media.HasAudio, true, options);
        await Check("stitched cuts", joined, [segments.Sum(s => s.Length)], media.HasAudio ? 1 : 0, 0);
        File.WriteAllText(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

        async Task Check(string name, ExportPlan plan, long[] frameCounts, int audioTracks, int subtitleTracks)
        {
            var clock = Stopwatch.StartNew();
            var maxProgress = TimeSpan.Zero;
            foreach (var step in plan.Steps)
            {
                var progress = new InlineProgress(t => { if (t > maxProgress) maxProgress = t; });
                await runner.RunAsync(runner.Ffmpeg!, step.Arguments, token, progress, media.Fps);
                if (maxProgress.TotalSeconds > step.Seconds + 0.1) throw new Exception(name + ": video progress overshot the cut.");
                maxProgress = TimeSpan.Zero;
            }
            clock.Stop();
            for (var i = 0; i < plan.Outputs.Count; i++)
            {
                var json = await runner.RunAsync(runner.Ffprobe!, ["-v", "error", "-count_frames", "-show_streams", "-show_format", "-of", "json", plan.Outputs[i]], token);
                using var doc = JsonDocument.Parse(json);
                var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                var video = streams.Single(s => s.GetProperty("codec_type").GetString() == "video");
                if (video.GetProperty("width").GetInt32() > 1920 || video.GetProperty("height").GetInt32() > 1080) throw new Exception(name + ": wrong output size.");
                if (video.GetProperty("nb_read_frames").GetString() != frameCounts[i].ToString(CultureInfo.InvariantCulture)) throw new Exception(name + ": wrong frame count.");
                if (video.GetProperty("color_transfer").GetString() != "bt709") throw new Exception(name + ": not tagged SDR.");
                var audio = streams.Where(s => s.GetProperty("codec_type").GetString() == "audio").ToArray();
                if (audio.Length != audioTracks || audio.Any(s => s.GetProperty("channels").GetInt32() != 2)) throw new Exception(name + ": wrong audio tracks/downmix.");
                if (streams.Count(s => s.GetProperty("codec_type").GetString() == "subtitle") != subtitleTracks) throw new Exception(name + ": wrong subtitle tracks.");
                File.WriteAllText(plan.Outputs[i] + ".probe.json", json);
            }
            results.Add(new { name, seconds = clock.Elapsed.TotalSeconds, frameCounts, plan.Outputs });
            Console.WriteLine($"{name}: exact frame counts, SDR, stereo and stream counts passed in {clock.Elapsed.TotalSeconds:0.00}s");
        }
    }

    sealed class InlineProgress(Action<TimeSpan> report) : IProgress<TimeSpan>
    {
        public void Report(TimeSpan value) => report(value);
    }
}
