// ============================================================================================
// IconOwnersTests.cs - what the icon tools read off a .lvclass / .lvlib before LabVIEW, and the
// offline probe of a VI's icon layer data.
// ============================================================================================
using System.Buffers.Binary;
using System.Text;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The member list decides what ApplyIconToVIs is asked to touch and what gets saved, so it is
/// read from the owner file and pinned here: URLs relative to the owner FILE treated as a
/// directory, virtual folders descended, only .vi members counted as icon targets, and an owner
/// found only when it actually LISTS the VI.
/// </summary>
public sealed class IconOwnersTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-owner-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    internal static string ClassXml(params string[] memberUrls) => $"""
        <?xml version='1.0' encoding='UTF-8'?>
        <LVClass LVVersion="20008000">
        	<Property Name="NI.Lib.Icon" Type="Bin">abc</Property>
        	<Item Name="Foo.ctl" Type="Class Private Data" URL="Foo.ctl"/>
        	<Item Name="public" Type="Folder">
        {string.Join("\n", memberUrls.Select(u => $"\t\t<Item Name=\"{Path.GetFileName(u)}\" Type=\"VI\" URL=\"{u}\"/>"))}
        	</Item>
        </LVClass>
        """;

    internal static string LibraryXml(params string[] itemUrls) => $"""
        <?xml version='1.0' encoding='UTF-8'?>
        <Library LVVersion="26008000">
        	<Item Name="Nested.lvclass" Type="LVClass" URL="../Nested/Nested.lvclass"/>
        	<Item Name="api" Type="Folder">
        {string.Join("\n", itemUrls.Select(u => $"\t\t<Item Name=\"{Path.GetFileName(u)}\" Type=\"VI\" URL=\"{u}\"/>"))}
        	</Item>
        	<Item Name="Lib Helper.vi" Type="VI" URL="/&lt;vilib&gt;/x/Lib Helper.vi"/>
        </Library>
        """;

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void A_class_lists_its_members_by_absolute_path_through_virtual_folders()
    {
        var cls = Write(@"Foo\Foo.lvclass", ClassXml("../Read.vi", "../Sub/Write.vi", "../Type.ctl"));

        var owner = IconOwners.Read(cls);

        Assert.Equal(IconOwners.Class, owner.Kind);
        Assert.Equal("20008000", owner.LvVersion);
        Assert.Equal("abc", owner.IconProperty);
        Assert.Equal([Path.Combine(_dir, @"Foo\Read.vi"), Path.Combine(_dir, @"Foo\Sub\Write.vi")],
                     owner.Vis.Select(m => m.Path));
        // a .ctl member is listed but is no icon target - only .vi was measured
        Assert.Contains(owner.Members, m => m.Name == "Type.ctl" && !m.IsVi);
    }

    [Fact]
    public void A_library_lists_its_own_vis_but_not_a_nested_class_or_a_symbolic_url()
    {
        var lib = Write(@"Lib\Lib.lvlib", LibraryXml("../A.vi", "../api/B.vi"));

        var owner = IconOwners.Read(lib);

        Assert.Equal(IconOwners.Library, owner.Kind);
        Assert.Equal([Path.Combine(_dir, @"Lib\A.vi"), Path.Combine(_dir, @"Lib\api\B.vi")],
                     owner.Vis.Select(m => m.Path));
    }

    [Fact]
    public void A_file_whose_root_does_not_match_its_extension_is_refused()
    {
        var fake = Write(@"X\X.lvlib", ClassXml("../A.vi"));

        Assert.Throws<InvalidDataException>(() => IconOwners.Read(fake));
        Assert.Throws<InvalidDataException>(() => IconOwners.Read(Write(@"X\X.lvproj", "<Project/>")));
    }

    [Fact]
    public void The_owner_of_a_vi_is_the_file_that_lists_it_not_one_merely_nearby()
    {
        var vi = Write(@"Foo\Read.vi", "vi");
        var owner = Write(@"Foo\Foo.lvclass", ClassXml("../Read.vi"));
        Write(@"Foo\Other.lvclass", ClassXml("../Elsewhere.vi"));
        Write(@"Foo.lvlib", LibraryXml("Foo/Unrelated.vi"));

        Assert.Equal([owner], IconOwners.OwnersListing(vi));
        Assert.Empty(IconOwners.OwnersListing(Write(@"Foo\Loose.vi", "vi")));
    }

    // ---------------------------------------------------------------- the layer probe

    /// <summary>
    /// A minimal LabVIEW resource file holding the given blocks, in the layout the probe reads:
    /// header offsets at 16, the block table named by the info header, 20-byte section records
    /// pointing into the data area where a length precedes each section.
    /// </summary>
    internal static byte[] Rsrc(params (string Type, byte[] Data)[] blocks)
    {
        using var data = new MemoryStream();
        var offsets = new List<int>();
        foreach (var (_, bytes) in blocks)
        {
            offsets.Add((int)data.Length);
            data.Write(BigEndian(bytes.Length));
            data.Write(bytes);
        }

        const int infoOffset = 64;
        // info header (32 bytes copy of the file header + 20 bytes of which the 4th word is the table offset)
        var table = new MemoryStream();
        table.Write(BigEndian(blocks.Length - 1));
        var listStart = 4 + blocks.Length * 12;
        for (var i = 0; i < blocks.Length; i++)
        {
            table.Write(Encoding.ASCII.GetBytes(blocks[i].Type));
            table.Write(BigEndian(0));                                      // one section
            table.Write(BigEndian(listStart + i * 20));
        }
        for (var i = 0; i < blocks.Length; i++)
        {
            table.Write(BigEndian(-1)); table.Write(BigEndian(-1)); table.Write(BigEndian(0));
            table.Write(BigEndian(offsets[i])); table.Write(BigEndian(0));
        }

        const int tableAt = 32 + 20;                                       // relative to the info block
        var infoBlock = new byte[tableAt];
        BinaryPrimitives.WriteInt32BigEndian(infoBlock.AsSpan(32 + 12), tableAt);
        var info = infoBlock.Concat(table.ToArray()).ToArray();

        var dataOffset = infoOffset + info.Length;
        var file = new byte[dataOffset + data.Length];
        Encoding.ASCII.GetBytes("RSRC\r\n").CopyTo(file, 0);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(16), infoOffset);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(20), info.Length);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(24), dataOffset);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(28), (int)data.Length);
        info.CopyTo(file, infoOffset);
        data.ToArray().CopyTo(file, dataOffset);
        return file;
    }

    private static byte[] BigEndian(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        return b;
    }

    [Fact]
    public void A_layered_icon_is_seen_in_the_VITS_block()
    {
        var vits = Encoding.Latin1.GetBytes(
            "....NI_IconEditor....Layer.lvclass..NI_Library....Layer.lvclass..VI Icon....");
        var vi = Write("Layered.vi", "");
        File.WriteAllBytes(vi, Rsrc(("LVSR", [1, 2, 3]), ("VITS", vits), ("icl8", new byte[1024])));

        var probe = ViIconLayers.Read(vi);

        Assert.Null(probe.Error);
        Assert.True(probe.HasIconEditorData);
        Assert.True(probe.HasLibraryLayer);
        Assert.Equal(2, probe.LayerCount);
        Assert.Equal(vits.Length, probe.VitsBytes);
    }

    [Fact]
    public void A_flattened_icon_has_no_layer_data_and_a_non_vi_is_an_error_not_a_verdict()
    {
        var flat = Write("Flat.vi", "");
        File.WriteAllBytes(flat, Rsrc(("VITS", new byte[157])));

        var probe = ViIconLayers.Read(flat);
        Assert.False(probe.HasIconEditorData);
        Assert.Equal(0, probe.LayerCount);
        Assert.Equal(157, probe.VitsBytes);

        Assert.NotNull(ViIconLayers.Read(Write("Text.vi", "not a resource file")).Error);
    }
}
