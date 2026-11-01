using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The build contract's offline checks. The fixture is the ATM DQMH cold build of 2026-10-31,
/// reduced: a loose `Deposit.vi` beside the DQMH module's `Account Manager.lvlib:Deposit.vi` is
/// the clash that cost that build a failing Caraya run (Error 403681).
/// </summary>
public class BuildContractTests
{
    private const string Err = BuildContract.ErrorClusterType;

    private static string Pane(string inputs, string outputs) =>
        $"\"inputs\": [{inputs}], \"outputs\": [{outputs}]";

    private static string T(string name, string type, int conIdx, string connection) =>
        $"{{\"name\": \"{name}\", \"type\": \"{type}\", \"conIdx\": {conIdx}, \"connection\": \"{connection}\"}}";

    /// <summary>A compliant leaf on 4833: path in 0, error in 11, accounts out 4, error out 15.</summary>
    private static string Leaf(string path, string calls = "") =>
        $"{{\"path\": \"{path}\", \"builder\": \"leaf\", " +
        Pane(T("Path", "path", 0, "recommended") + "," + T("error in", Err, 11, "required"),
             T("Accounts", "array{string}", 4, "recommended") + "," + T("error out", Err, 15, "recommended")) +
        (calls.Length > 0 ? $", \"calls\": [{calls}]" : "") + "}";

    private static BuildContract.Document Doc(string vis, string extra = "") =>
        BuildContract.Parse($"{{ {extra} \"root\": \"C:\\\\Build\", \"vis\": [{vis}] }}", @"C:\Build\build-contract.json");

    private static BuildContract.PlanResult Plan(string vis, string extra = "") =>
        BuildContract.Plan(Doc(vis, extra), 4833);

    [Fact]
    public void ACompliantContractHasNoErrorsAndOrdersItsWaves()
    {
        var result = Plan(Leaf(@"SubVIs\\Read.vi") + "," + Leaf(@"SubVIs\\Find.vi", "\"Read.vi\"") + "," +
                          Leaf(@"Main\\Main.vi", "\"Find.vi\", \"Read.vi\""));

        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Waves.Count);
        Assert.Equal("Read.vi", result.Waves[0].Single().FileName);
        Assert.Equal("Main.vi", result.Waves[2].Single().FileName);
    }

    [Fact]
    public void AnUnknownKeyIsRefusedByName()
    {
        var e = Assert.Throws<ArgumentException>(() =>
            Doc("{\"path\": \"A.vi\", \"inptus\": []}"));
        Assert.Contains("inptus", e.Message);
        Assert.Contains("Accepted", e.Message);
    }

    [Fact]
    public void ANamedTypeIsExpandedAndAnUndefinedOneRefused()
    {
        var doc = Doc("{\"path\": \"A.vi\", \"kind\": \"existing\", \"inputs\": [{\"name\": \"S\", \"type\": \"@State\", \"conIdx\": 0}]}",
                      "\"types\": {\"State\": \"cluster{bool.Card Inserted}\"},");
        Assert.Equal("cluster{bool.Card Inserted}", doc.Vis[0].Inputs[0].Type);

        Assert.Throws<ArgumentException>(() =>
            Doc("{\"path\": \"A.vi\", \"inputs\": [{\"name\": \"S\", \"type\": \"@Nope\", \"conIdx\": 0}]}"));
    }

    [Fact]
    public void ALooseVIBesideAModuleVIOfTheSameNameIsNamedAndABareCallIsAmbiguous()
    {
        var module = "{\"path\": \"Libraries\\\\Account Manager\\\\Deposit.vi\", \"owner\": \"Account Manager.lvlib\", \"kind\": \"existing\"}";
        var result = Plan(module + "," + Leaf(@"SubVIs\\Deposit.vi") + "," +
                          Leaf(@"Tests\\Test Deposit.vi", "\"Deposit.vi\""));

        Assert.Contains(result.Warnings, w => w.Rule == "sameFileName");
        var ambiguous = Assert.Single(result.Errors, e => e.Rule == "ambiguousCall");
        Assert.StartsWith(@"rename SubVIs\Deposit.vi", ambiguous.Fix);
        Assert.Contains("Account Manager.lvlib:Deposit.vi", ambiguous.Fix);
    }

    [Fact]
    public void AQualifiedCallResolvesWithoutAmbiguity()
    {
        var module = "{\"path\": \"Libraries\\\\Account Manager\\\\Deposit.vi\", \"owner\": \"Account Manager.lvlib\", \"kind\": \"existing\"}";
        var result = Plan(module + "," + Leaf(@"SubVIs\\Deposit.vi") + "," +
                          Leaf(@"Main.vi", "\"Account Manager.lvlib:Deposit.vi\", \"SubVIs\\\\Deposit.vi\""));

        Assert.DoesNotContain(result.Errors, e => e.Rule == "ambiguousCall");
        Assert.Equal(2, result.Waves.Count);
    }

    [Fact]
    public void TwoLooseVIsOfOneNameAreAnError()
    {
        var result = Plan(Leaf(@"A\\X.vi") + "," + Leaf(@"B\\X.vi"));
        Assert.Contains(result.Errors, e => e.Rule == "sameFileNameTwice");
    }

    [Fact]
    public void TheHouseRulesForANewVIAreEachNamed()
    {
        var vi = "{\"path\": \"A.vi\", " + Pane(
            T("Path", "path", 0, "recommended") + "," + T("error in (no error)", Err, 11, "recommended"),
            "{\"name\": \"Out\", \"type\": \"string\", \"conIdx\": 4}") + "}";
        var rules = Plan(vi).Errors.Select(e => e.Rule).ToList();

        Assert.Contains("errorInLabel", rules);
        Assert.Contains("noErrorOut", rules);
        Assert.Contains("outputRequired", rules); // connection omitted means required
    }

    [Fact]
    public void ErrorInMustBeRequiredAndOfTheErrorClusterType()
    {
        var vi = "{\"path\": \"A.vi\", " + Pane(
            T("error in", "cluster{bool.status}", 11, "recommended"),
            T("error out", Err, 15, "recommended")) + "}";
        var rules = Plan(vi).Errors.Select(e => e.Rule).ToList();

        Assert.Contains("errorInNotRequired", rules);
        Assert.Contains("errorClusterType", rules);
    }

    [Fact]
    public void AnInputOnTheOutputEdgeAndASlotOffThePatternAreViolations()
    {
        // 4833: conIdx 4 is top RIGHT, and 25 is a slot of neither 4833 nor the widened 4834
        var vi = "{\"path\": \"A.vi\", " + Pane(
            T("Path", "path", 4, "recommended") + "," + T("error in", Err, 11, "required"),
            T("X", "string", 25, "recommended") + "," + T("error out", Err, 15, "recommended")) + "}";
        var pane = Plan(vi).Errors.Where(e => e.Rule == "pane").ToList();

        Assert.Contains(pane, e => e.Terminal == "Path");
        Assert.Contains(pane, e => e.Terminal == "X");
    }

    /// <summary>The ATM build's Find Account.vi: outputs up to conIdx 19 came out on 4834.</summary>
    [Fact]
    public void ConIdxBeyondTheStationPatternIsCheckedAgainstTheWidenedOne()
    {
        // 4834 (6x2x2x2x2x6) as the generator produced it: error in 5, error out 19
        var vi = "{\"path\": \"Find Account.vi\", " + Pane(
            T("Accounts", "array{string}", 0, "recommended") + "," + T("error in", Err, 5, "required"),
            T("Found", "bool", 14, "recommended") + "," + T("error out", Err, 19, "recommended")) + "}";
        var result = Plan(vi);

        Assert.Equal(BuildContract.WidenedPattern, result.PatternOf[0]);
        Assert.Contains(result.Warnings, w => w.Rule == "patternWidened");
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains("is not a slot"));
    }

    [Fact]
    public void AClassMemberIsCheckedAgainst4815NotTheStationDefault()
    {
        // 4815: error in 8, error out 0 - the station's 11/15 would be wrong here
        var member = "{\"path\": \"C\\\\M.vi\", \"owner\": \"C.lvclass\", " + Pane(
            T("error in", Err, 8, "required"), T("error out", Err, 0, "recommended")) + "}";
        var result = Plan(member);

        Assert.DoesNotContain(result.Errors, e => e.Rule == "pane");
        Assert.Equal(4815, result.PatternOf[0]);
    }

    [Fact]
    public void ASuppliedPanelAndAToolMadeVIAreNotHeldToTheHouseRules()
    {
        var result = Plan("{\"path\": \"Main.vi\", \"kind\": \"supplied\"}," +
                          "{\"path\": \"M.vi\", \"kind\": \"existing\", \"owner\": \"M.lvlib\"}");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ACycleIsAnError()
    {
        var result = Plan(Leaf("A.vi", "\"B.vi\"") + "," + Leaf("B.vi", "\"A.vi\""));
        Assert.Contains(result.Errors, e => e.Rule == "callCycle");
    }

    [Fact]
    public void TwoBuildersInOneFolderAreWarnedAbout()
    {
        var other = Leaf(@"SubVIs\\B.vi").Replace("\"leaf\"", "\"ui\"");
        var result = Plan(Leaf(@"SubVIs\\A.vi") + "," + other);
        Assert.Contains(result.Warnings, w => w.Rule == "sharedOutputDirectory");
    }

    [Fact]
    public void AnUnknownCallIsExternalNotAnError()
    {
        var result = Plan(Leaf("A.vi", "\"Simple Error Handler.vi\""));
        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, w => w.Rule == "externalCall");
    }

    [Fact]
    public void AnInstallationNamesakeIsNamed()
    {
        var result = BuildContract.Plan(Doc(Leaf("Trim Whitespace.vi")), 4833,
            name => name == "Trim Whitespace.vi" ? @"vi.lib\string.mnu" : null);
        Assert.Contains(result.Warnings, w => w.Rule == "installationNamesake");
    }

    // ---------------------------------------------------------------- verify

    private static BuildContract.Vi OneLeaf() => Doc(Leaf("A.vi")).Vis[0];

    [Fact]
    public void ABuiltPaneEqualToTheContractHasNoDifferences()
    {
        var differences = BuildContract.Compare(OneLeaf(),
            [new("Path", "path", 0, "recommended"), new("error in", "cluster{bool.status, int32.code, string.source}", 11, "required")],
            [new("Accounts", "array{string}", 4, "recommended"), new("error out", Err, 15, "recommended")]);
        Assert.Empty(differences);
    }

    [Fact]
    public void EveryKindOfDeviationIsNamed()
    {
        var differences = BuildContract.Compare(OneLeaf(),
            [new("Path", "string", 1, "recommended"), new("error in", Err, 11, null), new("Extra", "bool", 2, "optional")],
            [new("error out", Err, 15, "recommended")]);

        Assert.Contains(differences, d => d.Contains("'Path' type string"));
        Assert.Contains(differences, d => d.Contains("'Path' sits on conIdx 1"));
        Assert.Contains(differences, d => d.Contains("'Accounts' is not on the built pane"));
        Assert.Contains(differences, d => d.Contains("'Extra' is on the built pane but not in the contract"));
        Assert.DoesNotContain(differences, d => d.StartsWith("'error in' is required"));
    }
}
