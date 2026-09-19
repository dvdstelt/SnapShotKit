using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SnapShotKit.Daemon;

/// <summary>What became of a frame handed to the stitcher.</summary>
public enum Stitched
{
    /// <summary>The first frame, taken whole.</summary>
    Started,

    /// <summary>The same as the last one kept. Nothing scrolled.</summary>
    Unmoved,

    /// <summary>It had scrolled on, and what it showed for the first time was added.</summary>
    Grown,

    /// <summary>It had scrolled back the way it came, over what is already held. Nothing to add.</summary>
    Back,

    /// <summary>
    /// Where it belongs could not be worked out: it shares too little with the last one kept, or
    /// what it shares is too plain to say how far it moved. Left out, and the next is tried against
    /// the same one.
    /// </summary>
    Lost,

    /// <summary>
    /// Lost too many times running, so it was put underneath what is held as it stands, and
    /// following carries on from it. There is a seam in the picture where it went on.
    /// </summary>
    Rejoined,

    /// <summary>As tall as it is allowed to get.</summary>
    Full
}

/// <summary>
/// Joins the frames of something being scrolled into one tall picture.
///
/// Nothing on Wayland lets a capture tool scroll somebody else's window, so the user scrolls and
/// this watches. Each frame is the same region of the screen a moment later, and the whole of the
/// work is finding how far the content moved between one and the next: once that is known, the
/// rows that came into view are the rows to add.
///
/// How far is found in two steps. Every row is boiled down to a few numbers, the brightness of
/// sixteen stretches across it, and the offsets at which one frame's rows line up with the other's
/// are found on those, which is cheap enough to try every offset there is. The best few are then
/// checked against the actual pixels, because two different rows of text can average out alike,
/// and a join made on a false match puts a tear through the picture that nobody can take out
/// afterwards.
///
/// Three things on a page do not scroll with it, and each would defeat a plain comparison. A header
/// or a footer that stays put is found as the rows at the top and bottom that did not change while
/// the middle did, and the match is made on the band between them; the footer is kept off the
/// picture until the end, where it goes once, at the bottom. The scrollbar moves the wrong way at
/// the wrong speed, so the right-hand edge is left out of every comparison. And the pointer sits
/// wherever the user left it, which is what the tolerance in the pixel check is for.
///
/// No knowledge of where frames come from, and no clock. It is handed frames and says what it made
/// of each, which is what lets it be tried on made-up pages.
/// </summary>
public sealed class ScrollStitcher
{
    /// <summary>How many stretches a row is averaged over. Enough that two rows of text differ, few enough to try every offset.</summary>
    const int Segments = 16;

    /// <summary>The share of the width, from the right, left out of every comparison: where a scrollbar is.</summary>
    const double IgnoredRight = 0.04;

    /// <summary>How far apart two rows' averages may be, per stretch in brightness levels, and still be the same row.</summary>
    const double RowTolerance = 1.5;

    /// <summary>How far a colour channel may be out and the pixel still count as the same, in the check against real pixels.</summary>
    const int PixelTolerance = 10;

    /// <summary>The share of checked pixels that have to agree. Short of all of them, for the pointer and for a caret that blinks.</summary>
    const double PixelAgreement = 0.97;

    /// <summary>The tallest picture this will make. A width of 1920 at this height is 150 MB held in memory, which is enough.</summary>
    public const int MaximumRows = 20000;

    readonly int width;
    readonly int height;
    readonly int compared;

    /// <summary>The picture so far, BGRx rows one after another, without any footer.</summary>
    byte[] content;

    /// <summary>How many rows of <see cref="content"/> are in use.</summary>
    int rows;

    /// <summary>The last frame kept, its rows' averages, and how far down the picture its top row is.</summary>
    byte[]? last;

    int[]? lastSignature;
    int lastTop;

    /// <summary>How far down the picture the frame that reached furthest had its top row.</summary>
    int furthest;

    /// <summary>How many rows at the head of a frame belong to a header that stays put, as last seen.</summary>
    int headerRows;

    /// <summary>The latest frame that could not be placed, kept in case the capture ends on it.</summary>
    byte[]? unplaced;

    int[]? unplacedSignature;

    /// <summary>How many frames running could not be placed.</summary>
    int lost;

    /// <summary>How many of those in a row it takes before one is put underneath regardless. About a second and a half of frames.</summary>
    const int Patience = 12;

    /// <summary>
    /// Whether a scroll has been followed since the last seam, which is what earns the next one.
    /// True to begin with: a first flick of the wheel that carries the page further than a screen
    /// deserves one, or the capture would never get started at all.
    /// </summary>
    bool followedSinceSeam = true;

    /// <summary>How many times the picture was carried on without knowing how far it had moved.</summary>
    public int Seams { get; private set; }

    /// <summary>How many rows at the foot of the last frame kept belong to a footer that stays put, and so are not in the picture yet.</summary>
    int footer;

    public ScrollStitcher(int width, int height)
    {
        this.width = width;
        this.height = height;

        compared = Math.Max((int)(width * (1 - IgnoredRight)), Math.Min(width, Segments));
        content = new byte[(long)width * 4 * height * 2];
    }

    /// <summary>How tall the picture would be if it were finished now.</summary>
    public int Rows => rows + footer;

    /// <summary>Whether anything has been added since the first frame.</summary>
    public bool HasGrown { get; private set; }

    /// <summary>Takes a frame: <see cref="height"/> rows of BGRx, <see cref="width"/> wide, with no padding between rows.</summary>
    public Stitched Add(byte[] frame)
    {
        var signature = Signature(frame);

        if (last is null || lastSignature is null)
        {
            Append(frame, 0, height);
            Keep(frame, signature, top: 0);
            return Stitched.Started;
        }

        // The rows at the top and bottom that are where they were: a header and a footer that do
        // not scroll, or simply nothing having moved at all.
        var header = 0;

        while (header < height && Same(lastSignature, header, signature, header))
        {
            header++;
        }

        if (header == height)
        {
            return Stitched.Unmoved;
        }

        var still = 0;

        while (still < height - header && Same(lastSignature, height - 1 - still, signature, height - 1 - still))
        {
            still++;
        }

        var found = Offset(frame, signature, header, still);

        if (found is null && header + still > 0)
        {
            // A page that is mostly white has a great many rows that "did not change", and taking
            // them for a header and footer leaves too narrow a band to match on. Tried again on
            // the whole frame, with nothing held to be standing still.
            found = Offset(frame, signature, 0, 0);
            still = 0;
        }

        if (found is not { } moved)
        {
            // Half the frame or more is exactly where it was. That is not a page that has scrolled
            // out of reach, it is a page standing still with something going on in it: a caret, a
            // spinner, a line of text being typed. Nothing to add and nothing lost.
            if (header + still >= height / 2)
            {
                return Stitched.Unmoved;
            }

            // Carried on regardless once, and not again until something has been followed
            // properly since. A region with a film playing in it never lines up with anything, and
            // without this it would be pasted underneath itself every second and a half for as
            // long as it was left running.
            unplaced = frame;
            unplacedSignature = signature;

            if (++lost < Patience || !followedSinceSeam)
            {
                return Stitched.Lost;
            }

            return Rejoin(frame, signature);
        }

        lost = 0;
        unplaced = null;
        headerRows = header;
        followedSinceSeam |= moved > 0;

        if (moved == 0)
        {
            return Stitched.Unmoved;
        }

        var top = lastTop + moved;

        if (moved < 0)
        {
            // Scrolled back up over what is already held. Kept as the one to measure from, so that
            // coming back down again is followed, but it has nothing new in it.
            Keep(frame, signature, top);
            return Stitched.Back;
        }

        // What was taken from the last frame's foot as picture turns out to be a footer. Taken
        // back off, to go on once at the very end.
        //
        // Only when the last frame is the one that reached furthest. After scrolling back up, the
        // last frame kept sits in the middle of what is held, and cutting the picture off at its
        // foot would throw away everything gathered below it.
        var body = height - still;

        if (lastTop == furthest)
        {
            rows = Math.Min(rows, lastTop + body);
        }

        var first = Math.Max(rows - top, 0);

        if (first < body)
        {
            var room = MaximumRows - rows;

            Append(frame, first, Math.Min(body - first, room));
            HasGrown = true;
            furthest = top;

            if (body - first >= room)
            {
                footer = 0;
                Keep(frame, signature, top);
                return Stitched.Full;
            }
        }

        footer = still;
        Keep(frame, signature, top);
        return Stitched.Grown;
    }

    /// <summary>The picture: everything gathered, and whatever stayed put at the foot of the last frame underneath it.</summary>
    public Image<Rgb24> Finish()
    {
        // Whatever was on screen at the end goes in, placed or not. The end of a page is where a
        // capture stops, and it is also where a last hard flick of the wheel is most likely to
        // have carried the page further than could be followed, so a frame left unplaced when the
        // capture ends is very probably the bottom of the page. Put underneath with a seam, it
        // may repeat a little of what is above it; left out, the bottom is simply missing.
        if (unplaced is not null && unplacedSignature is not null && rows < MaximumRows)
        {
            Rejoin(unplaced, unplacedSignature);
        }

        if (last is not null && footer > 0)
        {
            Append(last, height - footer, footer);
            footer = 0;
        }

        var image = new Image<Rgb24>(width, Math.Max(rows, 1));
        var source = content;
        var rowBytes = width * 4;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height && y < rows; y++)
            {
                var row = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Bgra32>(source.AsSpan(y * rowBytes, rowBytes));
                PixelOperations<Bgra32>.Instance.ToRgb24(Configuration.Default, row, accessor.GetRowSpan(y));
            }
        });

        return image;
    }

    /// <summary>
    /// Gives up on finding where a frame belongs, and puts it underneath.
    ///
    /// Scrolling through a screenful of plain background leaves nothing to line up by, and neither
    /// does one flick of the wheel that carries the page further than a screen. Waiting for a
    /// match that cannot come would lose everything scrolled from then on, because the frame being
    /// measured from falls further behind with each one. A seam is the smaller loss: what is below
    /// it is whole, and something may be doubled or missing across it, where otherwise the rest of
    /// the page would be missing altogether.
    /// </summary>
    Stitched Rejoin(byte[] frame, int[] signature)
    {
        lost = 0;
        unplaced = null;
        followedSinceSeam = false;
        Seams++;

        // The footer last seen is held to be a footer still, and the header a header, so neither
        // is repeated across the seam.
        rows = Math.Min(rows, furthest + height - footer);

        var body = height - footer - headerRows;
        var room = MaximumRows - rows;

        Keep(frame, signature, top: rows - headerRows);
        furthest = lastTop;

        Append(frame, headerRows, Math.Min(body, room));
        HasGrown = true;

        return body >= room ? Stitched.Full : Stitched.Rejoined;
    }

    void Keep(byte[] frame, int[] signature, int top)
    {
        last = frame;
        lastSignature = signature;
        lastTop = top;
    }

    void Append(byte[] frame, int firstRow, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var rowBytes = width * 4;
        var needed = (long)(rows + count) * rowBytes;

        if (needed > content.Length)
        {
            Array.Resize(ref content, (int)Math.Min(Math.Max(needed, (long)content.Length * 2), (long)(MaximumRows + height) * rowBytes));
        }

        Buffer.BlockCopy(frame, firstRow * rowBytes, content, rows * rowBytes, count * rowBytes);
        rows += count;
    }

    // ---- Finding how far it moved ---------------------------------------------------------------

    /// <summary>
    /// How many rows the content moved up between the last frame kept and this one, negative for
    /// down, or null when no offset lines the two up.
    /// </summary>
    /// <param name="header">Rows at the top that stayed put, and are left out.</param>
    /// <param name="still">Rows at the bottom that stayed put, likewise.</param>
    int? Offset(byte[] frame, int[] signature, int header, int still)
    {
        var band = height - header - still;
        // The least two frames have to share to be lined up. Small, because a wheel flicked hard
        // leaves little in common between one look and the next, and whatever is asked for here
        // beyond what is needed is content that goes unplaced. What stops a small overlap being a
        // false one is the check against real pixels, not the size of it.
        var least = Math.Max(32, band / 10);

        if (band < least * 2)
        {
            return null;
        }

        // The cost of every offset in both directions: how far apart the rows that would overlap
        // are, on average. Overlapping rows of this frame [from, to) sit over the last frame's
        // rows [from + offset, to + offset).
        List<(int Offset, double Cost)> candidates = [];

        for (var offset = -(band - least); offset <= band - least; offset++)
        {
            var from = header + Math.Max(-offset, 0);
            var to = height - still - Math.Max(offset, 0);

            // Given up on as soon as it has cost more than a fit is allowed to in total. Nearly
            // every offset is wrong and says so within a few rows, and walking the rest of the
            // overlap to find out exactly how wrong is most of what this search would cost: on a
            // large region it is the difference between ten looks a second and three, and at three
            // an ordinary scroll moves further between looks than the frames overlap.
            long total = 0;
            var allowed = (long)(RowTolerance * Segments * (to - from));

            for (var row = from; row < to && total <= allowed; row++)
            {
                total += Distance(signature, row, lastSignature!, row + offset);
            }

            if (total <= allowed)
            {
                candidates.Add((offset, (double)total / ((to - from) * Segments)));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Nearest first. A page with a repeating pattern, a table or a list, lines up at every
        // multiple of the repeat, and frames come often enough that the true answer is the small
        // one. Taking a larger one would drop the rows between.
        var best = candidates.Min(candidate => candidate.Cost);

        foreach (var (offset, _) in candidates
                     .Where(candidate => candidate.Cost <= best + 0.35)
                     .OrderBy(candidate => Math.Abs(candidate.Offset))
                     .Take(6))
        {
            var from = header + Math.Max(-offset, 0);
            var to = height - still - Math.Max(offset, 0);

            if (Telling(signature, from, to) && Agrees(frame, from, to, offset))
            {
                return offset;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the rows that overlap have anything in them to line up by. A stretch of plain
    /// background matches itself at every offset, so a match found there says nothing about how far
    /// it moved, and believing it would drop or double whatever was scrolled past.
    /// </summary>
    static bool Telling(int[] signature, int from, int to)
    {
        var changes = 0;

        for (var row = from; row + 1 < to; row++)
        {
            if (Distance(signature, row, signature, row + 1) > Segments * 2)
            {
                changes++;
            }
        }

        return changes >= 6;
    }

    /// <summary>The check against the pixels themselves, on every other row and every third pixel of the overlap.</summary>
    bool Agrees(byte[] frame, int from, int to, int offset)
    {
        var rowBytes = width * 4;
        long agreed = 0, checkedPixels = 0;

        for (var row = from; row < to; row += 2)
        {
            var here = row * rowBytes;
            var there = (row + offset) * rowBytes;

            for (var x = 0; x < compared; x += 3)
            {
                var at = x * 4;

                if (Math.Abs(frame[here + at] - last![there + at]) <= PixelTolerance
                    && Math.Abs(frame[here + at + 1] - last[there + at + 1]) <= PixelTolerance
                    && Math.Abs(frame[here + at + 2] - last[there + at + 2]) <= PixelTolerance)
                {
                    agreed++;
                }

                checkedPixels++;
            }
        }

        return checkedPixels > 0 && (double)agreed / checkedPixels >= PixelAgreement;
    }

    /// <summary>Every row as the average brightness of each of its stretches, scrollbar left out.</summary>
    int[] Signature(byte[] frame)
    {
        var signature = new int[height * Segments];
        var rowBytes = width * 4;
        var span = Math.Max(compared / Segments, 1);

        for (var row = 0; row < height; row++)
        {
            for (var segment = 0; segment < Segments; segment++)
            {
                var start = row * rowBytes + segment * span * 4;
                var total = 0;

                for (var x = 0; x < span; x++)
                {
                    var at = start + x * 4;

                    // Blue, green twice, red: near enough to brightness, and no multiplication.
                    total += frame[at] + 2 * frame[at + 1] + frame[at + 2];
                }

                signature[row * Segments + segment] = total / (span * 4);
            }
        }

        return signature;
    }

    static int Distance(int[] a, int rowA, int[] b, int rowB)
    {
        var total = 0;

        for (var segment = 0; segment < Segments; segment++)
        {
            total += Math.Abs(a[rowA * Segments + segment] - b[rowB * Segments + segment]);
        }

        return total;
    }

    static bool Same(int[] a, int rowA, int[] b, int rowB) => Distance(a, rowA, b, rowB) <= Segments * RowTolerance;
}
