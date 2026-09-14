using System.Text;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Emits the AIXML for a class's LUnit test methods: one round trip per field, one all-defaults
/// test and one write-all/read-all independence test.
///
/// WHY THIS EXISTS. Measured over seven builds of the same shape, this is what is left. Shipping
/// the skeletons in <c>scripts/templates/lunit/</c> took the *orientation* cost from 98 s over nine
/// turns to about 20 s over one; what remained was the model transcribing roughly 19 kB of text —
/// 60-90 s of wall clock against 1.7 s inside tools, in a single turn, and by then the largest item
/// in the whole route. Transcription is not a judgement call, so it is not a model's job.
///
/// WHAT IS DELIBERATELY NOT AUTOMATED: the VALUES. A generator that invented them would produce six
/// green tests that pin nothing, which is the one failure this route must not make easy. The caller
/// supplies a value per field and the descriptions are built from those values, so a test always
/// says what it is pinning.
///
/// THE SHIPPED TEMPLATES ARE THE SPECIFICATION, not decoration. They were lifted line-for-line from
/// six files that ran green with a negative control, and <c>LUnitScaffoldTests</c> compares this
/// emitter's output against them for a four-field class. The comparison is uid-NORMALISED: a uid is
/// an arbitrary label naming a wire, so what must match is the graph, and holding the numbers
/// identical would only pin an accident of how the first files happened to be written.
/// </summary>
internal static class LUnitScaffold
{
    /// <summary>One field of the subject class, with the sockets standing in for its accessors.</summary>
    /// <param name="Name">Field name exactly as the accessor spells it, spaces included.</param>
    /// <param name="Type">AIXML type: <c>string</c>, <c>double</c>, <c>int32</c>, <c>bool</c>, …</param>
    /// <param name="WriteStub">Placeholder VI standing in for <c>Write &lt;Name&gt;.vi</c>.</param>
    /// <param name="ReadStub">Placeholder VI standing in for <c>Read &lt;Name&gt;.vi</c>.</param>
    /// <param name="Value">The value the round trip and the independence test write.</param>
    internal sealed record Field(string Name, string Type, string WriteStub, string ReadStub,
                                 string Value);

    // A uid names a WIRE, not an object, and the only requirement is that it is unique within the
    // VI. These bands keep every element class in its own decade so no count of fields can collide -
    // the shipped templates grew organically and their independence test would have overlapped its
    // read and assert bands at six fields.
    // NUMBERED FROM AixmlCheck.SafeUidBase, not from 100 - added 2026-09-14. Every band here used
    // to sit inside LabVIEW's reserved panel-heap range, so a five-file suite over a three-field
    // class emitted 55 uids that LabVIEW logs `trying to override with non-reserved UID` for and
    // renumbers anyway. `CLAUDE.md` records TestTools.UidBase = 4200 as numbering "everything the
    // TOOLS emit"; this generator was not reached by that change, and `scripts/aixml_lint.py`
    // flagged all five files of the first real build. The offsets are unchanged, so the decade
    // independence the comment above describes is untouched.
    private const int Base = AixmlCheck.SafeUidBase;
    private const int ClassIn = Base + 100, ErrorIn = Base + 101, Seed = Base + 110;
    private const int ValueBase = Base + 200, ExpectedBase = Base + 300, DescriptionBase = Base + 400;
    private const int WriteBase = Base + 500, ReadBase = Base + 600, AssertBase = Base + 700;
    private const int ClassOut = Base + 140, ErrorOut = Base + 141;

    private const string Assertion = @"Test Case.lvclass\3APass If Equal.vim";
    private const string ErrorCluster = "cluster{bool.status,int32.code,string.source}";

    /// <summary>
    /// The default a field of this type reads before anything writes to it.
    ///
    /// THIS WAS A THREE-CASE COPY ENDING <c>_ =&gt; "0"</c> UNTIL 2026-09-14, AND IT FAILED A
    /// CORRECT CLASS. Measured on a cold build whose <c>Logger</c> carries a <c>path</c> field:
    /// the generated <c>Test Field Defaults.vi</c> asserted <c>Expected:0(Path)</c> against a
    /// class whose default is the empty path, so the suite reported a defect that did not exist -
    /// the worst shape a generator can have, because it reads as a fault in the code under test.
    /// The inconsistency was visible inside one generated file: the <c>Logger seed</c> constant,
    /// same <c>path</c> type, correctly carried <c>value=""</c>.
    ///
    /// It is the SAME catch-all <see cref="Tools.TestTools.DefaultFor"/> records being fixed on
    /// 2026-09-02, still present here because the rule had two implementations. It now has one:
    /// <c>CLAUDE.md</c>'s rule for the uid base applies to this just as well - a second
    /// implementation that disagrees is worse than either alone.
    /// </summary>
    internal static string DefaultFor(string type) => Tools.TestTools.DefaultFor(type);

    /// <summary>
    /// AIXML attribute escaping. Backslash FIRST or the escapes introduced below are re-escaped;
    /// then the two characters the format reserves - a comma separates terminals in an
    /// <c>inputs=</c> list and a colon separates a uid from its terminal - and then XML's own.
    /// A description carrying a comma is not exotic: every one this route writes has one.
    /// </summary>
    internal static string Escape(string value) => value
        .Replace("\\", @"\5C")
        .Replace(",", @"\2C")
        .Replace(":", @"\3A")
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    /// <summary>One field: write it, read it back, assert the two are equal.</summary>
    internal static string RoundTrip(string testClass, string subjectClass, Field field)
    {
        var description =
            $"Round trip over the {field.Name} accessors - Write {field.Name} stores a value and " +
            $"Read {field.Name} must return it unchanged. The value is not the type default, so a " +
            "Write that stores nothing cannot pass.";
        var assertion = $"{field.Name} must read back the value that was written";

        var sb = Head(testClass, $"Test {field.Name} Round Trip.vi", description);
        Constant(sb, Seed, $"{subjectClass} seed", "path", "");
        Constant(sb, ValueBase, field.Name, field.Type, field.Value);
        Constant(sb, ExpectedBase, "Expected", field.Type, field.Value);
        Constant(sb, DescriptionBase, "Description", "string", assertion);

        Call(sb, WriteBase, field.WriteStub,
             $"{subjectClass} in:{Seed}.value,{Esc(field.Name)}:{ValueBase}.value," +
             $"error in (no error):{ErrorIn}.value",
             $"{subjectClass} out:{WriteBase}.{subjectClass} out,error out:{WriteBase}.error out");
        Call(sb, ReadBase, field.ReadStub,
             $"{subjectClass} in:{WriteBase}.{subjectClass} out," +
             $"error in (no error):{WriteBase}.error out",
             $"{subjectClass} out:,{Esc(field.Name)}:{ReadBase}.{Esc(field.Name)}," +
             $"error out:{ReadBase}.error out");
        Assert(sb, AssertBase, ClassIn, ExpectedBase, $"{ReadBase}.{Esc(field.Name)}",
               $"{ReadBase}.error out", DescriptionBase);

        return Tail(sb, testClass, AssertBase);
    }

    /// <summary>
    /// Every field read off a FRESH class constant. All reads take the seed, deliberately: this test
    /// must see an untouched object. They are ordered only by the error chain.
    /// </summary>
    internal static string Defaults(string testClass, string subjectClass,
                                    IReadOnlyList<Field> fields)
    {
        var description =
            $"Asserts the default state of a fresh {subjectClass} class constant - every field read " +
            "straight off the class default with no Write anywhere in the chain. This pins the " +
            "documented defaults and is the one test that would catch a private data control whose " +
            "defaults had been changed by accident.";

        var sb = Head(testClass, "Test Field Defaults.vi", description);
        Constant(sb, Seed, $"{subjectClass} seed", "path", "");
        for (var i = 0; i < fields.Count; i++)
            Constant(sb, ExpectedBase + i, $"Expected {fields[i].Name}", fields[i].Type,
                     DefaultFor(fields[i].Type));
        for (var i = 0; i < fields.Count; i++)
            Constant(sb, DescriptionBase + i, $"Description {fields[i].Name}", "string",
                     $"Default {fields[i].Name} of a fresh {subjectClass} must be " +
                     Spoken(fields[i].Type));

        for (var i = 0; i < fields.Count; i++)
            Call(sb, ReadBase + i, fields[i].ReadStub,
                 $"{subjectClass} in:{Seed}.value," +
                 $"error in (no error):{(i == 0 ? $"{ErrorIn}.value" : $"{ReadBase + i - 1}.error out")}",
                 $"{subjectClass} out:,{Esc(fields[i].Name)}:{ReadBase + i}.{Esc(fields[i].Name)}," +
                 $"error out:{ReadBase + i}.error out");

        Chain(sb, fields, ReadBase, $"{ReadBase + fields.Count - 1}.error out");
        return Tail(sb, testClass, AssertBase + fields.Count - 1);
    }

    /// <summary>
    /// Write every field on ONE object, then read every field back off THAT object. Catches a Write
    /// that also stores into a field it does not own - which no single-field round trip can, because
    /// the other field is never read after the write.
    /// </summary>
    internal static string Independence(string testClass, string subjectClass,
                                        IReadOnlyList<Field> fields)
    {
        var names = string.Join(" then ", fields.Select(f => f.Name));
        var description =
            $"Writes all {fields.Count} fields on one object in the order {names} and then reads " +
            "them all back. This catches a fault the per-field round trips cannot - a Write " +
            "accessor that also stores into a field it does not own passes every single-field test " +
            "because that other field is never read after the write. It detects any Write that " +
            "disturbs a field written EARLIER in the chain; one that disturbs a LATER field is " +
            "masked because the later Write repairs it.";

        // A FIELD WHOSE LITERAL CANNOT SURVIVE IS WRITTEN AND ASSERTED AT ITS DEFAULT, not at the
        // value the caller supplied. Authoring the supplied value would put a constant on the
        // diagram that ConvertAIXMLToVI silently empties on BOTH sides, so the assertion would read
        // `must still read the value it was given` over a value nothing ever gave it - a true
        // result reached through a false sentence, and two lint warnings on the generated file.
        //
        // THE ASSERTION IS KEPT rather than dropped, which is the opposite call from the round
        // trip, and the difference is what each one claims. A round trip claims `Write stored it and
        // Read returned it`, which a no-op Write satisfies at the default - nothing is left to
        // assert. This test claims `no OTHER Write disturbed this field`, and that is still caught:
        // a Write storing into a field it does not own puts a non-default value here and fails.
        var sb = Head(testClass, "Test Write Independence.vi", description);
        Constant(sb, Seed, $"{subjectClass} seed", "path", "");
        for (var i = 0; i < fields.Count; i++)
            Constant(sb, ValueBase + i, fields[i].Name, fields[i].Type, Authorable(fields[i]));
        for (var i = 0; i < fields.Count; i++)
            Constant(sb, ExpectedBase + i, $"Expected {fields[i].Name}", fields[i].Type,
                     Authorable(fields[i]));
        for (var i = 0; i < fields.Count; i++)
            Constant(sb, DescriptionBase + i, $"Description {fields[i].Name}", "string",
                     AixmlCheck.DiscardsNonEmptyValue(fields[i].Type)
                         ? $"After all {fields.Count} fields were written {fields[i].Name} must " +
                           $"still be {Spoken(fields[i].Type)} - a {fields[i].Type.Trim()} literal " +
                           "cannot be authored in AIXML, so this pins that no other Write stored " +
                           "into it rather than that this one round-tripped"
                         : $"After all {fields.Count} fields were written {fields[i].Name} must " +
                           "still read the value it was given");

        for (var i = 0; i < fields.Count; i++)
            Call(sb, WriteBase + i, fields[i].WriteStub,
                 $"{subjectClass} in:" +
                 (i == 0 ? $"{Seed}.value" : $"{WriteBase + i - 1}.{subjectClass} out") +
                 $",{Esc(fields[i].Name)}:{ValueBase + i}.value," +
                 $"error in (no error):" +
                 (i == 0 ? $"{ErrorIn}.value" : $"{WriteBase + i - 1}.error out"),
                 $"{subjectClass} out:{WriteBase + i}.{subjectClass} out," +
                 $"error out:{WriteBase + i}.error out");

        var written = $"{WriteBase + fields.Count - 1}.{subjectClass} out";
        for (var i = 0; i < fields.Count; i++)
            Call(sb, ReadBase + i, fields[i].ReadStub,
                 $"{subjectClass} in:{written},error in (no error):" +
                 (i == 0 ? $"{WriteBase + fields.Count - 1}.error out"
                         : $"{ReadBase + i - 1}.error out"),
                 $"{subjectClass} out:,{Esc(fields[i].Name)}:{ReadBase + i}.{Esc(fields[i].Name)}," +
                 $"error out:{ReadBase + i}.error out");

        Chain(sb, fields, ReadBase, $"{ReadBase + fields.Count - 1}.error out");
        return Tail(sb, testClass, AssertBase + fields.Count - 1);
    }

    /// <summary>The assertion chain: one per field, threaded through the test case object.</summary>
    private static void Chain(StringBuilder sb, IReadOnlyList<Field> fields, int readBase,
                              string firstError)
    {
        for (var i = 0; i < fields.Count; i++)
            Assert(sb, AssertBase + i,
                   objectFrom: i == 0 ? ClassIn : AssertBase + i - 1,
                   expected: ExpectedBase + i,
                   actual: $"{readBase + i}.{Esc(fields[i].Name)}",
                   error: i == 0 ? firstError : $"{AssertBase + i - 1}.error out",
                   description: DescriptionBase + i);
    }

    /// <summary>
    /// Whether an independence test over these fields asserts anything at all.
    ///
    /// ONE FIELD IS THE DEGENERATE CASE. That test's whole claim is "no OTHER Write disturbed this
    /// field", and with a single field there is no other Write - so it writes a value, reads it
    /// back and asserts it, which is what the round trip beside it already says, word for word.
    /// When that single field's literal is DISCARDED as well it is worse than redundant: both
    /// sides become the default and the assertion compares the default with the default, while the
    /// generated description still promises that no other Write stored into it.
    ///
    /// MEASURED 2026-09-15 on a class whose only field is a `timestamp`: two green tests, one
    /// assertion each, pinning nothing, and every cheap checker answering `[clean]` because
    /// nothing in the document is malformed. The cut is on the COUNT rather than on the type,
    /// because the redundancy is there for an ordinary single field too.
    /// </summary>
    internal static bool IndependenceAssertsSomething(IReadOnlyList<Field> fields) =>
        fields.Count >= 2;

    /// <summary>
    /// The literal to put on the diagram for this field: the caller's value, unless the type
    /// discards a non-empty one - see AixmlCheck.DiscardsNonEmptyValue for the measured table -
    /// in which case the only literal AIXML can carry is the default, and authoring anything else
    /// produces a constant LabVIEW empties behind your back.
    /// </summary>
    private static string Authorable(Field field) =>
        AixmlCheck.DiscardsNonEmptyValue(field.Type) ? DefaultFor(field.Type) : field.Value;

    /// <summary>
    /// How the default reads in a test's own DESCRIPTION. Derived from the literal rather than
    /// listed separately, because the two drifting apart is how `must be 0` came to describe a
    /// path field - the assertion and the sentence explaining it were computed by different tables.
    /// </summary>
    private static string Spoken(string type)
    {
        var literal = DefaultFor(type);
        if (literal.Length > 0) return literal;

        var t = type.Trim();
        if (t.StartsWith("string", StringComparison.Ordinal)) return "the empty string";
        if (t.StartsWith("path", StringComparison.Ordinal)) return "the empty path";
        return "empty";
    }

    private static string Esc(string value) => Escape(value);

    private static StringBuilder Head(string testClass, string viName, string description)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<VI _name=\"{Escape(viName)}\" description=\"{Escape(description)}\">");
        sb.AppendLine($"  <Control _name=\"{Escape(testClass)} In\" conIdx=\"11\" " +
                      $"connection=\"required\" outputs=\"value:{ClassIn}.value\" type=\"path\" " +
                      $"uid=\"{ClassIn}\" uid_parent=\"root\" value=\"\"/>");
        sb.AppendLine($"  <Control _name=\"error in (no error)\" conIdx=\"8\" " +
                      $"connection=\"recommended\" outputs=\"value:{ErrorIn}.value\" " +
                      $"type=\"{ErrorCluster}\" uid=\"{ErrorIn}\" uid_parent=\"root\" " +
                      "value=\"[false,0,]\"/>");
        return sb;
    }

    private static void Constant(StringBuilder sb, int uid, string name, string type, string value)
        => sb.AppendLine($"  <Constant _name=\"{Escape(name)}\" outputs=\"value:{uid}.value\" " +
                         $"type=\"{type}\" uid=\"{uid}\" uid_parent=\"root\" " +
                         $"value=\"{Escape(value)}\"/>");

    private static void Call(StringBuilder sb, int uid, string target, string inputs, string outputs)
        => sb.AppendLine($"  <Call target=\"{Escape(target)}\" inputs=\"{inputs}\" " +
                         $"outputs=\"{outputs}\" uid=\"{uid}\" uid_parent=\"root\"/>");

    private static void Assert(StringBuilder sb, int uid, int objectFrom, int expected,
                               string actual, string error, int description)
    {
        var from = objectFrom == ClassIn
            ? $"{ClassIn}.value"
            : $"{objectFrom}.LUnit Test Case Out";
        sb.AppendLine($"  <Call target=\"{Assertion}\" inputs=\"LUnit Test Case In:{from}," +
                      $"Expected:{expected}.value,Actual:{actual},error in (no error):{error}," +
                      $"Description:{description}.value\" outputs=\"LUnit Test Case Out:{uid}." +
                      $"LUnit Test Case Out,error out:{uid}.error out\" uid=\"{uid}\" " +
                      "uid_parent=\"root\"/>");
    }

    private static string Tail(StringBuilder sb, string testClass, int lastAssert)
    {
        sb.AppendLine($"  <Indicator _name=\"{Escape(testClass)} Out\" conIdx=\"3\" " +
                      $"connection=\"recommended\" inputs=\"value:{lastAssert}.LUnit Test Case " +
                      $"Out\" type=\"path\" uid=\"{ClassOut}\" uid_parent=\"root\" value=\"\"/>");
        sb.AppendLine($"  <Indicator _name=\"error out\" conIdx=\"0\" connection=\"recommended\" " +
                      $"inputs=\"value:{lastAssert}.error out\" type=\"{ErrorCluster}\" " +
                      $"uid=\"{ErrorOut}\" uid_parent=\"root\" value=\"[false,0,]\"/>");
        sb.AppendLine("</VI>");
        return sb.ToString();
    }
}
