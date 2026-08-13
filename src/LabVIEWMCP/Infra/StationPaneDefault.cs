using LabVIEWMcp.Grpc;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Which connector pane pattern a NEWLY created VI gets on this station - read out of
/// <c>LabVIEW.ini</c>, key <c>DefaultConPane</c>.
///
/// WHY THIS EXISTS, and it is a correction. This repository twice wrote down a fixed conIdx map for
/// generated VIs, and twice it was wrong on a machine other than the one it was measured on. The
/// second time cost a rejected VI. Both write-ups reasoned that the AIXML generator "chooses" the
/// pattern and that the choice could not be predicted - one of them measured index sets against
/// patterns trying to find the rule.
///
/// There is no rule to find: the generator does not choose at all. LabVIEW gives every new VI the
/// station's DEFAULT pane, and that default is a setting. `DefaultConPane="4833"` is what this
/// station carries, which is why everything generated here comes out 16-terminal; LabVIEW's factory
/// default is 4815, the 12-terminal 4x2x2x4, which is why the older measurement was right when it
/// was made and wrong later. So the pattern IS knowable in advance - just not from the AIXML, and
/// not from anything inside LabVIEW's API.
///
/// WHAT THIS DOES NOT TELL YOU. Only the pattern of a *new* VI. An existing VI carries whatever pane
/// it was given, possibly rotated or flipped, so <c>lvai_connector_pane</c> still measures rather
/// than assumes - see <see cref="ConnectorPanePatterns"/> on orientations. And a setting can change
/// under you: an edited ini, a different machine, a colleague's station. That is an argument for
/// reading it each time, which is what this does, not for writing the number down again.
///
/// READ-ONLY, AND THAT IS A RULE RATHER THAN AN OVERSIGHT. There is no write path here and none is to
/// be added: the station's owner has ruled out modifying <c>LabVIEW.ini</c>. It is tempting for one
/// specific job - setting <c>DefaultConPane</c> would let the four unmeasured patterns be observed on
/// a throwaway VI - and that is exactly the temptation being refused. LabVIEW also rewrites the file
/// on exit, so a write would have to be timed around the IDE's lifetime, which makes it worse rather
/// than better. Report what the key says; if it must change, the user changes it.
/// </summary>
internal static class StationPaneDefault
{
    /// <summary>LabVIEW's own default when the key is absent: the 12-terminal 4x2x2x4.</summary>
    public const int FactoryDefault = 4815;

    internal sealed record Reading(int? Pattern, string? IniPath, string Note);

    /// <summary>
    /// The station's default pattern, or null when it cannot be established. Never guesses: an
    /// absent key is reported as absent, with the factory default named as what LabVIEW would then
    /// use, because "probably 4815" and "4815, measured" must not read the same.
    /// </summary>
    public static Reading Read(string? iniPath = null)
    {
        var path = iniPath ?? Locate();
        if (path is null)
            return new Reading(null, null,
                "No LabVIEW.ini found next to any installed LabVIEW.exe, so the station default is " +
                $"unknown. LabVIEW's own default is {FactoryDefault}.");

        if (!File.Exists(path))
            return new Reading(null, path, $"No LabVIEW.ini at '{path}'.");

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (IOException error)
        {
            return new Reading(null, path, $"LabVIEW.ini could not be read: {error.Message}");
        }

        if (FindValue(lines) is not { } raw)
            return new Reading(null, path,
                "LabVIEW.ini carries no DefaultConPane, so LabVIEW uses its own default, " +
                $"{FactoryDefault}. Nothing has been measured to confirm that on this station.");

        return int.TryParse(raw, out var pattern)
            ? new Reading(pattern, path, $"LabVIEW.ini: DefaultConPane={raw}")
            : new Reading(null, path,
                $"LabVIEW.ini carries DefaultConPane={raw}, which is not a pattern number.");
    }

    /// <summary>
    /// The key's value, unquoted. Written by hand as often as by LabVIEW, so leading whitespace, no
    /// whitespace, quotes and no quotes all have to read the same.
    /// </summary>
    internal static string? FindValue(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var text = line.Trim();
            if (!text.StartsWith("DefaultConPane", StringComparison.OrdinalIgnoreCase)) continue;

            var equals = text.IndexOf('=');
            if (equals < 0) continue;

            // Guard against a longer key that merely starts the same way.
            if (text[..equals].Trim().Length != "DefaultConPane".Length) continue;

            var value = text[(equals + 1)..].Trim().Trim('"').Trim();
            if (value.Length > 0) return value;
        }

        return null;
    }

    /// <summary>
    /// LabVIEW.ini sits next to LabVIEW.exe. Uses the same discovery and the same preference order as
    /// everything else here, so the ini read belongs to the installation the tools actually talk to.
    /// </summary>
    private static string? Locate()
    {
        var install = LabViewLocator.Select(LabViewLocator.Discover());
        if (install is null) return null;

        var directory = Path.GetDirectoryName(install.ExePath);
        return directory is null ? null : Path.Combine(directory, "LabVIEW.ini");
    }
}
