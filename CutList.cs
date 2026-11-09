namespace VideoTrimmer;

/// <summary>A kept range of frames. Out is exclusive, like the trim range.</summary>
readonly record struct Segment(long In, long Out)
{
    public long Length => Out - In;
    public bool Contains(long frame) => frame >= In && frame < Out;
    public bool Overlaps(Segment other) => In < other.Out && other.In < Out;
}

/// <summary>Helpers for the list of segments a multi-cut export keeps.</summary>
static class CutList
{
    /// <summary>Clamps to the clip, drops empty segments, sorts, and joins segments that overlap or touch.</summary>
    public static List<Segment> Normalize(IEnumerable<Segment> segments, long total)
    {
        var sorted = segments
            .Select(s => new Segment(Math.Clamp(s.In, 0, total), Math.Clamp(s.Out, 0, total)))
            .Where(s => s.Out > s.In)
            .OrderBy(s => s.In)
            .ToList();
        var merged = new List<Segment>();
        foreach (var segment in sorted)
        {
            if (merged.Count > 0 && segment.In <= merged[^1].Out)
                merged[^1] = merged[^1] with { Out = Math.Max(merged[^1].Out, segment.Out) };
            else
                merged.Add(segment);
        }

        return merged;
    }

    /// <summary>The ranges of the clip that no segment keeps.</summary>
    public static List<Segment> Gaps(IReadOnlyList<Segment> normalized, long total)
    {
        var gaps = new List<Segment>();
        var cursor = 0L;
        foreach (var segment in normalized)
        {
            if (segment.In > cursor) gaps.Add(new Segment(cursor, segment.In));
            cursor = Math.Max(cursor, segment.Out);
        }

        if (cursor < total) gaps.Add(new Segment(cursor, total));
        return gaps;
    }

    public static long KeptFrames(IEnumerable<Segment> segments) => segments.Sum(s => s.Length);

    /// <summary>Where <paramref name="frame"/> lands in the exported video: the kept frames before it.</summary>
    public static long EditedFrame(IEnumerable<Segment> normalized, long frame) =>
        normalized.Sum(s => Math.Clamp(frame - s.In, 0, s.Length));

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        var merged = Normalize([new(50, 80), new(0, 10), new(70, 90), new(10, 20), new(30, 30)], 100);
        Expect(merged.Count == 2, "normalize joins touching and overlapping segments");
        Expect(merged[0] == new Segment(0, 20) && merged[1] == new Segment(50, 90), "normalize sorts and merges");
        var clamped = Normalize([new(-5, 10), new(95, 140)], 100);
        Expect(clamped[0] == new Segment(0, 10) && clamped[1] == new Segment(95, 100), "normalize clamps to the clip");
        var gaps = Gaps(merged, 100);
        Expect(gaps.Count == 2 && gaps[0] == new Segment(20, 50) && gaps[1] == new Segment(90, 100), "gaps between and after segments");
        Expect(Gaps([new(0, 100)], 100).Count == 0, "no gaps when everything is kept");
        Expect(KeptFrames(merged) == 60, "kept frames");
        Expect(EditedFrame(merged, 15) == 15 && EditedFrame(merged, 30) == 20 && EditedFrame(merged, 60) == 30 && EditedFrame(merged, 99) == 60, "edited frame skips gaps");
        return errors;
    }
}
