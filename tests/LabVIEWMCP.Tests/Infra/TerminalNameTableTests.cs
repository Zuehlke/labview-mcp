using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The composer wires a primitive's terminal BY NAME through the (primResID, parmIndex) -> name
/// tables, so two names swapped in a table wire every caller to the wrong slot. Found 2026-11-11:
/// `Get Queue Status` `elements` landed on the `queue name` string slot ("the type of the source is
/// string", an agent's double queue - NI's converter took the same document and ran it), and the
/// same audit found Move (`new path` / `cancelled`), New VI (`password` / `type specifier`) and
/// Insert Into Map (`map out` / `value unchanged?`), all from terminal-names-learned.tsv. Move and
/// New VI composed broken on the old table and executable on the fixed one; NI validated all four.
///
/// The evidence is the harvested part itself: its types carry labels. A slot whose type is labelled
/// with the name the tables give ANOTHER slot of a DIFFERENT kind of type is a swap. Labels often
/// come from the donor's wires (`queue out`, `error out`), which is why the kinds must differ;
/// numerics count as one kind.
/// </summary>
public sealed class TerminalNameTableTests
{
    /// <summary>
    /// Donor labels that read like a swap and are not, each checked against the slot types:
    /// 23810 (writer endpoint): slot 3 is the endpoint REFNUM, labelled `writer name` by the donor's
    /// wire; slot 11 is the `writer name` STRING - the table agrees with both types.
    /// </summary>
    private static readonly HashSet<string> NotASwap = ["23810:3"];

    private static string Composer => Path.Combine(RepoTree.Root, "scripts", "aixml-pylabview");

    private static Dictionary<string, Dictionary<string, string>> Names()
    {
        var names = new Dictionary<string, Dictionary<string, string>>();
        foreach (var path in new[]
                 {
                     Path.Combine(RepoTree.Root, "experiments", "pylabview", "terminal-names.tsv"),
                     Path.Combine(Composer, "terminal-names-learned.tsv"),
                     Path.Combine(Composer, "terminal-names-extra.tsv"),
                 })
        {
            foreach (var line in File.ReadLines(path))
            {
                var f = line.Split('\t');
                if (f.Length < 4 || f[0].Length == 0 || !f[0].All(char.IsDigit)) continue;
                (names.TryGetValue(f[0], out var n) ? n : names[f[0]] = [])[f[2]] = f[3];
            }
        }
        return names;
    }

    private static string Kind(XElement type)
    {
        var t = (string?)type.Attribute("Type") ?? "";
        return t.StartsWith("Num", StringComparison.Ordinal) || t.StartsWith("Unit", StringComparison.Ordinal) ? "numeric" : t;
    }

    [Fact]
    public void No_terminal_name_sits_on_a_slot_the_part_types_for_another_name()
    {
        var names = Names();
        var swaps = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Composer, "parts"), "prim__*.xml"))
        {
            var rid = Regex.Match(Path.GetFileName(file), @"prim__(\d+)\.xml").Groups[1].Value;
            if (!names.TryGetValue(rid, out var byParm)) continue;
            var part = XElement.Load(file);
            var types = part.Element("types")!.Elements().ToList();
            var slots = part.Element("heap")!.Elements().First().Element("termList")!.Elements()
                .Select(t => t.Element("dco")!)
                .Select(d => (Parm: (string?)d.Element("parmIndex") is { } p && p != "0" ? p : "out",
                              Id: int.Parse(Regex.Match((string)d.Element("typeDesc")!, @"\d+").Value)))
                .ToList();
            var first = slots.Min(s => s.Id);
            // several terms without a parmIndex (a growable node's) all read as `out`; the last
            // wins, as in the audit this test was measured with
            var typeOf = new Dictionary<string, XElement>();
            foreach (var s in slots.Where(s => s.Id - first < types.Count)) typeOf[s.Parm] = types[s.Id - first];
            var slotOf = byParm.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
            foreach (var (parm, type) in typeOf)
            {
                if ((string?)type.Attribute("Label") is not { } label
                    || !slotOf.TryGetValue(label, out var other) || other == parm
                    || !typeOf.TryGetValue(other, out var otherType) || Kind(otherType) == Kind(type)
                    || NotASwap.Contains($"{rid}:{parm}")) continue;
                swaps.Add($"{rid} slot {parm} ({Kind(type)}) is labelled '{label}', the name of slot {other} ({Kind(otherType)})");
            }
        }
        Assert.True(swaps.Count == 0, string.Join(Environment.NewLine, swaps));
    }

    [Fact]
    public void The_four_corrected_pairs_stay_corrected()
    {
        var names = Names();
        Assert.Equal("elements", names["9110"]["5"]);
        Assert.Equal("queue name", names["9110"]["6"]);
        Assert.Equal("cancelled", names["8068"]["1"]);
        Assert.Equal("new path", names["8068"]["3"]);
        Assert.Equal("type specifier VI Refnum (for type only)", names["8014"]["5"]);
        Assert.Equal("password", names["8014"]["6"]);
        Assert.Equal("value unchanged?", names["2456"]["1"]);
        Assert.Equal("map out", names["2456"]["3"]);
    }
}
