using System.Diagnostics;
using System.Runtime.Versioning;

namespace LabVIEWMcp.Export;

/// Drives the exporter VI over LabVIEW's ActiveX VI Server - the public, documented
/// interface - so nothing in the runtime path touches lvai.LVAI.
///
/// Uses Call rather than Run. Run needs the VI idle, and a VI that lvai's RunVIAsTopLevel
/// has touched stays in ExecState 2 (reserved as top level) for the rest of the session,
/// where Run answers "The VI is not in a state compatible with this operation". Call
/// invokes it with parameters the way a caller would, which is also what its connector
/// pane is for.
[SupportedOSPlatform("windows")]
internal static class ViServerDriver
{
    public static object Connect()
    {
        var t = Type.GetTypeFromProgID("LabVIEW.Application")
                ?? throw new InvalidOperationException("LabVIEW.Application is not registered.");
        return Activator.CreateInstance(t)
               ?? throw new InvalidOperationException("could not create LabVIEW.Application.");
    }

    /// Returns (diagramCount, errorSource, elapsedMs).
    public static (string DiagramCount, string ErrorSource, double Ms) RunExporter(
        object lvApp, string probeVi, string targetVi, string outFile)
    {
        dynamic lv = lvApp;
        dynamic vi = lv.GetVIReference(probeVi);

        // Connector-pane order is how Call matches them up, but names are accepted and are
        // far less brittle than positions.
        object[] names = ["vi path", "out path", "diagram count", "error source"];
        object[] values = [targetVi, outFile, "", ""];

        var sw = Stopwatch.StartNew();
        vi.Call(ref names, ref values);
        sw.Stop();

        return (values[2]?.ToString() ?? "", values[3]?.ToString() ?? "", sw.Elapsed.TotalMilliseconds);
    }
}
