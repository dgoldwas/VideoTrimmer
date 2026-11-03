using System.Globalization;

namespace VideoTrimmer;

/// <summary>
/// Frame addresses for the trim range. The out frame is exclusive.
/// Timecode uses the rounded frame rate so the timecode and frame boxes round-trip.
/// Export timestamps still use the real frame rate.
/// </summary>
static class TimeMath
{
    public static int RoundedFps(double fps)
    {
        var rounded = (int)Math.Round(fps, MidpointRounding.AwayFromZero);
        return Math.Max(1, rounded);
    }

    public static string Format(long frame, double fps)
    {
        var rate = RoundedFps(fps);
        var whole = frame / rate;
        var fractional = frame % rate;
        if (fractional < 0)
        {
            fractional += rate;
            whole--;
        }

        return $"{whole / 60:00}:{Math.Abs(whole % 60):00}:{fractional:00}";
    }

    public static bool TryParse(string text, double fps, out long frame)
    {
        frame = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return false;
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var fractional)) return false;
        var rate = RoundedFps(fps);
        if (minutes < 0 || seconds is < 0 or > 59 || fractional < 0 || fractional >= rate) return false;
        frame = ((minutes * 60L) + seconds) * rate + fractional;
        return true;
    }

    public static (long In, long Out) WithIn(long inFrame, long outFrame, long total)
    {
        if (total < 1) total = 1;
        inFrame = Math.Clamp(inFrame, 0, total - 1);
        outFrame = Math.Clamp(outFrame, inFrame + 1, total);
        return (inFrame, outFrame);
    }

    public static (long In, long Out) WithOut(long inFrame, long outFrame, long total)
    {
        if (total < 1) total = 1;
        outFrame = Math.Clamp(outFrame, 1, total);
        inFrame = Math.Clamp(inFrame, 0, outFrame - 1);
        return (inFrame, outFrame);
    }

    public static long StickIn(long requested, long outFrame, long total)
    {
        var last = Math.Max(0, Math.Min(total, outFrame) - 1);
        return Math.Clamp(requested, 0, last);
    }

    public static long StickOut(long requested, long inFrame, long total)
    {
        var first = Math.Min(Math.Max(1, total), Math.Max(0, inFrame) + 1);
        return Math.Clamp(requested, first, Math.Max(1, total));
    }

    public static (long In, long Out) Slide(long anchorIn, long anchorOut, long delta, long total)
    {
        var length = Math.Max(1, anchorOut - anchorIn);
        if (length > total) length = Math.Max(1, total);
        var start = Math.Clamp(anchorIn + delta, 0, Math.Max(0, total - length));
        return (start, start + length);
    }

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        Expect(RoundedFps(29.97) == 30, "round fps");
        Expect(Format(0, 30) == "00:00:00", "format zero");
        Expect(Format(32, 30) == "00:01:02", "format 32");
        Expect(Format(30, 29.97) == "00:01:00", "format fractional second");
        Expect(TryParse("00:01:02", 30, out var parsed) && parsed == 32, "parse 32");
        Expect(TryParse("90:00:00", 30, out var longClip) && longClip == 162000, "parse 90 min");
        Expect(!TryParse("1:02", 30, out _), "reject short");
        Expect(!TryParse("00:00:30", 30, out _), "reject frame past the rate");

        var pushed = WithIn(50, 40, 100);
        Expect(pushed.In == 50 && pushed.Out == 51, "push out");
        var honored = WithOut(20, 10, 100);
        Expect(honored.In == 9 && honored.Out == 10, "pull in");
        var whole = WithIn(0, 30, 30);
        Expect(whole.In == 0 && whole.Out == 30, "whole clip");
        Expect(StickIn(80, 40, 100) == 39, "stick in");
        Expect(StickIn(-4, 10, 30) == 0, "stick in at start");
        Expect(StickOut(5, 20, 100) == 21, "stick out");
        Expect(StickOut(500, 20, 100) == 100, "stick out at end");

        var slid = Slide(10, 25, 1000, 100);
        Expect(slid.In == 85 && slid.Out == 100, "slide to end");
        var slidBack = Slide(10, 25, -100, 100);
        Expect(slidBack.In == 0 && slidBack.Out == 15, "slide to start");
        return errors;
    }
}
