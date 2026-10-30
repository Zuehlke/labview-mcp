using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LabVIEWMcp.Layout;

/// <summary>
/// A comment whose ARROW points at an object: the comment's text and where the target sits
/// relative to the comment's top-left corner. The VI Server dump does not say where an arrow
/// points; the heap does - a `label` carries an `attachment` whose `attachedObject` is the
/// target. Measured 2026-10-30 on a DQMH 7.1.2 Main.vi: "Drop this VI wherever you need to fire
/// the Private.Start Acquisition event" pointed 130 px right and 200 px down at its loose call,
/// 80 px clear of it, so the 40 px nearness rule left it a note at the top right of the diagram
/// and its arrow ran across every loop (docs/diagram-layout.md).
/// </summary>
internal sealed record CommentArrow(string Text, int Dx, int Dy, int TargetW, int TargetH);

internal static class CommentArrows
{
    /// <summary>Every arrowed comment in a `*_BDHb.xml` heap from pylv_extract.</summary>
    public static List<CommentArrow> Read(string heapXml)
    {
        var root = XDocument.Load(heapXml).Root;
        if (root is null) return [];
        // only elements carrying a class: a reference like <attachment uid=".."/> carries the
        // same uid as the object it names
        var byUid = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in root.Descendants())
            if (e.Attribute("uid")?.Value is { } uid && e.Attribute("class") is not null)
                byUid[uid] = e;

        var arrows = new List<CommentArrow>();
        foreach (var label in root.Descendants().Where(e => (string?)e.Attribute("class") == "label"))
        {
            if (label.Element("attachment")?.Attribute("uid")?.Value is not { } aUid
                || !byUid.TryGetValue(aUid, out var attachment)
                || attachment.Element("attachedObject")?.Attribute("uid")?.Value is not { } tUid
                || !byUid.TryGetValue(tUid, out var target)
                || Bounds(label) is not { } lb || Bounds(target) is not { } tb
                || label.Element("textRec")?.Element("text")?.Value is not { } text)
                continue;
            arrows.Add(new CommentArrow(Normalise(Unquote(text)), tb.L - lb.L, tb.T - lb.T, tb.W, tb.H));
        }
        return arrows;
    }

    /// <summary>The target of an arrowed comment among the diagram's objects, matched by text and
    /// by the offset the arrow had - the heap and the dump agree up to a translation.</summary>
    public static DObject? TargetOf(DObject comment, IEnumerable<DObject> candidates, IReadOnlyList<CommentArrow> arrows,
                                    int slack = 4)
    {
        if (arrows.Count == 0 || comment.Text is null) return null;
        var text = Normalise(comment.Text);
        foreach (var a in arrows.Where(a => a.Text == text))
        {
            var hit = candidates.FirstOrDefault(n => !ReferenceEquals(n, comment)
                && Math.Abs(n.Box.L - comment.Box.L - a.Dx) <= slack
                && Math.Abs(n.Box.T - comment.Box.T - a.Dy) <= slack
                && Math.Abs(n.Box.W - a.TargetW) <= slack && Math.Abs(n.Box.H - a.TargetH) <= slack);
            if (hit is not null) return hit;
        }
        return null;
    }

    // heap bounds are (top, left, bottom, right)
    private static Box? Bounds(XElement e)
    {
        var m = Regex.Match(e.Element("bounds")?.Value ?? "", @"\((-?\d+),\s*(-?\d+),\s*(-?\d+),\s*(-?\d+)\)");
        if (!m.Success) return null;
        int t = int.Parse(m.Groups[1].Value), l = int.Parse(m.Groups[2].Value),
            b = int.Parse(m.Groups[3].Value), r = int.Parse(m.Groups[4].Value);
        return new Box(l, t, r - l, b - t);
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    internal static string Normalise(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
