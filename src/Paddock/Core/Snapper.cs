using Paddock.Interop;

namespace Paddock.Core;

/// <summary>Magnetisches Einrasten an Bildschirmrändern und benachbarten Bereichen.</summary>
public static class Snapper
{
    public static RECT SnapMove(RECT r, IReadOnlyList<RECT> others, IReadOnlyList<RECT> work, int threshold, int gap)
    {
        var (xs, ys) = Candidates(others, work, gap);
        int dx = Best(r.Left, xs.lefts, threshold), dx2 = Best(r.Right, xs.rights, threshold);
        int dy = Best(r.Top, ys.tops, threshold), dy2 = Best(r.Bottom, ys.bottoms, threshold);
        int mx = Pick(dx, dx2), my = Pick(dy, dy2);
        return new RECT(r.Left + mx, r.Top + my, r.Right + mx, r.Bottom + my);
    }

    /// <summary>Beim Größe-Ändern nur die gezogenen Kanten einrasten (WMSZ_*-Kante).</summary>
    public static RECT SnapSize(RECT r, int edge, IReadOnlyList<RECT> others, IReadOnlyList<RECT> work, int threshold, int gap)
    {
        var (xs, ys) = Candidates(others, work, gap);
        bool left = edge is 1 or 4 or 7, right = edge is 2 or 5 or 8, top = edge is 3 or 4 or 5, bottom = edge is 6 or 7 or 8;
        if (left) r.Left += Best(r.Left, xs.lefts, threshold);
        if (right) r.Right += Best(r.Right, xs.rights, threshold);
        if (top) r.Top += Best(r.Top, ys.tops, threshold);
        if (bottom) r.Bottom += Best(r.Bottom, ys.bottoms, threshold);
        return r;
    }

    static ((List<int> lefts, List<int> rights), (List<int> tops, List<int> bottoms)) Candidates(IReadOnlyList<RECT> others, IReadOnlyList<RECT> work, int gap)
    {
        List<int> lefts = [], rights = [], tops = [], bottoms = [];
        foreach (var w in work)
        {
            lefts.Add(w.Left + gap); rights.Add(w.Right - gap);
            tops.Add(w.Top + gap); bottoms.Add(w.Bottom - gap);
        }
        foreach (var o in others)
        {
            lefts.Add(o.Right + gap); lefts.Add(o.Left);
            rights.Add(o.Left - gap); rights.Add(o.Right);
            tops.Add(o.Bottom + gap); tops.Add(o.Top);
            bottoms.Add(o.Top - gap); bottoms.Add(o.Bottom);
        }
        return ((lefts, rights), (tops, bottoms));
    }

    static int Best(int value, List<int> candidates, int threshold)
    {
        int best = 0, bestDist = threshold + 1;
        foreach (var c in candidates)
        {
            int d = Math.Abs(c - value);
            if (d < bestDist) { bestDist = d; best = c - value; }
        }
        return bestDist <= threshold ? best : 0;
    }

    static int Pick(int a, int b) => a == 0 ? b : b == 0 ? a : Math.Abs(a) <= Math.Abs(b) ? a : b;

    /// <summary>Holt ein Rechteck vollständig auf einen sichtbaren Arbeitsbereich zurück.</summary>
    public static RECT EnsureVisible(RECT r, IReadOnlyList<RECT> work)
    {
        if (work.Count == 0) return r;
        if (work.Any(w => w.IntersectsWith(r) && r.Top >= w.Top - 4 && r.Top < w.Bottom - 20)) return r;
        var cx = r.Left + r.Width / 2; var cy = r.Top + r.Height / 2;
        var target = work.OrderBy(w => Math.Abs(w.Left + w.Width / 2 - cx) + Math.Abs(w.Top + w.Height / 2 - cy)).First();
        int width = Math.Min(r.Width, target.Width - 16), height = Math.Min(r.Height, target.Height - 16);
        int x = Math.Clamp(r.Left, target.Left + 8, target.Right - width - 8);
        int y = Math.Clamp(r.Top, target.Top + 8, target.Bottom - height - 8);
        return new RECT(x, y, x + width, y + height);
    }
}
