using System.Text;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The offline half of class creation: the flattened-string codec, the private data wrapper, the
/// field grammar, the document and the reader.
///
/// THE WRAPPER TESTS ARE THE ONES THAT EARN THEIR KEEP. Measured 2026-08-26: a private data blob
/// whose u32 length field sat two bytes late passed pylv_rebuild, passed its own encode/decode round
/// trip, and produced a class LabVIEW would not load - answering with three class entries whose
/// every field was blank and an error message about invalid *paths* from inside NI's own
/// Get library info.vi. Nothing pointed at the blob, and finding it cost most of an afternoon. The
/// offset is pinned here so it cannot move again in silence.
///
/// Nothing in this file needs LabVIEW or the pylabview bundle.
/// </summary>
public class LvClassTests : IDisposable
{
    private readonly string _tree;

    public LvClassTests()
    {
        _tree = Path.Combine(Path.GetTempPath(), "lvclass-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tree);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tree, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- the codec

    [Theory]
    [InlineData(new byte[] { 0x00 }, "!!")]
    [InlineData(new byte[] { 0xFF }, "`Q")]
    public void EncodePinsSixBitsPerCharacterAtOffset0x21(byte[] input, string expected) =>
        Assert.Equal(expected, LvClass.Encode(input));

    /// <summary>
    /// Every length modulo 3, because the padding of the final group is where a codec goes wrong -
    /// and a stray trailing byte here becomes a control LabVIEW cannot parse.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(97)]
    [InlineData(4627)]
    public void CodecRoundTripsAnyLength(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(i * 7 + 13);

        Assert.Equal(data, LvClass.Decode(LvClass.Encode(data)));
    }

    [Fact]
    public void DecodeIgnoresWhitespaceSoAWrappedPropertyStillReads()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var wrapped = string.Join(
            Environment.NewLine, LvClass.Encode(data).Chunk(4).Select(c => new string(c)));

        Assert.Equal(data, LvClass.Decode(wrapped));
    }

    // ---------------------------------------------------------------- the private data wrapper

    [Fact]
    public void WrapPutsTheControlLengthAtOffset29AsBigEndianU32()
    {
        var ctl = new byte[300];
        var blob = LvClass.DecodeProperty(LvClass.Wrap(ctl));

        var length = (blob[LvClass.LengthFieldOffset] << 24) |
                     (blob[LvClass.LengthFieldOffset + 1] << 16) |
                     (blob[LvClass.LengthFieldOffset + 2] << 8) |
                     blob[LvClass.LengthFieldOffset + 3];

        Assert.Equal(300, length);

        // 29 header + 4 length + the control + 4 trailing zeros, and nothing else.
        Assert.Equal(29 + 4 + 300 + 4, blob.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, blob[^4..]);
    }

    [Fact]
    public void WrapKeepsTheLabViewVersionAtTheFrontOfTheHeader()
    {
        var blob = LvClass.DecodeProperty(LvClass.Wrap(new byte[8]));

        // LVVersion 26008000, the only four bytes of that header anyone has decoded.
        Assert.Equal(new byte[] { 0x26, 0x00, 0x80, 0x00 }, blob[..4]);
    }

    [Fact]
    public void UnwrapReturnsExactlyTheControlAndNotTheTrailingZeros()
    {
        var ctl = new byte[512];
        for (var i = 0; i < ctl.Length; i++) ctl[i] = (byte)(i % 251);

        Assert.Equal(ctl, LvClass.Unwrap(LvClass.Wrap(ctl)));
    }

    [Fact]
    public void UnwrapRefusesABlobTooShortToCarryALengthField()
    {
        var truncated = LvClass.Encode(new byte[10]);

        var problem = Assert.Throws<InvalidDataException>(() => LvClass.Unwrap(truncated));
        Assert.Contains("too short", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The 2026-08-26 failure, reproduced: shift the header by two bytes and the length field reads
    /// a number the payload cannot satisfy. Before this check the symptom was LabVIEW's, three
    /// steps later, and it named paths.
    /// </summary>
    [Fact]
    public void UnwrapRefusesALengthFieldThePayloadCannotSatisfy()
    {
        var good = LvClass.DecodeProperty(LvClass.Wrap(new byte[64]));
        var shifted = new byte[good.Length];
        good.AsSpan(0, good.Length - 2).CopyTo(shifted.AsSpan(2));

        var problem = Assert.Throws<InvalidDataException>(
            () => LvClass.Unwrap(LvClass.Encode(shifted)));
        Assert.Contains("length field", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- the field grammar

    [Fact]
    public void ParseFieldsTakesTypeDotNameAndKeepsSpacesInNames()
    {
        var fields = LvClass.ParseFields(
            "string.Manufacturer, int32.Year Of Manufacture ,bool.Has Halo");

        Assert.Collection(fields,
            f => { Assert.Equal("string", f.Type); Assert.Equal("Manufacturer", f.Name); },
            f => { Assert.Equal("int32", f.Type); Assert.Equal("Year Of Manufacture", f.Name); },
            f => { Assert.Equal("bool", f.Type); Assert.Equal("Has Halo", f.Name); });
    }

    [Fact]
    public void ParseFieldsAcceptsNothingAsEmptyPrivateData()
    {
        Assert.Empty(LvClass.ParseFields(null));
        Assert.Empty(LvClass.ParseFields("   "));
    }

    /// <summary>
    /// An unknown type is refused rather than given a guessed `value` literal. AIXML requires the
    /// literal and a wrong one generates without complaint - aixml-reference.md section 2 records
    /// `TRUE` generating cleanly and running as false.
    /// </summary>
    [Fact]
    public void ParseFieldsRefusesATypeItHasNoLiteralFor()
    {
        var problem = Assert.Throws<ArgumentException>(
            () => LvClass.ParseFields("cluster{double.X}.Position"));
        Assert.Contains("types it knows", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Manufacturer", "is not a field")]
    [InlineData("string.", "is not a field")]
    [InlineData(".Manufacturer", "is not a field")]
    [InlineData("string.A,string.A", "appears twice")]
    public void ParseFieldsRefusesAMalformedSpec(string spec, string expected)
    {
        var problem = Assert.Throws<ArgumentException>(() => LvClass.ParseFields(spec));
        Assert.Contains(expected, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCarrierIsOneControlPerField_NotACluster()
    {
        // NI's Add Member Data to Private Data Control.vi takes front-panel CONTROL REFERENCES,
        // one per field, so the fields travel as controls on a throwaway VI. The cluster this
        // used to author went with the route that converted a VI into a control by flipping
        // flags - that produced a class LabVIEW reported and refused to compile.
        var aixml = LvClass.CarrierAixml("Auto",
            LvClass.ParseFields("timestamp.Built,string.Make,double.Top Speed"));

        Assert.Equal(3, aixml.Split("<Control ").Length - 1);
        Assert.Contains("_name=\"Built\" outputs=\"value:\" type=\"timestamp\" ", aixml, StringComparison.Ordinal);
        Assert.Contains("_name=\"Top Speed\"", aixml, StringComparison.Ordinal);
        Assert.DoesNotContain("cluster{", aixml, StringComparison.Ordinal);
        Assert.DoesNotContain("conIdx", aixml, StringComparison.Ordinal);
    }

    [Fact]
    public void ATimestampFieldIsAcceptedAndCarriesTheEmptyLiteral()
    {
        // Left out of the literal table at first, which refused `timestamp.Timestamp` with "no
        // default literal for" - a message that reads as though AIXML could not express a
        // timestamp at all. It can: 20 cached exports write `type="timestamp" ... value=""`,
        // controls and constants alike, the same empty literal a string uses.
        var aixml = LvClass.CarrierAixml("X", LvClass.ParseFields("timestamp.When,double.How Far"));

        Assert.Contains("type=\"timestamp\" uid=\"10\" uid_parent=\"root\" value=\"\"", aixml, StringComparison.Ordinal);
        Assert.Contains("type=\"double\" uid=\"11\" uid_parent=\"root\" value=\"0\"", aixml, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the document

    [Fact]
    public void DocumentWithNoParentCarriesNoParentLibrariesItem()
    {
        var text = LvClass.Document("Auto", LvClass.Wrap(new byte[16]), null, null);

        Assert.DoesNotContain("Parent Libraries", text, StringComparison.Ordinal);
        Assert.Contains("<Item Name=\"Auto.ctl\" Type=\"Class Private Data\" URL=\"Auto.ctl\">",
                        text, StringComparison.Ordinal);
        Assert.Contains("NI.LibItem.Scope\" Type=\"Int\">2<", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentWithAParentNamesItQualifiedAndByRelativeUrl()
    {
        var text = LvClass.Document("Bus", LvClass.Wrap(new byte[16]),
                                    "Fleet.lvlib:Auto.lvclass", "../Auto/Auto.lvclass");

        Assert.Contains(
            "<Item Name=\"Fleet.lvlib:Auto.lvclass\" Type=\"Parent\" URL=\"../Auto/Auto.lvclass\"/>",
            text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- reading one back

    [Fact]
    public void ReadReportsThePlainTextAncestorChainNearestFirst()
    {
        var path = WriteClass("Bus", """
                <Item Name="Parent Libraries" Type="Parent Libraries">
                    <Item Name="Auto.lvclass" Type="Parent" URL="../Auto/Auto.lvclass"/>
                    <Item Name="Vehicle.lvclass" Type="Parent" URL="../Vehicle/Vehicle.lvclass"/>
                </Item>
            """);

        var info = LvClass.Read(path);

        Assert.Equal(["Auto.lvclass", "Vehicle.lvclass"], info.Ancestors);
        Assert.Contains("plain text", info.AncestorSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// The older representation. The payload is built up byte by byte rather than written as a
    /// string literal: a PTH0 record separates its components with length prefixes, and raw control
    /// bytes in C# source make the file unreadable to the compiler.
    /// </summary>
    [Fact]
    public void ReadFallsBackToTheEncodedParentLinkForOlderFiles()
    {
        var decoded = new List<byte> { 0x0D };
        decoded.AddRange(Encoding.Latin1.GetBytes("BaseLib.lvlib"));
        decoded.Add(0x15);
        decoded.AddRange(Encoding.Latin1.GetBytes("Message Queue.lvclass"));
        decoded.AddRange(Encoding.Latin1.GetBytes("PTH0"));

        // Escaped the way a real file has to escape it: the codec emits characters XML reserves.
        var encoded = LvClass.Encode([.. decoded])
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

        var path = WriteClass("Derived",
            $"""    <Property Name="NI.LVClass.ParentClassLinkInfo" Type="Bin">{encoded}</Property>""");

        var info = LvClass.Read(path);

        // The LAST .lvclass in the record is the parent; the .lvlib before it is the owning
        // library and must not be reported as an ancestor.
        Assert.Equal(["Message Queue.lvclass"], info.Ancestors);
        Assert.Contains("ParentClassLinkInfo", info.AncestorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BaseLib.lvlib", info.Ancestors);
    }

    /// <summary>
    /// Measured on JKI's Lexer.lvclass, which reported `inheritsFrom "JKI JSON
    /// Serialization.lvlib"` until 2026-08-26 - a library, which cannot be a parent. A record that
    /// names no class is not an ancestry answer.
    /// </summary>
    [Fact]
    public void ReadWillNotReportALibraryAsAParentClass()
    {
        var encoded = LvClass.Encode(Encoding.Latin1.GetBytes("JKI JSON Serialization.lvlibPTH0"))
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

        var path = WriteClass("Lonely",
            $"""    <Property Name="NI.LVClass.ParentClassLinkInfo" Type="Bin">{encoded}</Property>""");

        var info = LvClass.Read(path);

        Assert.Empty(info.Ancestors);
        Assert.Contains("named no .lvclass", info.AncestorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSaysLabViewObjectWhenNoParentIsRecorded()
    {
        var info = LvClass.Read(WriteClass("Auto", ""));

        Assert.Empty(info.Ancestors);
        Assert.Contains("LabVIEW Object", info.AncestorSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scope comes from the property, never the folder name. The census found folders called
    /// `private` whose twelve members were all effectively public.
    /// </summary>
    [Fact]
    public void ReadTakesScopeFromThePropertyAndNotFromTheFolderName()
    {
        var path = WriteClass("Auto", """
                <Item Name="private" Type="Folder">
                    <Item Name="Get Make.vi" Type="VI" URL="../Get Make.vi">
                        <Property Name="NI.ClassItem.MethodScope" Type="UInt">1</Property>
                        <Property Name="NI.ClassItem.IsStaticMethod" Type="Bool">false</Property>
                    </Item>
                    <Item Name="Helper.vi" Type="VI" URL="../Helper.vi">
                        <Property Name="NI.ClassItem.MethodScope" Type="UInt">2</Property>
                        <Property Name="NI.ClassItem.IsStaticMethod" Type="Bool">true</Property>
                    </Item>
                </Item>
            """);

        var info = LvClass.Read(path);

        Assert.Collection(info.Members,
            m =>
            {
                Assert.Equal("Get Make.vi", m.Name);
                Assert.Equal("public", m.Scope);
                Assert.True(m.DynamicDispatch);
            },
            m =>
            {
                Assert.Equal("Helper.vi", m.Name);
                Assert.Equal("private", m.Scope);
                Assert.False(m.DynamicDispatch);
            });
    }

    [Fact]
    public void ReadCountsAControlMemberAsAControlNotAVi()
    {
        var path = WriteClass("Auto", """
                <Item Name="Mode.ctl" Type="VI" URL="../Mode.ctl">
                    <Property Name="NI.ClassItem.MethodScope" Type="UInt">1</Property>
                </Item>
            """);

        Assert.Equal("control", Assert.Single(LvClass.Read(path).Members).Type);
    }

    [Fact]
    public void ReadReportsPrivateDataBytesAndFlagsABlobThatDoesNotDecode()
    {
        var good = LvClass.Read(WriteClass("Auto", "", LvClass.Wrap(new byte[321])));
        Assert.Equal(321, good.PrivateDataBytes);

        // -1 is the state that makes LabVIEW report the class with every field blank.
        var bad = LvClass.Read(WriteClass("Broken", "", LvClass.Encode(new byte[12])));
        Assert.Equal(-1, bad.PrivateDataBytes);
    }

    [Fact]
    public void ReadRefusesALibraryOrProjectFile()
    {
        var path = Path.Combine(_tree, "Fleet.lvlib");
        File.WriteAllText(path, "<?xml version='1.0'?><Library LVVersion=\"26008000\"/>");

        var problem = Assert.Throws<InvalidDataException>(() => LvClass.Read(path));
        Assert.Contains("not <LVClass>", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QualifiedNameUsesTheOwningLibraryWhenTheParentHasOne()
    {
        Assert.Equal("Auto.lvclass", LvClass.QualifiedName(WriteClass("Auto", "")));

        var owned = WriteClass("Owned", "", extraProperties:
            """    <Property Name="NI.Lib.ContainingLib" Type="Str">Fleet.lvlib</Property>""");
        Assert.Equal("Fleet.lvlib:Owned.lvclass", LvClass.QualifiedName(owned));
    }

    // ---------------------------------------------------------------- the project entry

    [Fact]
    public void AddToProjectPutsTheClassBeforeDependenciesAndRefusesADuplicate()
    {
        var projectPath = Path.Combine(_tree, "Fleet.lvproj");
        File.WriteAllText(projectPath, LvClass.Project([("Auto.lvclass", "../Auto/Auto.lvclass")]));

        Assert.True(LvClass.AddToProject(projectPath, "Bus", "../Bus/Bus.lvclass"));

        var text = File.ReadAllText(projectPath);
        Assert.True(text.IndexOf("Bus.lvclass", StringComparison.Ordinal) <
                    text.IndexOf("Dependencies", StringComparison.Ordinal),
            "the class must land among the content items, not after Dependencies");

        // Listing it twice makes LabVIEW report a conflict, which reads as a broken class.
        Assert.False(LvClass.AddToProject(projectPath, "Bus", "../Bus/Bus.lvclass"));
    }

    /// <summary>
    /// The second class written into a project must not reformat what the first one wrote.
    /// `XDocument.Save` did exactly that: a BOM appeared, the declaration's single quotes became
    /// double, tabs became two spaces and every self-closing tag gained a space - which is the
    /// noise `lvproj-structure.md` section 8 exists to prevent.
    /// </summary>
    [Fact]
    public void AddToProjectPreservesTheFilesOwnFormatting()
    {
        var projectPath = Path.Combine(_tree, "Style.lvproj");
        var before = LvClass.Project([("Auto.lvclass", "../Auto/Auto.lvclass")]);
        File.WriteAllText(projectPath, before);

        Assert.True(LvClass.AddToProject(projectPath, "Bus", "../Bus/Bus.lvclass"));

        var after = File.ReadAllText(projectPath);
        var added = $"\t\t<Item Name=\"Bus.lvclass\" Type=\"LVClass\" URL=\"../Bus/Bus.lvclass\"/>\r\n";

        // Byte-for-byte the original, plus exactly one line.
        Assert.Equal(before.Replace("\t\t<Item Name=\"Dependencies\"",
                                    added + "\t\t<Item Name=\"Dependencies\"",
                                    StringComparison.Ordinal), after);

        Assert.DoesNotContain('﻿', after);
        Assert.Contains("<?xml version='1.0' encoding='UTF-8'?>", after, StringComparison.Ordinal);
        Assert.DoesNotContain(" />", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// A LabVIEW-saved project has a Dependencies item WITH CHILDREN, so it is not self-closing.
    /// Anchoring on the whole element rather than on the Type attribute would miss it.
    /// </summary>
    [Fact]
    public void AddToProjectFindsADependenciesItemThatHasChildren()
    {
        var projectPath = Path.Combine(_tree, "Saved.lvproj");
        File.WriteAllText(projectPath,
            "<?xml version='1.0' encoding='UTF-8'?>\r\n" +
            "<Project Type=\"Project\" LVVersion=\"26008000\">\r\n" +
            "\t<Item Name=\"My Computer\" Type=\"My Computer\">\r\n" +
            "\t\t<Item Name=\"Dependencies\" Type=\"Dependencies\">\r\n" +
            "\t\t\t<Item Name=\"vi.lib\" Type=\"Folder\"/>\r\n" +
            "\t\t</Item>\r\n" +
            "\t\t<Item Name=\"Build Specifications\" Type=\"Build\"/>\r\n" +
            "\t</Item>\r\n" +
            "</Project>\r\n");

        Assert.True(LvClass.AddToProject(projectPath, "Bus", "../Bus/Bus.lvclass"));

        var lines = File.ReadAllLines(projectPath);
        var added = Array.FindIndex(lines, l => l.Contains("Bus.lvclass", StringComparison.Ordinal));
        var dependencies = Array.FindIndex(
            lines, l => l.Contains("Type=\"Dependencies\"", StringComparison.Ordinal));

        Assert.True(added >= 0 && added < dependencies);
    }

    [Fact]
    public void AddToProjectRefusesAProjectWithNoAnchorRatherThanGuessing()
    {
        var projectPath = Path.Combine(_tree, "Odd.lvproj");
        File.WriteAllText(projectPath,
            "<?xml version='1.0' encoding='UTF-8'?>\r\n" +
            "<Project Type=\"Project\" LVVersion=\"26008000\">\r\n" +
            "\t<Item Name=\"My Computer\" Type=\"My Computer\"/>\r\n" +
            "</Project>\r\n");

        var problem = Assert.Throws<InvalidDataException>(
            () => LvClass.AddToProject(projectPath, "Bus", "../Bus/Bus.lvclass"));
        Assert.Contains("no anchor", problem.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// `../` pops the referencing FILE NAME, not a directory - so a class beside its project is
    /// `../Auto/Auto.lvclass` and a parent one folder over is `../../Auto/Auto.lvclass`. NI's own
    /// Circle Message.lvclass carries exactly the second shape.
    ///
    /// THIS SHIPPED WRONG: the first version took a directory and emitted `Auto/Auto.lvclass`,
    /// which resolves inside the .lvproj treated as a folder. LabVIEW finds the class anyway, so
    /// the load check passed and only a human reading the file noticed.
    /// </summary>
    [Fact]
    public void RelativeUrlFromAProjectFileReachesASiblingFolder()
    {
        var url = LvClass.RelativeUrl(
            Path.Combine(_tree, "Fleet.lvproj"), Path.Combine(_tree, "Auto", "Auto.lvclass"));

        Assert.Equal("../Auto/Auto.lvclass", url);
    }

    [Fact]
    public void RelativeUrlFromAClassFileToAParentNeedsTwoLevels()
    {
        var url = LvClass.RelativeUrl(
            Path.Combine(_tree, "Bus", "Bus.lvclass"),
            Path.Combine(_tree, "Auto", "Auto.lvclass"));

        Assert.Equal("../../Auto/Auto.lvclass", url);
    }

    [Fact]
    public void RelativeUrlNeverProducesADriveLetterOrABackslash()
    {
        // Checklist item 3 of lvproj-structure.md section 9: not one absolute path in 29 194 URLs.
        var url = LvClass.RelativeUrl(
            Path.Combine(_tree, "deep", "Fleet.lvproj"),
            Path.Combine(_tree, "deep", "sub", "folder", "Auto.lvclass"));

        Assert.Equal("../sub/folder/Auto.lvclass", url);
        Assert.DoesNotContain('\\', url);
        Assert.DoesNotContain(':', url);
    }

    /// <summary>
    /// A project LabVIEW has to complete is a project LabVIEW marks dirty on open. The first
    /// version of this generator wrote `server.tcp.enabled` alone.
    /// </summary>
    [Fact]
    public void ProjectCarriesTheDocumentedCorePropertySetAheadOfItsItems()
    {
        var text = LvClass.Project([("Auto.lvclass", "../Auto/Auto.lvclass")]);

        foreach (var name in new[]
                 {
                     "NI.LV.All.SourceOnly", "NI.Project.Description",
                     "server.app.propertiesEnabled", "server.control.propertiesEnabled",
                     "server.tcp.enabled", "server.tcp.port", "server.tcp.serviceName",
                     "server.tcp.serviceName.default", "server.vi.callsEnabled",
                     "server.vi.propertiesEnabled", "specify.custom.address",
                 })
            Assert.Contains($"Name=\"{name}\"", text, StringComparison.Ordinal);

        Assert.True(
            text.LastIndexOf("</Property>", StringComparison.Ordinal) <
            text.IndexOf("Type=\"LVClass\"", StringComparison.Ordinal),
            "properties come ahead of items");
    }

    // ---------------------------------------------------------------- helpers

    private string WriteClass(string className, string items, string? blob = null,
                              string? extraProperties = null)
    {
        var directory = Path.Combine(_tree, className);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{className}.lvclass");

        var text = new StringBuilder();
        text.AppendLine("<?xml version='1.0' encoding='UTF-8'?>");
        text.AppendLine("<LVClass LVVersion=\"26008000\">");
        if (extraProperties is not null) text.AppendLine(extraProperties);
        if (blob is not null)
            text.AppendLine("\t<Property Name=\"NI.LVClass.FlattenedPrivateDataCTL\" Type=\"Bin\">" +
                            blob + "</Property>");
        if (items.Length > 0) text.AppendLine(items);
        text.AppendLine($"\t<Item Name=\"{className}.ctl\" Type=\"Class Private Data\" " +
                        $"URL=\"{className}.ctl\">");
        text.AppendLine("\t\t<Property Name=\"NI.LibItem.Scope\" Type=\"Int\">2</Property>");
        text.AppendLine("\t</Item>");
        text.AppendLine("</LVClass>");

        File.WriteAllText(path, text.ToString());
        return path;
    }
    /// <summary>
    /// THE REGRESSION, measured 2026-08-31 on the Hund class. `lvai_create_accessors` resumed from
    /// the MEMBER COUNT divided by two, which assumed every member is half an accessor pair. A class
    /// carrying two interface overrides therefore resumed at field 1 and `Name` silently got no
    /// accessors, while the answer reported `membersAfter: 8` and looked complete.
    /// </summary>
    [Fact]
    public void FieldsWithAccessorsIgnoresMembersThatAreNotAccessors()
    {
        // The exact shape that broke it: two overrides and no accessors at all.
        List<LvClass.Member> justOverrides =
        [
            new("Get Name.vi", "Hund/Get Name.vi", "vi", "public", true),
            new("Lautgebung.vi", "Hund/Lautgebung.vi", "vi", "public", true),
        ];

        Assert.Equal(0, LvClass.FieldsWithAccessors(justOverrides, 2));
        Assert.Equal(2, justOverrides.Count);   // the old rule read this as "resume at field 1"
    }

    [Fact]
    public void FieldsWithAccessorsCountsPairsAndIgnoresMethodsBesideThem()
    {
        List<LvClass.Member> mixed =
        [
            new("Get Name.vi", "Hund/Get Name.vi", "vi", "public", true),
            new("Lautgebung.vi", "Hund/Lautgebung.vi", "vi", "public", true),
            new("Read Name.vi", "Read Name.vi", "vi", "public", true),
            new("Write Name.vi", "Write Name.vi", "vi", "public", true),
            new("Read Rasse.vi", "Read Rasse.vi", "vi", "public", true),
            new("Write Rasse.vi", "Write Rasse.vi", "vi", "public", true),
        ];

        Assert.Equal(2, LvClass.FieldsWithAccessors(mixed, 2));   // two fields done, not three
        Assert.Equal(2, LvClass.FieldsWithAccessors(mixed, 0));   // Read only
        Assert.Equal(2, LvClass.FieldsWithAccessors(mixed, 1));   // Write only
    }

    [Fact]
    public void FieldsWithAccessorsIgnoresAControlMember()
    {
        // The private data control is a member too, and it is not an accessor.
        List<LvClass.Member> withControl =
        [
            new("Hund.ctl", "Hund.ctl", "control", "public", null),
            new("Read Name.vi", "Read Name.vi", "vi", "public", true),
            new("Write Name.vi", "Write Name.vi", "vi", "public", true),
        ];

        Assert.Equal(1, LvClass.FieldsWithAccessors(withControl, 2));
    }

}
