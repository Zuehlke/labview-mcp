using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Array constants as NI's converter lays them out (docs/cold-build-pc-ai-di-tdms.md, the pump
/// station's display fix, 2026-11-01). The pump station's diagram showed every array constant as a
/// row of greyed elements beside an empty frame. Two causes, both read off NI's build of the same
/// seven-constant probe:
/// 1. the ELEMENT (the array's own &lt;ddo&gt;) stayed at the donor's offset while the frame moved
///    right of the wider label - NI puts it 35 px into the frame's first part;
/// 2. the nRC/oRC anchor offset k is the centre of the array's final bounds, (w div 2, h div 2),
///    not the donor's offset - (83, 11) for 166 x 23, (56, 23) for 112 x 46, (25, 11) for a field.
/// The composed VI is read back with pylabview. SKIPS where the bundled interpreter is missing.
/// </summary>
public sealed class PylabviewArrayConstantTests
{
    private const string Err = "cluster{bool.status,int32.code,string.source}";
    private const string Ev = "array{cluster{string.Event Source,string.Event Text}}";

    private static readonly string Probe = $$"""
        <VI _name="ArrConst Probe.vi" description="Array constants.">
          <Control _name="error in" conIdx="11" connection="required" outputs="value:4201.value" type="{{Err}}" uid="4201" uid_parent="root" value="[false,0,]"/>
          <Constant _name="No Previous Pressures" outputs="value:4202.value" type="array{double}" uid="4202" uid_parent="root" value="[]"/>
          <Constant _name="No Previous Running" outputs="value:4203.value" type="array{bool}" uid="4203" uid_parent="root" value="[]"/>
          <Constant _name="No Events" outputs="value:4204.value" type="{{Ev}}" uid="4204" uid_parent="root" value="[]"/>
          <Constant _name="Message Template" outputs="value:4206.value" type="cluster{bool.Stop,array{double}.Pressures,array{bool}.Running,{{Ev}}.Events}" uid="4206" uid_parent="root" value="[false,[],[],[]]"/>
          <Indicator _name="P" conIdx="4" connection="recommended" inputs="value:4202.value" type="array{double}" uid="4210" uid_parent="root" value="[]"/>
          <Indicator _name="R" conIdx="6" connection="recommended" inputs="value:4203.value" type="array{bool}" uid="4211" uid_parent="root" value="[]"/>
          <Indicator _name="E" conIdx="8" connection="recommended" inputs="value:4204.value" type="{{Ev}}" uid="4212" uid_parent="root" value="[]"/>
          <Indicator _name="M" conIdx="10" connection="recommended" inputs="value:4206.value" type="cluster{bool.Stop,array{double}.Pressures,array{bool}.Running,{{Ev}}.Events}" uid="4214" uid_parent="root" value="[false,[],[],[]]"/>
          <Indicator _name="error out" conIdx="15" connection="recommended" inputs="value:4201.value" type="{{Err}}" uid="4215" uid_parent="root" value="[false,0,]"/>
        </VI>
        """;

    private static int[] Pair(string? text) =>
        Regex.Matches(text ?? "", "-?\\d+").Select(m => int.Parse(m.Value)).ToArray();

    private static async Task<List<XElement>?> ComposedArraysAsync(string dir)
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return null;
        var xml = Path.Combine(dir, "probe.xml");
        var vi = Path.Combine(dir, "ArrConst Probe.vi");
        await File.WriteAllTextAsync(xml, Probe);
        var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
        Assert.True(File.Exists(vi), composed.Message);
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
            ["-x", "-i", vi, "-m", Path.Combine(dir, "x", "ArrConstProbe.xml")], 120, default);
        Assert.True(extract.ExitCode == 0, extract.StdErr);
        var heap = XElement.Load(Directory.GetFiles(Path.Combine(dir, "x"), "*_BDHb.xml").Single());
        // a cluster's fields are SL__arrayElement entries of its zPlaneList, not <ddo>s
        return [.. heap.Descendants().Where(d => (string?)d.Attribute("class") == "indArr" && d.Element("bounds") is not null)];
    }

    [Fact]
    public async Task Every_array_constant_has_its_element_in_its_frame_and_its_anchor_at_its_centre()
    {
        var dir = Path.Combine(Path.GetTempPath(), "arrconst-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            if (await ComposedArraysAsync(dir) is not { } arrays) return;
            Assert.Equal(6, arrays.Count);        // three at the top level, three in the cluster

            var anchored = 0;
            foreach (var arr in arrays)
            {
                var box = Pair(arr.Element("bounds")!.Value);
                var frame = arr.Element("partsList")!.Elements()
                    .First(p => (string?)p.Attribute("class") == "cosm" && p.Element("bounds") is not null);
                var element = Pair(arr.Element("ddo")!.Element("bounds")!.Value);
                Assert.Equal(Pair(frame.Element("bounds")!.Value)[1] + 35, element[1]);

                // where the pair is set, it is -(place + centre); the centre part is what is fixed
                if (arr.Element("nRC") is { } nrc && Pair(nrc.Value) is { Length: 2 } n && n[0] < 0)
                {
                    anchored++;
                    var placeH = -n[0] - (box[3] - box[1]) / 2;
                    var placeV = -n[1] - (box[2] - box[0]) / 2;
                    Assert.Equal(Pair(arr.Element("oRC")!.Value), n);
                    Assert.True(placeH >= -2000 && placeV >= -2000, $"anchor {n[0]},{n[1]} for {string.Join(",", box)}");
                    if (box[0] >= 0) Assert.Equal((box[1], box[0]), (placeH, placeV));   // top level: own place
                }
            }
            Assert.True(anchored >= 3, $"{anchored} arrays carry an anchor");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>
/// Where the composer looks for a Call target (AixmlTools.SearchFolders). The AIXML's own folder was
/// searched three levels deep even when it was our scratch tree, %TEMP%\LabVIEWMCP - into the
/// layout step's backups - and a regenerated test VI linked its subject to such a copy
/// (2026-11-01). The project above the target VI is searched instead.
/// </summary>
public sealed class ComposerSearchFolderTests
{
    [Fact]
    public void Our_scratch_tree_is_never_a_search_folder()
    {
        var scratchAixml = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "Test X.xml");
        Assert.True(AixmlTools.IsOurScratch(Path.GetDirectoryName(scratchAixml)!));
        Assert.Empty(AixmlTools.SearchFolders(scratchAixml));
        Assert.False(AixmlTools.IsOurScratch(Path.Combine(Path.GetTempPath(), "LabVIEWMCPX")));
    }

    [Fact]
    public void The_project_above_the_target_is_searched()
    {
        var root = Path.Combine(Path.GetTempPath(), "searchfolders-" + Guid.NewGuid().ToString("n")[..8]);
        var tests = Path.Combine(root, "Tests", "Acquisition");
        Directory.CreateDirectory(tests);
        try
        {
            File.WriteAllText(Path.Combine(root, "P.lvproj"), "<Project/>");
            var scratchAixml = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "Test X.xml");
            Assert.Equal([root], AixmlTools.SearchFolders(scratchAixml, Path.Combine(tests, "Test X.vi")));

            var ownAixml = Path.Combine(tests, "Test X.xml");
            Assert.Equal([tests, root], AixmlTools.SearchFolders(ownAixml, Path.Combine(tests, "Test X.vi")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
