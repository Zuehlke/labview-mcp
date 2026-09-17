using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Telling a method's own <c>path</c> PARAMETER apart from a class stand-in.
///
/// THE DEFECT THIS CLOSES. <c>lvai_add_class_method</c>'s on-disk verify counts every
/// <c>class="stdPath"</c> object in the front-panel heap and required ZERO of them, on the
/// assumption that the only reason a method carries a <c>path</c> terminal is that AIXML refuses a
/// class-typed one. That is usually true and not always: measured 2026-09-17 on <c>Drucken.vi</c>,
/// whose payload is a file path plus two others, the call answered <c>ok: false</c> with
/// <c>pathStandInsLeft: 1</c> while both class terminals were correctly retyped, the member was
/// added, the class was saved and the VI read <c>execState 1</c> afterwards. The sibling method
/// with no path payload answered 0 in the same run, which is the control arm.
///
/// AND IT HAD BEEN SEEN ONCE BEFORE. <c>VerifyFailureDetail</c>'s own comment records the same
/// shape from 2026-09-07 - "a method asking to retype one of three <c>path</c> stand-ins, so two
/// were legitimately left" - and the remedy then was only to stop that branch throwing. A remedy
/// written into a comment is not a fix; this repository already records that lesson for
/// <c>nodesSwapped</c> and for <c>dwarnCount</c>.
/// </summary>
public class ClassMethodPathTerminalTests
{
    private sealed class Doc : IDisposable
    {
        public string Root { get; }

        public Doc()
        {
            Root = Directory.CreateTempSubdirectory("path-terminals").FullName;
        }

        public string Write(string name, string xml)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, xml);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static List<ClassMethodTools.ClassTerminal> Terminals(params string[] names) =>
        [.. names.Select(n => new ClassMethodTools.ClassTerminal(n, null))];

    /// <summary>
    /// The measured case: two class stand-ins and one real path input. Only the real one counts.
    /// </summary>
    [Fact]
    public void A_real_path_parameter_is_counted_and_the_class_stand_ins_are_not()
    {
        using var doc = new Doc();
        var aixml = doc.Write("Drucken.xml", """
            <VI _name="Drucken.vi" description="Prints a file.">
              <Control _name="Drucker in" conIdx="0" connection="recommended" type="path" uid="4210" uid_parent="root" value="" outputs="value:"/>
              <Control _name="Datei" conIdx="5" connection="recommended" type="path" uid="4211" uid_parent="root" value="" outputs="value:"/>
              <Control _name="Kopien" conIdx="7" connection="recommended" type="int32" uid="4212" uid_parent="root" value="0" outputs="value:"/>
              <Indicator _name="Drucker out" conIdx="4" connection="recommended" type="path" uid="4295" uid_parent="root" value="" inputs="value:"/>
            </VI>
            """);

        Assert.Equal(1, ClassMethodTools.GenuinePathTerminals(
            aixml, Terminals("Drucker in", "Drucker out")));
    }

    /// <summary>
    /// THE CONTROL ARM. Without it a function that always answered 1 would pass the test above,
    /// and the gate would be wrong in the other direction - waving through a stand-in that never
    /// got retyped, which is the 2026-09-02 failure the whole verify exists for.
    /// </summary>
    [Fact]
    public void A_method_whose_only_paths_are_stand_ins_counts_none()
    {
        using var doc = new Doc();
        var aixml = doc.Write("Schalten.xml", """
            <VI _name="Schalten.vi" description="Switches the lamp.">
              <Control _name="Lampe in" conIdx="11" connection="recommended" type="path" uid="4210" uid_parent="root" value="" outputs="value:"/>
              <Control _name="An" conIdx="10" connection="recommended" type="bool" uid="4211" uid_parent="root" value="false" outputs="value:"/>
              <Indicator _name="Lampe out" conIdx="3" connection="recommended" type="path" uid="4270" uid_parent="root" value="" inputs="value:"/>
            </VI>
            """);

        Assert.Equal(0, ClassMethodTools.GenuinePathTerminals(
            aixml, Terminals("Lampe in", "Lampe out")));
    }

    /// <summary>
    /// A CONSTANT IS NOT A TERMINAL. Log paths are wired in as diagram constants on almost every
    /// method this repository generates, and counting one would inflate the expectation and hide a
    /// stand-in that really was left behind.
    /// </summary>
    [Fact]
    public void A_path_CONSTANT_on_the_diagram_is_not_counted()
    {
        using var doc = new Doc();
        var aixml = doc.Write("Anhalten.xml", """
            <VI _name="Anhalten.vi" description="Stops the fan.">
              <Control _name="Ventilator in" conIdx="11" connection="recommended" type="path" uid="4210" uid_parent="root" value="" outputs="value:"/>
              <Constant _name="Log Path" type="path" uid="4220" uid_parent="root" value="" outputs="value:"/>
              <Indicator _name="Ventilator out" conIdx="3" connection="recommended" type="path" uid="4270" uid_parent="root" value="" inputs="value:"/>
            </VI>
            """);

        Assert.Equal(0, ClassMethodTools.GenuinePathTerminals(
            aixml, Terminals("Ventilator in", "Ventilator out")));
    }

    /// <summary>
    /// An unreadable document answers null rather than zero, because zero would GATE - and gating
    /// on a number nobody could read is how a green answer gets manufactured.
    /// </summary>
    [Fact]
    public void An_unreadable_document_answers_null_so_nothing_is_gated_on_it()
    {
        using var doc = new Doc();
        Assert.Null(ClassMethodTools.GenuinePathTerminals(
            Path.Combine(doc.Root, "gone.xml"), Terminals("Pump in")));

        var broken = doc.Write("broken.xml", "<VI _name=\"x.vi\"");
        Assert.Null(ClassMethodTools.GenuinePathTerminals(broken, Terminals("Pump in")));
    }
}
