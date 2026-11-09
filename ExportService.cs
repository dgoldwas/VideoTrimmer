using System.Globalization;
using System.Text;

namespace VideoTrimmer;

/// <summary>One FFmpeg run in an export. <see cref="Seconds"/> is the output length it reports progress against.</summary>
readonly record struct ExportStep(string[] Arguments, double Seconds, double Weight);

/// <summary>
/// The FFmpeg runs that produce <see cref="Outputs"/>. Multi-segment fast copies write parts into <see cref="WorkDirectory"/>
/// and join them with the concat demuxer, using <see cref="ListFile"/>.
/// </summary>
sealed record ExportPlan(IReadOnlyList<ExportStep> Steps, string? WorkDirectory, string? ListFile, string? ListText, IReadOnlyList<string> Outputs);

static class ExportService
{
    public static string[] BuildArguments(
        string input,
        string output,
        double inSeconds,
        double outSeconds,
        bool exact,
        ExactExportOptions? exactOptions = null)
    {
        var start = Text(inSeconds);
        var length = Text(outSeconds - inSeconds);
        // Exact mode seeks after -i so the cut lands on the chosen frames. Fast copy seeks before -i.
        if (!exact)
        {
            return
            [
                "-y", "-progress", "pipe:1", "-nostats",
                "-ss", start,
                "-i", input,
                "-t", length,
                "-map", "0",
                "-c", "copy",
                "-avoid_negative_ts", "make_zero",
                output,
            ];
        }

        var options = exactOptions ?? ExactExportOptions.Default;
        var (crf, preset) = ExactExportPresets.Encoder(options.Quality);
        var audioCodec = options.CopyAudio ? "copy" : "aac";
        return
        [
            "-y", "-progress", "pipe:1", "-nostats",
            "-i", input,
            "-ss", start,
            "-t", length,
            "-map", "0",
            "-c:v", "libx264",
            "-crf", crf.ToString(CultureInfo.InvariantCulture),
            "-preset", preset,
            "-c:a", audioCodec,
            output,
        ];
    }

    /// <summary>
    /// Plans the export of <paramref name="segments"/> (normalized frame ranges) into one file.
    /// One segment uses the single-cut arguments above, so it behaves exactly as before.
    /// </summary>
    public static ExportPlan Plan(
        string input,
        string output,
        IReadOnlyList<Segment> segments,
        double fps,
        bool hasAudio,
        bool exact,
        ExactExportOptions? exactOptions = null)
    {
        if (segments.Count == 0) throw new ArgumentException("Nothing is selected to export.", nameof(segments));
        double Sec(long frame) => frame / fps;
        var total = segments.Sum(s => Sec(s.Out) - Sec(s.In));
        if (segments.Count == 1)
        {
            var only = segments[0];
            return new ExportPlan([new ExportStep(BuildArguments(input, output, Sec(only.In), Sec(only.Out), exact, exactOptions), total, 1)], null, null, null, [output]);
        }

        if (exact)
            return new ExportPlan([new ExportStep(BuildExactConcat(input, output, segments, fps, hasAudio, exactOptions ?? ExactExportOptions.Default), total, 1)], null, null, null, [output]);

        // Fast copy: copy each part beside the output (same drive, so no cross-volume copies), then join them losslessly.
        var directory = Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".";
        var work = Path.Combine(directory, "." + Path.GetFileName(output) + ".parts");
        var extension = Path.GetExtension(output);
        var steps = new List<ExportStep>();
        var list = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var name = $"part{i + 1:000}{extension}";
            var seconds = Sec(segments[i].Out) - Sec(segments[i].In);
            steps.Add(new ExportStep(BuildArguments(input, Path.Combine(work, name), Sec(segments[i].In), Sec(segments[i].Out), exact: false), seconds, seconds));
            list.Append("file '").Append(name).Append("'\n");
        }

        var listFile = Path.Combine(work, "parts.txt");
        // Joining rewrites every byte once more, which costs roughly a third of copying the parts.
        steps.Add(new ExportStep(
        [
            "-y", "-progress", "pipe:1", "-nostats",
            "-f", "concat", "-safe", "0",
            "-i", listFile,
            "-map", "0",
            "-c", "copy",
            output,
        ], total, total / 3));
        return new ExportPlan(steps, work, listFile, list.ToString(), [output]);
    }

    /// <summary>The file names for saving each segment on its own: name_01.ext, name_02.ext, ...</summary>
    public static IReadOnlyList<string> SeparateOutputs(string output, int count)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".";
        var name = Path.GetFileNameWithoutExtension(output);
        var extension = Path.GetExtension(output);
        var digits = Math.Max(2, count.ToString(CultureInfo.InvariantCulture).Length);
        return Enumerable.Range(1, count)
            .Select(i => Path.Combine(directory, $"{name}_{i.ToString("D" + digits, CultureInfo.InvariantCulture)}{extension}"))
            .ToList();
    }

    /// <summary>Saves each segment as its own file, using the single-cut arguments for every one.</summary>
    public static ExportPlan PlanSeparate(
        string input,
        string output,
        IReadOnlyList<Segment> segments,
        double fps,
        bool exact,
        ExactExportOptions? exactOptions = null)
    {
        if (segments.Count == 0) throw new ArgumentException("Nothing is selected to export.", nameof(segments));
        var outputs = SeparateOutputs(output, segments.Count);
        var steps = segments.Select((segment, i) =>
        {
            var seconds = (segment.Out - segment.In) / fps;
            return new ExportStep(BuildArguments(input, outputs[i], segment.In / fps, segment.Out / fps, exact, exactOptions), seconds, seconds);
        }).ToList();
        return new ExportPlan(steps, null, null, null, outputs);
    }

    /// <summary>
    /// One re-encode that trims each segment by frame number and joins them with the concat filter.
    /// Only the first video and audio tracks survive a filter graph, and the audio has to be re-encoded.
    /// </summary>
    static string[] BuildExactConcat(string input, string output, IReadOnlyList<Segment> segments, double fps, bool hasAudio, ExactExportOptions options)
    {
        var n = segments.Count;
        var graph = new StringBuilder();
        graph.Append("[0:v:0]split=").Append(n);
        for (var i = 0; i < n; i++) graph.Append("[vs").Append(i).Append(']');
        graph.Append(';');
        for (var i = 0; i < n; i++)
            graph.Append($"[vs{i}]trim=start_frame={segments[i].In}:end_frame={segments[i].Out},setpts=PTS-STARTPTS[v{i}];");
        if (hasAudio)
        {
            graph.Append("[0:a:0]asplit=").Append(n);
            for (var i = 0; i < n; i++) graph.Append("[as").Append(i).Append(']');
            graph.Append(';');
            for (var i = 0; i < n; i++)
                graph.Append($"[as{i}]atrim=start={Text(segments[i].In / fps)}:end={Text(segments[i].Out / fps)},asetpts=PTS-STARTPTS[a{i}];");
        }

        for (var i = 0; i < n; i++)
        {
            graph.Append("[v").Append(i).Append(']');
            if (hasAudio) graph.Append("[a").Append(i).Append(']');
        }

        graph.Append("concat=n=").Append(n).Append(":v=1:a=").Append(hasAudio ? 1 : 0).Append("[v]");
        if (hasAudio) graph.Append("[a]");

        var (crf, preset) = ExactExportPresets.Encoder(options.Quality);
        var args = new List<string>
        {
            "-y", "-progress", "pipe:1", "-nostats",
            "-i", input,
            "-filter_complex", graph.ToString(),
            "-map", "[v]",
        };
        if (hasAudio) args.AddRange(["-map", "[a]"]);
        args.AddRange(["-c:v", "libx264", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-preset", preset]);
        if (hasAudio) args.AddRange(["-c:a", "aac"]);
        args.Add(output);
        return args.ToArray();
    }

    static string Text(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A canceled ffmpeg process can keep the file open for a moment.
        }
    }

    public static void TryDeleteDirectory(string? path)
    {
        if (path is null) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A canceled ffmpeg process can keep a part open for a moment.
        }
    }

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        var fast = BuildArguments("in.mp4", "out.mp4", 1, 5, exact: false);
        Expect(fast.Contains("-c") && fast.Contains("copy"), "fast copy keeps stream copy");
        Expect(!fast.Contains("-crf"), "fast copy has no crf");

        var balanced = BuildArguments("in.mp4", "out.mp4", 1, 5, exact: true, ExactExportOptions.Default);
        Expect(balanced.Contains("-crf") && balanced.Contains("23"), "balanced crf 23");
        Expect(balanced.Contains("medium"), "balanced preset");
        Expect(balanced.Contains("aac"), "default audio aac");

        var high = BuildArguments("in.mp4", "out.mp4", 1, 5, exact: true, new ExactExportOptions(ExactQuality.High, CopyAudio: true));
        Expect(high.Contains("18") && high.Contains("slow"), "high quality encoder");
        var copyIndex = Array.IndexOf(high, "-c:a");
        Expect(copyIndex >= 0 && copyIndex + 1 < high.Length && high[copyIndex + 1] == "copy", "copy audio");

        var small = BuildArguments("in.mp4", "out.mp4", 1, 5, exact: true, new ExactExportOptions(ExactQuality.Small, false));
        Expect(small.Contains("28") && small.Contains("veryfast"), "small quality encoder");

        var single = Plan("in.mp4", "out.mp4", [new Segment(30, 150)], 30, hasAudio: true, exact: false);
        Expect(single.Steps.Count == 1 && single.WorkDirectory is null, "one segment is a single run");
        Expect(single.Steps[0].Arguments.SequenceEqual(BuildArguments("in.mp4", "out.mp4", 1, 5, exact: false)), "one segment matches the single-cut arguments");

        Segment[] two = [new(0, 60), new(300, 330)];
        var exactPlan = Plan("in.mp4", "out.mp4", two, 30, hasAudio: true, exact: true);
        var graph = exactPlan.Steps[0].Arguments[Array.IndexOf(exactPlan.Steps[0].Arguments, "-filter_complex") + 1];
        Expect(exactPlan.Steps.Count == 1, "exact multi-cut is one run");
        Expect(graph.Contains("trim=start_frame=300:end_frame=330") && graph.Contains("atrim=start=10:end=11"), "exact trims by frame and time");
        Expect(graph.EndsWith("concat=n=2:v=1:a=1[v][a]"), "exact joins video and audio");
        Expect(Math.Abs(exactPlan.Steps[0].Seconds - 3) < 1e-9, "exact progress covers the kept length");
        var silent = Plan("in.mp4", "out.mp4", two, 30, hasAudio: false, exact: true).Steps[0].Arguments;
        Expect(!silent.Contains("[a]") && !string.Join(" ", silent).Contains("atrim"), "no audio graph without audio");

        var fastPlan = Plan("in.mp4", Path.Combine("C:\\clips", "out.mp4"), two, 30, hasAudio: true, exact: false);
        Expect(fastPlan.Steps.Count == 3, "fast multi-cut copies each part, then joins");
        Expect(fastPlan.WorkDirectory == Path.Combine("C:\\clips", ".out.mp4.parts"), "parts sit beside the output");
        Expect(fastPlan.ListText == "file 'part001.mp4'\nfile 'part002.mp4'\n", "concat list names the parts");
        Expect(fastPlan.Steps[^1].Arguments.Contains("concat") && fastPlan.Steps[^1].Arguments[^1] == Path.Combine("C:\\clips", "out.mp4"), "join writes the output");

        var showPath = Path.GetFullPath("show.mkv");
        var separate = PlanSeparate("in.mp4", showPath, two, 30, exact: true, new ExactExportOptions(ExactQuality.High, CopyAudio: true));
        Expect(separate.Outputs.Select(Path.GetFileName).SequenceEqual(["show_01.mkv", "show_02.mkv"]), "separate files are numbered");
        Expect(separate.Steps.Count == 2 && separate.Steps[1].Arguments.SequenceEqual(BuildArguments("in.mp4", separate.Outputs[1], 10, 11, exact: true, new ExactExportOptions(ExactQuality.High, CopyAudio: true))), "each separate file uses the single-cut arguments");
        Expect(Path.GetFileName(SeparateOutputs("a.mp4", 120)[0]) == "a_001.mp4", "numbering widens past 99");
        return errors;
    }
}
