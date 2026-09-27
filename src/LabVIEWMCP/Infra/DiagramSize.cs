using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// How big a generated block diagram is, measured after generation and estimated before it.
///
/// WHY. The user's standing rule is a diagram of about 1920 x 1080, and for weeks nothing measured
/// it, so nothing held it: rendered 2026-09-25, the ATM main VI of three consecutive agent builds
/// came out 3306, 3456 and 4152 px wide and its state machine 2116, 2963 and 2012 - every one over,
/// with every other check in the chain green. A rule without a measurement is advice.
///
/// TWO HALVES, because they answer different questions at different times:
///
///   - <see cref="Chain"/> reads the AIXML and finds the LONGEST DEPENDENCY CHAIN, in stages - a
///     Node or a Call is one stage, a structure is one plus the longest chain inside it. It costs no
///     LabVIEW, so it can run BEFORE a diagram is generated, and it names the elements along the
///     chain, which is what says WHICH stretch to fold into a subVI. Calibrated 2026-09-25 against
///     renders of fifteen generated VIs: on the big diagrams one stage is about 145-185 px (25
///     stages 4152 px, 24 stages 3456, 22 stages 3306, 16 stages 2963, 12 stages 2012 and 2116), so
///     1920 px is about 11-12 stages. The budget is 10, leaving room for long constants and labels,
///     which widen a diagram without adding a stage (a 3-stage message builder rendered 1090 px).
///   - <see cref="PngSize"/> reads the rendered top-level diagram, which is the verdict. LabVIEW's
///     auto-layout decides the geometry and AIXML carries none, so only a render knows.
///
/// WHAT THE CHAIN MEANS FOR THE FIX, and it corrects docs/cold-build-atm-cld.md §11, which read
/// "width only comes down by merging sequential subVIs": width follows the chain, so the fix is to
/// fold a SEQUENTIAL stretch of it into ONE new subVI - one call in place of k stages. Factoring
/// parallel work buys height and leaves width alone.
/// </summary>
internal static class DiagramSize
{
    internal const int MaxWidth = 1920;
    internal const int MaxHeight = 1080;
    internal const int MaxChainStages = 10;

    /// <summary>A PNG's pixel size off its IHDR chunk, or null when the file is not a PNG.</summary>
    internal static (int Width, int Height)? PngSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[24];
            if (stream.Read(head, 0, 24) < 24) return null;
            if (head[0] != 0x89 || head[1] != (byte)'P' || head[2] != (byte)'N' || head[3] != (byte)'G') return null;
            static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
            return (BigEndian(head, 16), BigEndian(head, 20));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The longest chain of a whole document, or null when it does not parse.</summary>
    internal sealed record ChainResult(double Stages, JsonArray Chain);

    internal static ChainResult? Chain(string xml)
    {
        XElement root;
        try { root = XElement.Parse(xml); }
        catch (XmlException) { return null; }
        return new Analysis(root).Of(root);
    }

    /// <summary>
    /// The step a generator adds to its answer: the measured size, the chain, and the verdict.
    /// <paramref name="size"/> null means no render was obtained - reported, never guessed.
    /// </summary>
    internal static JsonObject Step((int Width, int Height)? size, ChainResult? chain)
    {
        var within = size is { } s ? s.Width <= MaxWidth && s.Height <= MaxHeight : (bool?)null;
        var step = new JsonObject
        {
            ["step"] = "diagramSize",
            ["measured"] = size is not null,
            ["width"] = size?.Width,
            ["height"] = size?.Height,
            ["budget"] = $"{MaxWidth} x {MaxHeight}",
            ["withinBudget"] = within,
            ["longestChainStages"] = chain?.Stages,
            ["chainBudgetStages"] = MaxChainStages,
            ["longestChain"] = chain?.Chain.DeepClone(),
        };
        step["note"] = Note(size, chain);
        return step;
    }

    /// <summary>What the answer says about size - empty when the diagram is within budget.</summary>
    internal static string Note((int Width, int Height)? size, ChainResult? chain)
    {
        if (size is not { } s)
            return " The block diagram could not be rendered, so its size is NOT known - call " +
                   "lvai_render_diagrams and read the top-level PNG before calling it done.";
        if (s.Width <= MaxWidth && s.Height <= MaxHeight) return "";
        var wide = s.Width > MaxWidth;
        return $" THE BLOCK DIAGRAM IS OVER THE SIZE BUDGET: {s.Width} x {s.Height} px against " +
               $"{MaxWidth} x {MaxHeight}." +
               (wide
                   ? $" Width follows the longest dependency chain ({chain?.Stages ?? 0} stages here, " +
                     $"budget {MaxChainStages}) - fold a SEQUENTIAL stretch of `longestChain` into ONE " +
                     "new subVI, which puts one call where several stages were."
                   : "") +
               (s.Height > MaxHeight
                   ? " Height follows what sits in PARALLEL - move a group of parallel nodes into a subVI."
                   : "") +
               " The VI is written and valid; the size is what needs another pass.";
    }

    private sealed class Analysis
    {
        private readonly Dictionary<string, List<XElement>> _producers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<XElement>> _consumers = new(StringComparer.Ordinal);
        private readonly Dictionary<XElement, ChainResult> _memo = [];

        internal Analysis(XElement root)
        {
            foreach (var element in root.DescendantsAndSelf())
            {
                foreach (var net in Nets((string?)element.Attribute("outputs")))
                    Add(_producers, net, element);
                foreach (var net in Nets((string?)element.Attribute("inputs")))
                    Add(_consumers, net, element);
                // a Case selector and a loop's count read a net too, without an `inputs` list
                foreach (var key in new[] { "selectin", "count", "maxin" })
                    if ((string?)element.Attribute(key) is { Length: > 0 } net && net.Contains('.'))
                        Add(_consumers, net, element);
            }
        }

        private static void Add(Dictionary<string, List<XElement>> map, string net, XElement element)
        {
            if (!map.TryGetValue(net, out var list)) map[net] = list = [];
            list.Add(element);
        }

        private static IEnumerable<string> Nets(string? list) =>
            (list ?? "").Split(',')
                .Select(entry => entry.Split(':', 2))
                .Where(parts => parts.Length == 2 && parts[1].Length > 0)
                .Select(parts => parts[1]);

        /// <summary>The element of <paramref name="container"/>'s own diagram that holds <paramref name="element"/>.</summary>
        private static XElement? Representative(XElement element, XElement container)
        {
            var x = element;
            while (x is not null && x.Parent != container) x = x.Parent;
            if (x is null) return null;
            // A shift register's Left and Right are the two ends of a feedback path: kept apart,
            // or the loop's own register would read as a cycle.
            return x.Name.LocalName == "ShiftReg" && element.Parent == x ? element : x;
        }

        internal ChainResult Of(XElement container)
        {
            if (_memo.TryGetValue(container, out var known)) return known;

            // a Case or Event structure is as wide as its widest frame
            var frames = container.Elements("CaseFrame").ToList();
            if (container.Name.LocalName == "Structure" && frames.Count > 0)
            {
                var widest = frames.Select(Of).OrderByDescending(r => r.Stages).First();
                return _memo[container] = widest;
            }

            var members = new HashSet<XElement>();
            foreach (var element in container.Descendants())
                if (Representative(element, container) is { } member) members.Add(member);

            var edges = new Dictionary<XElement, HashSet<XElement>>();
            foreach (var (net, producers) in _producers)
            {
                if (!_consumers.TryGetValue(net, out var consumers)) continue;
                foreach (var producer in producers)
                {
                    if (Representative(producer, container) is not { } from) continue;
                    foreach (var consumer in consumers)
                        if (Representative(consumer, container) is { } to && to != from)
                        {
                            if (!edges.TryGetValue(from, out var set)) edges[from] = set = [];
                            set.Add(to);
                        }
                }
            }

            var best = new Dictionary<XElement, (double Length, XElement? Next)>();
            var onStack = new HashSet<XElement>();

            double Weight(XElement x) => x.Name.LocalName switch
            {
                "Structure" => 1 + Of(x).Stages,
                "Node" or "Call" => 1,
                _ => 0,
            };

            double Longest(XElement x)
            {
                if (best.TryGetValue(x, out var done)) return done.Length;
                if (!onStack.Add(x)) return 0;   // a cycle the analysis cannot order: counted once
                XElement? next = null;
                var tail = 0.0;
                foreach (var y in edges.GetValueOrDefault(x) ?? [])
                {
                    var length = Longest(y);
                    if (length > tail) { tail = length; next = y; }
                }
                onStack.Remove(x);
                var total = Weight(x) + tail;
                best[x] = (total, next);
                return total;
            }

            XElement? start = null;
            var stages = 0.0;
            foreach (var member in members)
            {
                var length = Longest(member);
                if (length > stages) { stages = length; start = member; }
            }

            var chain = new JsonArray();
            for (var x = start; x is not null; x = best[x].Next)
            {
                var weight = Weight(x);
                if (weight <= 0) continue;
                var entry = new JsonObject { ["element"] = Label(x), ["stages"] = weight };
                if (x.Name.LocalName == "Structure") entry["inside"] = Of(x).Chain.DeepClone();
                chain.Add(entry);
            }

            return _memo[container] = new ChainResult(stages, chain);
        }

        private static string Label(XElement x)
        {
            var name = (string?)x.Attribute("_name") ?? x.Name.LocalName;
            var target = (string?)x.Attribute("target");
            var fields = (string?)x.Attribute("fields");
            var text = x.Name.LocalName == "Call" && target is { Length: > 0 }
                ? target
                : target is { Length: > 0 } ? $"{name} {target}"
                : fields is { Length: > 0 } ? $"{name} {fields}"
                : name;
            return text.Replace(@"\3A", ":").Replace(@"\5C", @"\").Replace(@"\2C", ",");
        }
    }
}
