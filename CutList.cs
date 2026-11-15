using System.Text.Json;

namespace VideoTrimmer;

/// <summary>A kept range of frames. Out is exclusive, like the trim range.</summary>
readonly record struct Segment(long In, long Out)
{
    public long Length => Out - In;
    public bool Contains(long frame) => frame >= In && frame < Out;
    public bool Overlaps(Segment other) => In < other.Out && other.In < Out;
}

/// <summary>Every segment in time order and which one is being edited: what undo and redo step between.</summary>
readonly record struct SegmentState(IReadOnlyList<Segment> Segments, int Active)
{
    /// <summary>Only the segments count as a change; picking another one to edit does not.</summary>
    public bool SameSegments(SegmentState other) => Segments.SequenceEqual(other.Segments);
}

/// <summary>Undo and redo for the segments, and whether they changed since the project was opened or saved.</summary>
sealed class EditHistory
{
    const int Limit = 200;
    readonly List<SegmentState> undo = [];
    readonly List<SegmentState> redo = [];
    SegmentState? saved;

    public SegmentState? Current { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool IsDirty => Current is { } current && (saved is not { } kept || !current.SameSegments(kept));

    /// <summary>Starts over at <paramref name="state"/>, as just opened: nothing to undo and nothing unsaved.</summary>
    public void Reset(SegmentState state)
    {
        undo.Clear();
        redo.Clear();
        Current = saved = state;
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        Current = saved = null;
    }

    public void MarkSaved() => saved = Current;

    /// <summary>Counts the segments as unsaved until the next save, whatever they are.</summary>
    public void MarkUnsaved() => saved = null;

    /// <summary>Notes the segments as they are now. Returns true when they changed, which makes an undo step.</summary>
    public bool Record(SegmentState state)
    {
        if (Current is not { } current || current.SameSegments(state))
        {
            Current = state;
            return false;
        }

        undo.Add(current);
        if (undo.Count > Limit) undo.RemoveAt(0);
        redo.Clear();
        Current = state;
        return true;
    }

    public SegmentState? Undo() => Step(undo, redo);
    public SegmentState? Redo() => Step(redo, undo);

    SegmentState? Step(List<SegmentState> from, List<SegmentState> to)
    {
        if (from.Count == 0 || Current is not { } current) return null;
        var state = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(current);
        Current = state;
        return state;
    }
}

/// <summary>A segment list read from a saved <c>.json</c> file: the video it was made for, that video's frame rate, and the segments.</summary>
sealed record SavedCuts(string? Video, double Fps, IReadOnlyList<Segment> Segments);

/// <summary>Helpers for the list of segments a multi-cut export keeps.</summary>
static class CutList
{
    static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    /// <summary>The segment list as JSON: the source path, its frame rate, and each segment's in and (exclusive) out frame.</summary>
    public static string Save(string? video, double fps, IEnumerable<Segment> segments) =>
        JsonSerializer.Serialize(new { Video = video, Fps = fps, Segments = segments.Select(s => new { s.In, s.Out }) }, SaveOptions);

    /// <summary>Reads what <see cref="Save"/> wrote. Throws <see cref="JsonException"/> or <see cref="InvalidDataException"/> for anything else.</summary>
    public static SavedCuts Load(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Segments", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("This is not a VideoTrimmer segment list.");
        var segments = new List<Segment>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("In", out var start) || !item.TryGetProperty("Out", out var end)
                || !start.TryGetInt64(out var first) || !end.TryGetInt64(out var last))
                throw new InvalidDataException("A segment in this list has no In and Out frame.");
            segments.Add(new Segment(first, last));
        }

        var video = root.TryGetProperty("Video", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
        var fps = root.TryGetProperty("Fps", out var rate) && rate.TryGetDouble(out var value) ? value : 0;
        return new SavedCuts(video, fps, segments);
    }

    /// <summary>Moves saved segments onto a video with another frame rate, keeping their times.</summary>
    public static IEnumerable<Segment> Rescale(IEnumerable<Segment> segments, double savedFps, double fps) =>
        savedFps <= 0 || fps <= 0 || Math.Abs(savedFps - fps) < 0.0005
            ? segments
            : segments.Select(s => new Segment((long)Math.Round(s.In * fps / savedFps), (long)Math.Round(s.Out * fps / savedFps)));

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

        var saved = Load(Save(@"C:\clips\in.mp4", 24, [new(10, 20), new(50, 90)]));
        Expect(saved.Video == @"C:\clips\in.mp4" && saved.Fps == 24 && saved.Segments.SequenceEqual([new Segment(10, 20), new Segment(50, 90)]), "a saved segment list comes back");
        Expect(Load("""{"Segments":[]}""") is { Video: null, Fps: 0, Segments.Count: 0 }, "a list without a video or frame rate still loads");
        var foreign = false;
        try { Load("""{"Video":"a.mp4","Timecodes":["00:00:01"]}"""); } catch (InvalidDataException) { foreign = true; }
        Expect(foreign, "a file without segments is refused");
        var history = new EditHistory();
        SegmentState whole = new([new(0, 100)], 0), split = new([new(0, 50), new(50, 100)], 1), trimmed = new([new(0, 50)], 0);
        history.Reset(whole);
        Expect(!history.IsDirty && !history.CanUndo, "a fresh history has nothing to undo or save");
        Expect(!history.Record(whole with { Active = 0 }), "the same segments make no undo step");
        Expect(history.Record(split) && history.Record(trimmed) && history.IsDirty, "changes make undo steps and need saving");
        Expect(history.Undo() == split && history.Undo() == whole && history.Undo() is null && !history.IsDirty, "undo steps back to the opened state");
        Expect(history.Redo() == split && history.CanRedo, "redo steps forward again");
        history.MarkSaved();
        Expect(!history.IsDirty && history.Record(trimmed) && !history.CanRedo, "saving clears the change mark; a new change clears redo");
        Expect(Rescale([new(24, 48)], 24, 30).SequenceEqual([new Segment(30, 60)]) && Rescale([new(24, 48)], 0, 30).SequenceEqual([new Segment(24, 48)]), "segments keep their times on another frame rate");
        return errors;
    }
}
