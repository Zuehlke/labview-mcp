// Ported from wirelint (https://github.com/felipefoz/wirelint), v0.2.0, commit
// 280d621d0846f2307b2ab4325fdf60ab0b1f287c, under the following licence:
//
// MIT License
//
// Copyright (c) 2026 WireLint contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The house rules a SAVED LabVIEW file can be checked against with no LabVIEW at all: source-only
/// and no compiled code, automatic error handling off, a description, data-type terminals, and a
/// VI listed by the library or class it says owns it. Read from the resource blocks (LVSR, STRG,
/// BDHb, CONP/VCTP, LIvi, VICD) and from the XML of .lvproj/.lvlib/.lvclass.
///
/// WHY. lvai_check_aixml checks AIXML before generation, and lvai_generate_vi checks the VI it
/// writes. Files written by any other route - the graft into a supplied VI, Delacor's scripters,
/// NI's providers - were checked by nothing. The audit of 2026-11-21 over 618 files of our builds
/// found 12 grafted main VIs with automatic error handling ON and 56 DQMH files with compiled code
/// (docs/offline-audit-evaluation.md).
///
/// The method is wirelint's (github.com/felipefoz/wirelint, v0.2.0), ported and checked against
/// pylabview's full parse: 480 of 480 fields in 60 VIs. Two places differ from wirelint, both
/// measured: a polymorphic VI's CONP names a PolyVI type (0xF2) and a global VI's a Cluster (0x50),
/// which is no connector pane and no error (wirelint aborts the file); and a PTH0 relative path is relative to the FILE, an empty
/// component meaning "up one" - `Lib\LibVi.vi` stores ['', '', 'HkLibG.lvlib'].
/// A file this reader cannot parse is an analysis error, never a pass.
/// </summary>
internal static class LvFileAudit
{
    /// <summary>
    /// THE UPSTREAM VERSION THIS READER WAS PORTED FROM - compare against it when wirelint moves
    /// (`git diff v0.2.0..<new> -- src/formats src/rules.rs`). Ported: src/formats/rsrc.rs (LVSR,
    /// STRG, BDPW, LIvi/PTH0), heap.rs (BDHb terminals), connector.rs (CONP/VCTP), xml.rs, and the
    /// rule semantics in src/rules.rs. docs/offline-audit-evaluation.md §Upstream keeps the same
    /// line, and a test holds the two together.
    /// </summary>
    internal const string UpstreamBaseline =
        "felipefoz/wirelint v0.2.0 280d621d0846f2307b2ab4325fdf60ab0b1f287c (2026-11-20)";

    internal static readonly string[] ViExtensions = [".vi", ".vit", ".vim"];
    internal static readonly string[] FileExtensions = [".vi", ".vit", ".vim", ".ctl", ".lvlib", ".lvclass", ".lvproj"];

    internal sealed record Facts(
        string Path,
        string Kind,                 // vi, ctl, project, library, class
        bool SourceOnly,
        bool? CompiledCode,          // a VICD block in the file's own map (VI and ctl)
        bool? AutomaticErrorHandling,
        bool? AllowDebugging,
        bool HasDescription,
        int? ConnectorPane,          // 4800 + pattern index; null without a Function pane
        string? PaneLess,            // "polymorphic" or "global": a VI whose CONP names no pane
        int IconTerminals,
        int DataTypeTerminals,
        string? Owner,               // resolved owning .lvlib/.lvclass of a VI or .ctl
        IReadOnlyList<string>? Members, // resolved VI members of a .lvlib/.lvclass
        string? Version);

    internal sealed record Finding(string Path, string Rule, string Severity, string Message);

    internal sealed record Report(IReadOnlyList<Facts> Files, IReadOnlyList<(string Path, string Error)> Errors,
                                  IReadOnlyList<Finding> Findings, int Skipped);

    /// <summary>Rule ids, severity and what each one says - the one table the tool, the docs and
    /// the tests read.</summary>
    internal static readonly (string Id, string Severity, string Applies)[] Rules =
    [
        ("compiled-code", "error", "VI and .ctl: source-only flag clear, or a VICD block in the file"),
        ("automatic-error-handling", "error", "VI: Enable automatic error handling is on"),
        ("orphaned-vi", "error", "VI or .ctl: its owning .lvlib/.lvclass (scanned too) does not list it"),
        ("description", "warning", "VI, .ctl, .lvlib, .lvclass: no description"),
        ("terminal-style", "warning", "VI: a front-panel terminal shown as an icon"),
        ("source-only-default", "warning", ".lvproj, .lvlib, .lvclass: NI.LV.All.SourceOnly not true"),
    ];

    // ------------------------------------------------------------------ scan and rules

    /// <summary>Every LabVIEW file under the roots (a root may be a file), minus folders whose
    /// name is in <paramref name="excludeFolders"/> and folders starting with a dot.</summary>
    internal static List<string> Discover(IEnumerable<string> roots, IReadOnlyCollection<string> excludeFolders,
                                          out int skipped)
    {
        skipped = 0;
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var full = System.IO.Path.GetFullPath(root);
            if (File.Exists(full))
            {
                if (IsLabViewFile(full)) files.Add(full);
                continue;
            }
            if (!Directory.Exists(full)) continue;
            var pending = new Stack<string>([full]);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = System.IO.Path.GetFileName(sub);
                    if (name.StartsWith('.') || excludeFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }
                    pending.Push(sub);
                }
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (IsLabViewFile(f)) files.Add(f);
            }
        }
        return [.. files];
    }

    private static bool IsLabViewFile(string path) =>
        FileExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static Report Audit(IEnumerable<string> roots, IReadOnlyCollection<string> excludeFolders,
                                 IReadOnlyCollection<string> ignoreRules)
    {
        var paths = Discover(roots, excludeFolders, out var skipped);
        var facts = new List<Facts>();
        var errors = new List<(string, string)>();
        foreach (var p in paths)
        {
            try { facts.Add(Read(p)); }
            catch (Exception e) when (e is InvalidDataException or IOException or OverflowException
                                          or ArgumentOutOfRangeException or System.Xml.XmlException
                                          or UnauthorizedAccessException)
            {
                errors.Add((p, e.Message));
            }
        }
        return new Report(facts, errors, Check(facts, ignoreRules), skipped);
    }

    internal static List<Finding> Check(IReadOnlyList<Facts> files, IReadOnlyCollection<string> ignoreRules)
    {
        var owners = files.Where(f => f.Members is not null)
                          .ToDictionary(f => f.Path, f => new HashSet<string>(f.Members!, StringComparer.OrdinalIgnoreCase),
                                        StringComparer.OrdinalIgnoreCase);
        var findings = new List<Finding>();
        void Add(Facts f, string rule, string message)
        {
            if (ignoreRules.Contains(rule, StringComparer.OrdinalIgnoreCase)) return;
            var severity = Rules.First(r => r.Id == rule).Severity;
            findings.Add(new Finding(f.Path, rule, severity, message));
        }

        foreach (var f in files)
        {
            var isVi = f.Kind == "vi";
            if (isVi || f.Kind == "ctl")
            {
                if (!f.SourceOnly)
                    Add(f, "compiled-code", "Separate compiled code from source is OFF - the file keeps compiled code.");
                else if (f.CompiledCode == true)
                    Add(f, "compiled-code", "Marked source-only but the file still carries a VICD block.");
            }
            else if (!f.SourceOnly)
                Add(f, "source-only-default", "NI.LV.All.SourceOnly is not true - new members get compiled code.");

            // Not on a polymorphic or global VI: neither runs a diagram of its own, yet 29 of the
            // 43 in vi.lib carry the bit (Write To Spreadsheet File.vi) - a finding nobody can act on.
            if (isVi && f.PaneLess is null && f.AutomaticErrorHandling == true)
                Add(f, "automatic-error-handling", "Enable automatic error handling is ON (VI Properties > " +
                       "Execution) - an unwired error pops a dialog; lvai_set_vi_execution automaticErrorHandling=false.");
            if (!f.HasDescription && f.Kind != "project")
                Add(f, "description", "No description.");
            if (isVi && f.IconTerminals > 0)
                Add(f, "terminal-style", $"{f.IconTerminals} front-panel terminal(s) shown as icons, " +
                                         $"{f.DataTypeTerminals} as data types.");
            if (f.Owner is not null && owners.TryGetValue(f.Owner, out var members) && !members.Contains(f.Path))
                Add(f, "orphaned-vi", $"Links to '{System.IO.Path.GetFileName(f.Owner)}' as its owner, which does not list it.");
        }
        return findings;
    }

    // ------------------------------------------------------------------ one file

    internal static Facts Read(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".lvproj" or ".lvlib" or ".lvclass" => ReadXml(path),
            _ => ReadBinary(path, File.ReadAllBytes(path)),
        };
    }

    internal static Facts ReadBinary(string path, byte[] file)
    {
        var r = new Rsrc(file);
        var isVi = r.FileType is "LVIN" or "sVIN";
        if (!isVi && r.FileType is not ("LVCC" or "sVCC"))
            throw new InvalidDataException($"unsupported resource file type '{r.FileType}'");

        var lvsr = r.Sections("LVSR");
        if (lvsr.Count != 1) throw new InvalidDataException("expected exactly one LVSR section");
        var save = lvsr[0].Bytes;
        if (save.Length < 28) throw new InvalidDataException("truncated LVSR save record");
        var version = U32(save, 0);
        var flags2 = U32(save, 8);
        var instrument = U32(save, 24);

        bool hasDescription = false;
        var strg = r.Sections("STRG").Where(s => s.Index == 0).ToList();
        if (strg.Count > 1) throw new InvalidDataException("duplicate STRG section 0");
        if (strg.Count == 1)
        {
            var p = strg[0].Bytes;
            var text = Slice(p, 4, (int)U32(p, 0));
            hasDescription = text.Any(b => b is not (0x20 or 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0xCA or 0x00));
        }

        int? pane = null;
        string? paneLess = null;
        int icons = 0, dataTypes = 0;
        string? owner = null;
        if (isVi)
        {
            var conp = r.Sections("CONP");
            var vctp = r.Sections("VCTP");
            if (conp.Count == 1 && vctp.Count == 1)
                (pane, paneLess) = ConnectorPane(conp[0].Bytes, vctp[0].Bytes);
            var bd = r.Sections("BDHb");
            if (bd.Count > 1) throw new InvalidDataException("duplicate BDHb section");
            if (bd.Count == 1) (icons, dataTypes) = Terminals(bd[0].Bytes);
        }
        // A typedef in a library carries the same VILB link (Broadcast Events--cluster.ctl:
        // ['', 'Wash Station.lvlib']), and the library lists it as Type="VI".
        owner = OwnerLink(r.Sections("LIvi"), path);

        return new Facts(path, isVi ? "vi" : "ctl",
            SourceOnly: (flags2 & (1u << 10)) != 0,
            CompiledCode: r.Lists("VICD"),
            AutomaticErrorHandling: isVi ? (flags2 & 0x2000_0000u) != 0 : null,
            AllowDebugging: isVi ? (instrument & (1u << 9)) != 0 : null,
            HasDescription: hasDescription,
            ConnectorPane: pane, PaneLess: paneLess,
            IconTerminals: icons, DataTypeTerminals: dataTypes,
            Owner: owner, Members: null,
            Version: VersionText(version));
    }

    private static string VersionText(uint word)
    {
        var major = ((word >> 28) & 0xF) * 10 + ((word >> 24) & 0xF);
        var minor = (word >> 20) & 0xF;
        return $"{major}.{minor}";
    }

    internal static Facts ReadXml(string path)
    {
        var root = XDocument.Load(path).Root ?? throw new InvalidDataException("empty XML");
        var kind = root.Name.LocalName switch
        {
            "Project" => "project",
            "Library" => "library",
            "LVClass" => "class",
            var other => throw new InvalidDataException($"unexpected root element <{other}>"),
        };
        XElement? Prop(string name)
        {
            var hits = root.Elements("Property").Where(p => (string?)p.Attribute("Name") == name).ToList();
            if (hits.Count > 1) throw new InvalidDataException($"duplicate owner property {name}");
            return hits.FirstOrDefault();
        }
        var sourceOnly = Prop("NI.LV.All.SourceOnly")?.Value.Trim() == "true";
        var description = Prop(kind == "project" ? "NI.Project.Description" : "NI.Lib.Description")?.Value ?? "";
        IReadOnlyList<string>? members = null;
        if (kind != "project")
        {
            members = root.Descendants("Item")
                .Where(i => (string?)i.Attribute("Type") == "VI" && i.Attribute("URL") is not null)
                .Select(i => ResolveUrl(path, (string)i.Attribute("URL")!))
                .Where(p => p is not null).Select(p => p!).ToList();
        }
        return new Facts(path, kind, sourceOnly, null, null, null,
            HasDescription: !string.IsNullOrWhiteSpace(description.Replace(' ', ' ')),
            ConnectorPane: null, PaneLess: null, IconTerminals: 0, DataTypeTerminals: 0,
            Owner: null, Members: members, Version: (string?)root.Attribute("LVVersion"));
    }

    /// <summary>A library item URL is relative to the library FILE: "../X.vi" is a sibling.
    /// Absolute or token paths (`/&lt;vilib&gt;/...`) are not checked.</summary>
    internal static string? ResolveUrl(string ownerFile, string url)
    {
        if (url.StartsWith('/') || url.Contains(':') || url.StartsWith('<')) return null;
        var current = ownerFile;
        foreach (var part in url.Split('/'))
        {
            if (part == "..") current = System.IO.Path.GetDirectoryName(current)!;
            else if (part is "" or ".") continue;
            else current = System.IO.Path.Combine(current, part);
        }
        return System.IO.Path.GetFullPath(current);
    }

    // ------------------------------------------------------------------ resource map

    private sealed class Rsrc
    {
        private readonly byte[] _f;
        private readonly int _table, _count, _data, _dataEnd;
        public string FileType { get; }

        public Rsrc(byte[] f)
        {
            _f = f;
            if (f.Length < 32 || Encoding.ASCII.GetString(f, 0, 6) != "RSRC\r\n")
                throw new InvalidDataException("not a LabVIEW resource file (no RSRC header)");
            if (Encoding.ASCII.GetString(f, 12, 4) != "LBVW")
                throw new InvalidDataException("not a LabVIEW resource file (creator)");
            FileType = Encoding.ASCII.GetString(f, 8, 4);
            // The info header the FIRST header names - RsrcMap.Blocks explains why not the chain.
            var info = I32(f, 16);
            _data = I32(f, 24);
            _dataEnd = checked(_data + I32(f, 28));
            if (_data < 32 || _dataEnd > f.Length) throw new InvalidDataException("RSRC data area out of bounds");
            _table = checked(info + I32(f, checked(info + 32 + 12)));
            _count = checked(I32(f, _table) + 1);
            if (_count > 4096) throw new InvalidDataException("implausible RSRC block count");
            Slice(f, _table, 4 + _count * 12);
        }

        public bool Lists(string type)
        {
            for (var i = 0; i < _count; i++)
                if (Encoding.Latin1.GetString(_f, _table + 4 + i * 12, 4) == type) return true;
            return false;
        }

        public List<(int Index, byte[] Bytes)> Sections(string type)
        {
            var result = new List<(int, byte[])>();
            for (var i = 0; i < _count; i++)
            {
                var entry = _table + 4 + i * 12;
                if (Encoding.Latin1.GetString(_f, entry, 4) != type) continue;
                var n = checked(I32(_f, entry + 4) + 1);
                if (n > 4096) throw new InvalidDataException($"implausible {type} section count");
                var list = I32(_f, entry + 8);
                for (var s = 0; s < n; s++)
                {
                    var rec = checked(_table + list + s * 20);
                    var at = checked(_data + I32(_f, rec + 12));
                    var len = I32(_f, at);
                    if (checked(at + 4 + len) > _dataEnd)
                        throw new InvalidDataException($"{type} section exceeds the data area");
                    result.Add((I32(_f, rec), Slice(_f, at + 4, len)));
                }
            }
            return result;
        }
    }

    // ------------------------------------------------------------------ connector pane

    private const byte FunctionType = 0xF0, PolyViType = 0xF2, ClusterType = 0x50;

    /// <summary>The pane's catalogue id (4800 + pattern index), or no pane for a polymorphic VI
    /// (its CONP names its PolyVI type, 0xF2: 39 of 1532 vi.lib files) or a global VI (a Cluster,
    /// 0x50: 4 of them). The pattern shift by 3 is from docs/connector-pane-patterns.tsv.</summary>
    internal static (int? Pane, string? PaneLess) ConnectorPane(byte[] conp, byte[] vctp)
    {
        if (conp.Length != 2) throw new InvalidDataException("unsupported CONP payload size");
        var typeId = (conp[0] << 8) | conp[1];
        var decoded = Inflate(vctp, "VCTP");
        var c = 4;
        var count = I32(decoded, 0);
        if (count > 1_000_000) throw new InvalidDataException("implausible VCTP type count");
        var flat = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            var start = c;
            flat.Add(start);
            var len = Var(decoded, ref c);
            if (len < 4) throw new InvalidDataException("VCTP type descriptor smaller than its header");
            c = checked(start + len);
            if (c > decoded.Length) throw new InvalidDataException("VCTP type descriptor exceeds the pool");
        }
        var top = new List<int>();
        var topCount = Var(decoded, ref c);
        for (var i = 0; i < topCount; i++) top.Add(Var(decoded, ref c));
        if (typeId < 1 || typeId > top.Count) throw new InvalidDataException("CONP type index out of range");
        var flatId = top[typeId - 1];
        if (flatId >= flat.Count) throw new InvalidDataException("CONP flat type id out of range");

        c = flat[flatId];
        Var(decoded, ref c);
        var header = Slice(decoded, c, 2);
        c += 2;
        if (header[1] == PolyViType) return (null, "polymorphic");
        if (header[1] == ClusterType) return (null, "global");
        if (header[1] != FunctionType) throw new InvalidDataException($"CONP names type 0x{header[1]:X2}, not a pane");
        var clients = Var(decoded, ref c);
        for (var i = 0; i < clients; i++) Var(decoded, ref c);
        var rest = Slice(decoded, c, 4);
        var pattern = ((rest[2] << 8) | rest[3]) >> 3;
        if (pattern >= 36) throw new InvalidDataException("connector pane pattern code outside the 36 known");
        return (4800 + pattern, null);
    }

    /// <summary>LabVIEW's 16-bit, or 32-bit with the top bit set, variable-size field.</summary>
    private static int Var(byte[] b, ref int c)
    {
        var s = Slice(b, c, 2);
        c += 2;
        var v = (s[0] << 8) | s[1];
        if ((v & 0x8000) == 0) return v;
        var lo = Slice(b, c, 2);
        c += 2;
        return ((v & 0x7FFF) << 16) | (lo[0] << 8) | lo[1];
    }

    // ------------------------------------------------------------------ block-diagram heap

    private const int ArrayElement = -6, ClassAttr = -2, FpTerm = 22, ObjFlags = 172;
    private const uint IconStyle = 0x0400_0000;

    /// <summary>Front-panel terminals (fPTerm) by display style: objFlags bit 26 set is icon style.
    /// The bit is wirelint's; our 520 VIs all read data-type, as our layout draws them, and 82 of
    /// 1281 NI VIs carry icon terminals - not yet confirmed by toggling it in LabVIEW.</summary>
    internal static (int Icons, int DataTypes) Terminals(byte[] bdhb)
    {
        var heap = Inflate(bdhb, "BDHb");
        if (I32(heap, 0) != heap.Length - 4) throw new InvalidDataException("inconsistent BDHb heap length");
        int c = 4, icons = 0, dataTypes = 0;
        var stack = new Stack<(int Tag, int? Class)>();
        while (c < heap.Length)
        {
            var h = Slice(heap, c, 2);
            c += 2;
            var size = h[0] >> 5;
            var hasAttrs = (h[0] & 0x10) != 0;
            var scope = (h[0] >> 2) & 3;
            var raw = ((h[0] & 3) << 8) | h[1];
            var tag = raw == 1023 ? S32(heap, ref c) : raw - 31;
            int? cls = null;
            if (hasAttrs)
            {
                var n = UVar(heap, ref c);
                for (var i = 0; i < n; i++)
                {
                    var id = SVar(heap, ref c);
                    var value = SShort(heap, ref c);
                    if (id == ClassAttr) cls = value;
                }
            }
            var length = size switch
            {
                0 or 7 => 0,
                >= 1 and <= 4 => size,
                6 => UVar(heap, ref c),
                _ => throw new InvalidDataException("unsupported BDHb value size"),
            };
            var content = Slice(heap, c, length);
            c += length;
            switch (scope)
            {
                case 0 or 1:
                    if (tag == ObjFlags && stack.TryPeek(out var parent) && parent == (ArrayElement, FpTerm))
                    {
                        if (content.Length > 4) throw new InvalidDataException("oversized terminal objFlags");
                        uint flags = 0;
                        foreach (var b in content) flags = (flags << 8) | b;
                        if ((flags & IconStyle) != 0) icons++; else dataTypes++;
                    }
                    if (scope == 0) stack.Push((tag, cls));
                    break;
                case 2:
                    if (!stack.TryPop(out var open) || open.Tag != tag)
                        throw new InvalidDataException("mismatched BDHb closing tag");
                    break;
                default:
                    throw new InvalidDataException("unsupported BDHb scope");
            }
        }
        if (stack.Count != 0) throw new InvalidDataException("unclosed BDHb scope");
        return (icons, dataTypes);
    }

    private static int S32(byte[] b, ref int c) { var v = BinaryPrimitives.ReadInt32BigEndian(Slice(b, c, 4)); c += 4; return v; }
    private static int S16(byte[] b, ref int c) { var v = BinaryPrimitives.ReadInt16BigEndian(Slice(b, c, 2)); c += 2; return v; }

    private static int UVar(byte[] b, ref int c)
    {
        var first = Slice(b, c, 1)[0];
        c += 1;
        if (first == 255) { var v = BinaryPrimitives.ReadUInt16BigEndian(Slice(b, c, 2)); c += 2; return v; }
        if (first == 254) return S32(b, ref c);
        return first;
    }

    private static int SVar(byte[] b, ref int c)
    {
        var v = (sbyte)Slice(b, c, 1)[0];
        c += 1;
        return v switch { -128 => S16(b, ref c), -127 => S32(b, ref c), _ => v };
    }

    private static int SShort(byte[] b, ref int c)
    {
        var v = S16(b, ref c);
        return v == -32768 ? S32(b, ref c) : v;
    }

    // ------------------------------------------------------------------ owner link

    /// <summary>The owning library or class a VI's linker table names (the VILB entry's PTH0),
    /// resolved against the VI file; null without one or for an absolute path.</summary>
    internal static string? OwnerLink(IReadOnlyList<(int Index, byte[] Bytes)> livi, string viPath)
    {
        string? owner = null;
        foreach (var (index, p) in livi)
        {
            if (index != 0) continue;
            var vilb = IndexOf(p, "VILB"u8, 0);
            if (vilb < 0) continue;
            if (IndexOf(p, "VILB"u8, vilb + 4) >= 0) throw new InvalidDataException("multiple VILB entries");
            var pth = IndexOf(p, "PTH0"u8, vilb + 4);
            if (pth < 0) throw new InvalidDataException("VILB entry without its PTH0 path");
            var length = I32(p, pth + 4);
            var end = checked(pth + 8 + length);
            Slice(p, pth, end - pth);
            var type = (p[pth + 8] << 8) | p[pth + 9];
            var count = (p[pth + 10] << 8) | p[pth + 11];
            if (count > 128) throw new InvalidDataException("too many PTH0 components");
            var c = pth + 12;
            var current = viPath;
            for (var i = 0; i < count; i++)
            {
                var len = Slice(p, c, 1)[0];
                var part = Encoding.Latin1.GetString(Slice(p, c + 1, len));
                c += 1 + len;
                current = part.Length == 0 ? System.IO.Path.GetDirectoryName(current)!
                                           : System.IO.Path.Combine(current, part);
            }
            if (c != end) throw new InvalidDataException("inconsistent PTH0 length");
            if (type != 1 || count == 0) continue;     // absolute or token: not checked
            owner = System.IO.Path.GetFullPath(current);
        }
        return owner;
    }

    private static int IndexOf(byte[] b, ReadOnlySpan<byte> tag, int from) =>
        from >= b.Length ? -1 : b.AsSpan(from).IndexOf(tag) is var i and >= 0 ? from + i : -1;

    // ------------------------------------------------------------------ bytes

    private const int MaxInflated = 64 * 1024 * 1024;

    private static byte[] Inflate(byte[] payload, string what)
    {
        var expected = I32(payload, 0);
        if (expected > MaxInflated) throw new InvalidDataException($"{what} exceeds 64 MiB");
        using var z = new ZLibStream(new MemoryStream(payload, 4, payload.Length - 4), CompressionMode.Decompress);
        var output = new byte[expected];
        var read = 0;
        try
        {
            while (read < expected && z.Read(output, read, expected - read) is var n and > 0) read += n;
            if (read != expected || z.ReadByte() != -1)
                throw new InvalidDataException($"inconsistent compressed {what} length");
        }
        catch (InvalidDataException e) when (!e.Message.StartsWith("inconsistent"))
        {
            throw new InvalidDataException($"invalid compressed {what}: {e.Message}");
        }
        return output;
    }

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(Slice(b, at, 4));

    private static int I32(byte[] b, int at) => checked((int)U32(b, at));

    private static byte[] Slice(byte[] b, int at, int length)
    {
        if (at < 0 || length < 0 || at > b.Length - length)
            throw new InvalidDataException($"read of {length} bytes at {at} is outside {b.Length}");
        return b.AsSpan(at, length).ToArray();
    }
}
