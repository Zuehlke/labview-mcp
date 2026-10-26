using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// <see cref="LabViewContext"/>, issue #99: which LabVIEW, which INI, which add-on locations. The
/// LVAddons keys are OPTIONAL - a stock LabVIEW.ini has none - so a missing key must read as
/// LabVIEW's default and never as "no add-ons". A synthetic installation under %TEMP%: the exe is
/// absent, so the version comes from the folder name ("LabVIEW 2026" -> 26.99) as it would for an
/// unreadable version resource.
/// </summary>
public sealed class LabViewContextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lvctx-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Exe(string release = "2026")
    {
        var folder = Path.Combine(_dir, $"LabVIEW {release}");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "LabVIEW.exe");
    }

    private string Ini(params string[] lines)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"pref-{Guid.NewGuid():N}.ini");
        File.WriteAllLines(path, ["[LabVIEW]", "server.tcp.enabled=False", .. lines, "[Other]", "LVAddons.Enabled=False"]);
        return path;
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void An_ini_without_LVAddons_keys_means_LabVIEWs_defaults()
    {
        var context = LabViewContext.Build(Exe(), Ini(), null, "test");

        Assert.True(context.AddonsEnabled);
        Assert.Equal([LabViewContext.EnabledKey, LabViewContext.CustomKey, LabViewContext.AdditionalKey], context.Defaulted);
        if (AddonTree.DefaultRoot() is { } root)
            Assert.Equal([root], context.AddonLocations);
        else
            Assert.Empty(context.AddonLocations);
        Assert.Equal(26, context.Major);
    }

    [Fact]
    public void A_missing_ini_is_the_same_as_one_without_keys()
    {
        var context = LabViewContext.Build(Exe(), null, null, "test");   // no LabVIEW.ini beside the exe

        Assert.True(context.AddonsEnabled);
        Assert.Equal(3, context.Defaulted.Count);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(Exe())!, "LabVIEW.ini"), context.IniFile);
    }

    [Fact]
    public void Custom_replaces_the_default_and_additional_are_semicolon_separated_after_it()
    {
        var custom = Folder("custom");
        var venv = Folder("repo\\.lv-venv");
        var other = Folder("other");
        var context = LabViewContext.Build(Exe(), Ini(
            $"LVAddons.CustomLocation=\"{custom}\"",
            $"LVAddons.AdditionalLocations={venv}; \"{other}\" ;{Path.Combine(_dir, "missing")}"), null, "test");

        Assert.Equal([custom, venv, other], context.AddonLocations);
        Assert.Equal([LabViewContext.EnabledKey], context.Defaulted);
        Assert.Contains(context.Notes, n => n.Contains("missing") && n.Contains("does not exist"));
        Assert.False(context.OnlyDefaultLocations);
    }

    [Fact]
    public void A_relative_location_is_relative_to_the_ini()
    {
        var venv = Folder(".lv-venv");
        var context = LabViewContext.Build(Exe(), Ini("LVAddons.AdditionalLocations=.lv-venv"), null, "test");

        Assert.Contains(venv, context.AddonLocations);
    }

    [Fact]
    public void Enabled_False_means_no_addons_and_an_empty_location_argument()
    {
        var venv = Folder("venv");
        var context = LabViewContext.Build(Exe(), Ini("LVAddons.Enabled=False", $"LVAddons.AdditionalLocations={venv}"), null, "test");

        Assert.False(context.AddonsEnabled);
        Assert.Empty(context.SelectedAddons());
        var args = AixmlComposer.ContextArgs(context).ToList();
        Assert.Equal("", args[args.IndexOf("--addon-locations") + 1]);
    }

    [Fact]
    public void Additional_locations_before_LabVIEW_2024_are_ignored_and_said_so()
    {
        var venv = Folder("venv");
        var context = LabViewContext.Build(Exe("2023"), Ini($"LVAddons.AdditionalLocations={venv}"), null, "test");

        Assert.DoesNotContain(venv, context.AddonLocations);
        Assert.Contains(context.Notes, n => n.Contains("2024 Q1"));
    }

    [Fact]
    public void The_fingerprint_moves_when_a_package_is_installed()
    {
        var venv = Folder("venv");
        var context = LabViewContext.Build(Exe(), Ini($"LVAddons.AdditionalLocations={venv}"), null, "test");
        var before = context.Fingerprint();

        Directory.CreateDirectory(Path.Combine(venv, "pkg", "1"));
        Directory.SetLastWriteTimeUtc(venv, DateTime.UtcNow.AddMinutes(1));

        Assert.NotEqual(before, context.Fingerprint());
        Assert.Equal(context.LocationsKey, LabViewContext.Build(Exe(), context.PrefFile, null, "test").LocationsKey);
    }

    [Fact]
    public void Lvpm_command_line_yields_the_pref_file()
    {
        var args = ProcessCommandLine.Split(
            "\"C:\\Program Files\\National Instruments\\LabVIEW 2024\\LabVIEW.exe\" -pref C:\\git\\My Project\\.lv-venv\\.lvpm\\labview.ini");
        Assert.Equal("C:\\git\\My", ProcessCommandLine.ValueAfter(args, "pref"));   // unquoted: split at the space, as LabVIEW would

        args = ProcessCommandLine.Split(
            "\"C:\\LabVIEW 2024\\LabVIEW.exe\" -PREF \"C:\\git\\My Project\\.lv-venv\\.lvpm\\labview.ini\" C:\\git\\My Project.lvproj");
        Assert.Equal("C:\\git\\My Project\\.lv-venv\\.lvpm\\labview.ini", ProcessCommandLine.ValueAfter(args, "pref"));
        Assert.Null(ProcessCommandLine.ValueAfter(ProcessCommandLine.Split("\"C:\\LabVIEW.exe\""), "pref"));
    }

    [Fact]
    public void The_own_process_command_line_and_image_are_readable()
    {
        var pid = Environment.ProcessId;
        var image = ProcessCommandLine.ImagePath(pid);
        var line = ProcessCommandLine.Read(pid);

        Assert.NotNull(image);
        Assert.True(LabViewContext.SamePath(image!, Environment.ProcessPath!), image);
        Assert.False(string.IsNullOrEmpty(line));
        Assert.Contains(Path.GetFileNameWithoutExtension(Environment.ProcessPath!), line!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Of_two_LabVIEWs_the_one_whose_command_line_is_in_the_repository_is_chosen()
    {
        var plain = new LabViewContext.Instance(10, @"C:\Program Files\National Instruments\LabVIEW 2024\LabVIEW.exe", null,
            [@"C:\Program Files\National Instruments\LabVIEW 2024\LabVIEW.exe"]);
        var lvpm = new LabViewContext.Instance(20, @"C:\Program Files\National Instruments\LabVIEW 2024\LabVIEW.exe",
            @"C:\git\MyProject\.lv-venv\.lvpm\labview.ini",
            [@"C:\Program Files\National Instruments\LabVIEW 2024\LabVIEW.exe", "-pref",
             @"C:\git\MyProject\.lv-venv\.lvpm\labview.ini", @"C:\git\MyProject\MyProject.lvproj"]);
        var notes = new List<string>();

        Assert.Equal(20, LabViewContext.Choose([plain, lvpm], @"C:\git\MyProject\sub\Caller.vi", null, notes).Pid);
        Assert.Contains(notes, n => n.Contains("nearest"));

        notes.Clear();
        var fallback = LabViewContext.Choose([plain, lvpm], @"D:\elsewhere\X.vi", null, notes);
        Assert.Contains(notes, n => n.Contains("none is tied to this call"));
        Assert.Contains(fallback.Pid, new[] { 10, 20 });
    }

    [Fact]
    public void Addons_of_all_locations_are_ordered_by_name_not_by_location()
    {
        var custom = Folder("custom");
        var venv = Folder("venv");
        foreach (var (location, name) in new[] { (custom, "zcust"), (venv, "other"), (venv, "aprobe") })
        {
            var dir = Path.Combine(location, name, "1");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "lvaddoninfo.json"), $$"""{"AddonName":"{{name}}"}""");
        }
        var context = LabViewContext.Build(Exe(), Ini($"LVAddons.CustomLocation={custom}", $"LVAddons.AdditionalLocations={venv}"), null, "test");

        // measured: the alphabetically first add-on wins, the CustomLocation's no earlier than any
        Assert.Equal(["aprobe", "other", "zcust"], context.SelectedAddons().Select(a => a.Addon));
    }

    [Fact]
    public void The_same_addon_folder_in_two_locations_comes_from_the_later_one_first()
    {
        var custom = Folder("A");
        var venv = Folder("B");
        foreach (var location in new[] { custom, venv })
        {
            var dir = Path.Combine(location, "dup", "1");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "lvaddoninfo.json"), """{"AddonName":"dup"}""");
        }
        var both = LabViewContext.Build(Exe(), Ini($"LVAddons.CustomLocation={custom}", $"LVAddons.AdditionalLocations={venv}"), null, "test");
        var reversed = LabViewContext.Build(Exe(), Ini($"LVAddons.CustomLocation={Folder("empty")}", $"LVAddons.AdditionalLocations={venv};{custom}"), null, "test");

        // measured (lvaddons-probe4): `A;B` took B's copy, `B;A` A's, a CustomLocation lost to an additional one
        Assert.StartsWith(venv, both.SelectedAddons()[0].Folder);
        Assert.StartsWith(custom, reversed.SelectedAddons()[0].Folder);
    }

    [Fact]
    public void Ini_values_are_unquoted_and_the_section_is_respected()
    {
        var ini = Ini("LVAddons.CustomLocation=\"C:\\a b\"", "lvaddons.additionallocations=C:\\x");
        var keys = LabViewContext.ReadIniSection(ini, "LabVIEW");

        Assert.Equal("C:\\a b", keys[LabViewContext.CustomKey]);
        Assert.Equal("C:\\x", keys[LabViewContext.AdditionalKey]);
        Assert.False(keys.ContainsKey(LabViewContext.EnabledKey));   // only in [Other]
    }

    [Fact]
    public async Task CSharp_and_Python_select_the_same_addon_versions()
    {
        var root = Folder("LVAddons");
        void Addon(string name, string version, string? json, bool bom = false)
        {
            var dir = Path.Combine(root, name, version);
            Directory.CreateDirectory(dir);
            if (json is not null)
                File.WriteAllText(Path.Combine(dir, "lvaddoninfo.json"), json, new System.Text.UTF8Encoding(bom));
        }
        Addon("lvai", "1", """{"AddonName":"LVAI","MinimumSupportedLVVersion":"25.0"}""");
        Addon("lvai", "26.1", """{"AddonName":"LVAI","MinimumSupportedLVVersion":"26.0"}""");
        Addon("lvai", "26.3", """{"AddonName":"LVAI","MinimumSupportedLVVersion":"26.3"}""");
        Addon("daq64", "1", """{"AddonName":"daq","MinimumSupportedLVVersion":"22.0","SupportedBitness":"64"}""");
        Addon("daq32", "1", """{"AddonName":"daq","MinimumSupportedLVVersion":"22.0","SupportedBitness":32}""");
        Addon("plain", "1", """{"AddonName":"plain"}""");
        // each of these LabVIEW ignores - measured 2026-10-26
        Addon("noinfo", "1", null);
        Addon("bad", "1", "not json");
        Addon("bom", "1", """{"AddonName":"bom"}""", bom: true);
        Addon("noname", "1", """{"ApiVersion":"v1"}""");
        Addon("newer", "2", """{"AddonName":"newer","MinimumSupportedLVVersion":"30.0"}""");

        var admitted = AddonTree.Select(root, 26, 3, 32).Select(a => a.Addon).ToList();
        Assert.Equal(["daq32", "lvai", "plain"], admitted);

        var bundle = PyLabview.Locate();
        var composer = Path.Combine(RepoTree.Root, "scripts", "aixml-pylabview");
        if (bundle is null) return;

        foreach (var (major, minor, bits) in new (int?, int?, int?)[] { (26, 3, 32), (26, 1, 64), (25, 0, 32), (null, null, null) })
        {
            var csharp = string.Join(",", AddonTree.Select(root, major, minor, bits)
                .Select(a => $"{a.Addon}={Path.GetFileName(a.Folder)}"));

            var script = Path.Combine(_dir, $"sel-{Guid.NewGuid():N}.py");
            var version = major is null ? "None" : $"({major}, {minor})";
            var bitness = bits is null ? "None" : bits.ToString();
            File.WriteAllText(script,
                "import os, sys\n" +
                $"sys.path.insert(0, r'{composer}')\n" +
                "import resolve\n" +
                $"print(','.join('%s=%s' % (a, os.path.basename(v)) for a, v in resolve.select_addons(r'{root}', {version}, {bitness})))\n");
            var run = await PyLabview.RunAsync(bundle, script, [], 60, CancellationToken.None);

            Assert.True(run.ExitCode == 0, run.StdErr);
            Assert.Equal(csharp, run.StdOut.Trim());
        }
    }
}
