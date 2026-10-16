namespace LabVIEWMcp.Layout;

/// <summary>A rectangle in the block diagram pane's coordinates, as LabVIEW's Position and Bounds give it.</summary>
internal readonly record struct Box(int L, int T, int W, int H)
{
    public int R => L + W;
    public int B => T + H;
    public bool Contains(int x, int y, int slack = 0) =>
        x >= L - slack && x <= R + slack && y >= T - slack && y <= B + slack;
    public bool Overlaps(Box o, int gap = 0) =>
        L < o.R + gap && o.L < R + gap && T < o.B + gap && o.T < B + gap;
    public Box Offset(int dx, int dy) => this with { L = L + dx, T = T + dy };
    public static Box Union(IEnumerable<Box> boxes)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var x in boxes)
        {
            l = Math.Min(l, x.L); t = Math.Min(t, x.T); r = Math.Max(r, x.R); b = Math.Max(b, x.B);
        }
        return l == int.MaxValue ? default : new Box(l, t, r - l, b - t);
    }
}

/// <summary>One terminal of a node: its connector-pane label (empty for an unnamed slot), its
/// direction, and where it is.</summary>
internal sealed record Term(string Name, bool Source, Box Box);

/// <summary>One object of one diagram, addressed by the diagram's id and its index in that
/// diagram's All Objects[] - the address lvbd_layout_apply takes.</summary>
internal sealed class DObject
{
    public required string DiagramId { get; init; }
    public required int Index { get; init; }
    public required string Class { get; init; }
    public Box Box { get; set; }
    public int FrameCount { get; init; }
    public List<Term> Terms { get; init; } = [];
    public List<Box> Tunnels { get; init; } = [];
    /// <summary>A loop's shift registers as Shift Registers[] answers them - the RIGHT register
    /// of each pair, measured on the right border; the left one follows it.</summary>
    public List<Box> ShiftRegs { get; init; } = [];
    /// <summary>Total Bounds Rect: the object together with its label. Equal to <see cref="Box"/>
    /// when the dump did not carry it.</summary>
    public Box? Total { get; init; }
    /// <summary>A Case Structure's selector terminal on its left border - the "?" - which
    /// Tunnels[] does not list. An error cluster wired to it enters the frames THROUGH it, so on a
    /// DQMH Main it is the left end of the error rail.</summary>
    public Term? Selector => Class == "CaseStructure"
        ? Terms.FirstOrDefault(t => t.Box.L <= Box.L + 4 && !Tunnels.Any(b => b.L == t.Box.L && b.T == t.Box.T))
        : null;
    /// <summary>An Event Structure's dynamic event terminals, the pair on its left and right
    /// borders a registration refnum is wired to - not in Tunnels[] either. The timeout terminal in
    /// the top-left corner is not one of them.</summary>
    public List<Term> DynamicTerms => Class == "EventStructure"
        ? Terms.Where(t => !Tunnels.Any(b => b.L == t.Box.L && b.T == t.Box.T)
                           && !(t.Box.L == Box.L && t.Box.T == Box.T)
                           && (t.Box.L <= Box.L + 4 || t.Box.R >= Box.R - 4)).ToList()
        : [];
    /// <summary>How far the object's label reaches beyond its own bounds: left, top, right, bottom.</summary>
    public (int L, int T, int R, int B) Overhang => Total is { } t
        ? (Math.Max(0, Box.L - t.L), Math.Max(0, Box.T - t.T), Math.Max(0, t.R - Box.R), Math.Max(0, t.B - Box.B))
        : (0, 0, 0, 0);
    /// <summary>The object's own terminal name and direction when it IS a terminal (a control
    /// terminal, a loop's i or stop terminal); empty name and false otherwise.</summary>
    public string OwnName { get; init; } = "";
    public bool OwnSource { get; init; }
    /// <summary>A structure's Frame Size, the writable twin of Bounds; null for anything else.</summary>
    public (int W, int H)? FrameSize { get; init; }
    public List<Diagram> Frames { get; } = [];

    public bool IsStructure => FrameCount > 0;
    public bool IsText => Class == "Text";
    public bool IsDecoration => Class == "Decoration";
    /// <summary>A loop's i, N or stop terminal: class Terminal, placed by LabVIEW on the loop.</summary>
    public bool IsLoopTerminal => Class == "Terminal";
    public bool IsControlTerminal => Class == "ControlTerminal";
    public bool IsConstant => Class.EndsWith("Constant", StringComparison.Ordinal);
    public string Key => $"{DiagramId}#{Index}";
    public override string ToString() => $"{Key} {Class} {Box}";
}

/// <summary>A wire: the points where its ends meet terminals. Which object each end belongs to is
/// resolved by <see cref="Diagram.Resolve"/>.</summary>
internal sealed record Wire(int Index, List<(int X, int Y)> Ends);

internal sealed class Diagram
{
    public required string Id { get; init; }
    public Box Box { get; init; }
    public List<DObject> Objects { get; } = [];
    public List<Wire> Wires { get; } = [];
    /// <summary>The structure this diagram is a frame of; null for the top level.</summary>
    public DObject? Owner { get; set; }
    public int FrameIndex { get; set; }
    public int Depth => Id.Count(c => c == '.') / 2;
    /// <summary>The apply helper's path: "object frame object frame ...", "T" for the top level.</summary>
    public string ApplyPath
    {
        get
        {
            var parts = Id.Split('.').Skip(1).ToArray();
            return parts.Length == 0 ? "T" : string.Join(' ', parts);
        }
    }
}

/// <summary>
/// The geometry of a whole block diagram, parsed from lvbd_layout_dump's lines:
/// <c>D|id|left|top|width|height</c>,
/// <c>O|id|index|class|left|top|width|height|frames|terminals|tunnels|own|frame|shiftregs|total</c> and
/// <c>W|id|index|ends</c>. Diagram ids are "0" for the top level and parent.object.frame below it.
/// </summary>
internal sealed class DiagramModel
{
    public Dictionary<string, Diagram> Diagrams { get; } = new(StringComparer.Ordinal);
    public Diagram Top => Diagrams["0"];
    public IEnumerable<DObject> AllObjects => Diagrams.Values.SelectMany(d => d.Objects);

    public static DiagramModel Parse(IEnumerable<string> lines)
    {
        var model = new DiagramModel();
        var objects = new List<DObject>();
        var wires = new List<(string Id, Wire W)>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r', '\n');
            if (line.Length < 2) continue;
            var f = line.Split('|');
            switch (f[0])
            {
                case "D" when f.Length >= 6:
                    model.Diagrams[f[1]] = new Diagram { Id = f[1], Box = new Box(I(f[2]), I(f[3]), I(f[4]), I(f[5])) };
                    break;
                case "O" when f.Length >= 12:
                    objects.Add(new DObject
                    {
                        DiagramId = f[1],
                        Index = I(f[2]),
                        Class = f[3],
                        Box = new Box(I(f[4]), I(f[5]), I(f[6]), I(f[7])),
                        FrameCount = I(f[8]),
                        Terms = Items(f[9]).Select(ParseTerm).Where(t => t is not null).Select(t => t!).ToList(),
                        Tunnels = Items(f[10]).Select(ParseBox).Where(b => b is not null).Select(b => b!.Value).ToList(),
                        OwnName = Field(f[11], 0),
                        OwnSource = Field(f[11], 1) == "1",
                        FrameSize = f.Length > 12 && I(Field(f[12], 0)) > 0
                            ? (I(Field(f[12], 0)), I(Field(f[12], 1))) : null,
                        ShiftRegs = f.Length > 13
                            ? Items(f[13]).Select(ParseBox).Where(b => b is not null).Select(b => b!.Value).ToList() : [],
                        Total = f.Length > 14 ? ParseRect(f[14]) : null,
                    });
                    break;
                case "W" when f.Length >= 4:
                    wires.Add((f[1], new Wire(I(f[2]), Items(f[3]).Select(ParsePoint)
                        .Where(p => p is not null).Select(p => p!.Value).ToList())));
                    break;
            }
        }
        if (!model.Diagrams.ContainsKey("0"))
            throw new FormatException("The dump has no top-level diagram (no D|0 line).");
        foreach (var o in objects)
        {
            // an object whose label shows answers Bounds the size of object AND label at the
            // object's own Position (measured 2026-10-16: 13 objects, every one the same size as
            // its Total Bounds Rect, shifted). The object itself ends where the total ends.
            if (o.Total is { } t && (t.W, t.H) == (o.Box.W, o.Box.H) && (t.L, t.T) != (o.Box.L, o.Box.T)
                && t.R > o.Box.L && t.B > o.Box.T)
                o.Box = o.Box with { W = t.R - o.Box.L, H = t.B - o.Box.T };
            if (model.Diagrams.TryGetValue(o.DiagramId, out var d)) d.Objects.Add(o);
        }
        foreach (var d in model.Diagrams.Values) d.Objects.Sort((a, b) => a.Index.CompareTo(b.Index));
        foreach (var (id, w) in wires)
            if (model.Diagrams.TryGetValue(id, out var d)) d.Wires.Add(w);
        // frames: "parent.object.frame"
        foreach (var d in model.Diagrams.Values.Where(d => d.Id != "0"))
        {
            var parts = d.Id.Split('.');
            var parentId = string.Join('.', parts[..^2]);
            if (!model.Diagrams.TryGetValue(parentId, out var parent)) continue;
            var owner = parent.Objects.FirstOrDefault(o => o.Index == I(parts[^2]));
            if (owner is null) continue;
            d.Owner = owner;
            d.FrameIndex = I(parts[^1]);
            owner.Frames.Add(d);
        }
        foreach (var o in objects) o.Frames.Sort((a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
        return model;
    }

    private static int I(string s) => int.TryParse(s, out var v) ? v : 0;
    private static IEnumerable<string> Items(string s) => s.Split('~', StringSplitOptions.RemoveEmptyEntries);
    private static string Field(string s, int i)
    {
        var p = s.Split('^');
        return i < p.Length ? p[i] : "";
    }

    private static Term? ParseTerm(string s)
    {
        var p = s.Split('^');
        return p.Length < 6 ? null : new Term(p[0], p[1] == "1", new Box(I(p[2]), I(p[3]), I(p[4]), I(p[5])));
    }

    private static Box? ParseBox(string s)
    {
        var p = s.Split('^');
        return p.Length < 4 ? null : new Box(I(p[0]), I(p[1]), I(p[2]), I(p[3]));
    }

    /// <summary>left^top^right^bottom, as Total Bounds Rect gives it.</summary>
    private static Box? ParseRect(string s)
    {
        var p = s.Split('^');
        if (p.Length < 4) return null;
        var (l, t, r, b) = (I(p[0]), I(p[1]), I(p[2]), I(p[3]));
        return r > l && b > t ? new Box(l, t, r - l, b - t) : null;
    }

    private static (int X, int Y)? ParsePoint(string s)
    {
        var p = s.Split('^');
        return p.Length < 2 ? null : (I(p[0]), I(p[1]));
    }
}
