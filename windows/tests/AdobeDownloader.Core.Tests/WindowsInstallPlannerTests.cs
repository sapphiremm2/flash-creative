namespace AdobeDownloader.Core.Tests;

public class WindowsInstallPlannerTests
{
    private static InstallInspection Inspect(params InstallOperation[] operations) => new("APP", "1.0", "archive.zip", new("AdobeHttpsSegmentSha256", "https://adobe.com", 1),
        new("test", "hd-standard", "64-bit", "", new string('a', 64), operations, new Dictionary<string, int>(), [], []));
    private static Dictionary<string, string> Variables => new() { ["INSTALLDIR"] = @"C:\Apps\Test", ["StagingFolder"] = @"C:\Stage\Test", ["AdobeCode"] = "test-guid" };
    private static InstallOperation Asset(string target = @"[INSTALLDIR]\bin") => new("Assets/Asset", $"<Asset source=\"[StagingFolder]\\app\" target=\"{target}\" recursive=\"true\"/>");
    private static InstallOperation Registry(string value = "[INSTALLDIR]", string type = "REG_SZ") => new("Commands/Registry",
        $"<Registry><Path>HKEY_CLASSES_ROOT\\Test.File</Path><Name>Default</Name><Type>{type}</Type><Value>{value}</Value></Registry>");
    [Fact] public void CompilesExplicitMachinePathsAndRegistryView()
    {
        var plan = WindowsInstallPlanner.Create(Inspect(Asset(), Registry()), Variables, "en_US");
        Assert.Empty(plan.Blockers); Assert.False(plan.CanExecute); Assert.True(plan.RequireExecutablePublisherVerification);
        Assert.Equal("DeferredUnverified", plan.DetachedSignatureStatus);
        Assert.Equal(@"C:\Apps\Test\bin", Assert.Single(plan.Assets).Target);
        var value = Assert.Single(plan.Registry); Assert.Equal("Registry64", value.View); Assert.Equal("HKEY_LOCAL_MACHINE", value.Hive);
        Assert.Equal(@"Software\Classes\Test.File", value.Key); Assert.Equal("", value.Name); Assert.Equal(@"C:\Apps\Test", value.Data);
    }
    [Theory] [InlineData(@"[INSTALLDIR]\..\escape")] [InlineData(@"[INSTALLDIR]\CON")]
    [InlineData(@"[INSTALLDIR]\file:stream")] [InlineData(@"[INSTALLDIR]suffix")]
    [InlineData(@"C:\elsewhere")] [InlineData(@"[Missing]\file")]
    public void UnsafeOrUnresolvedDestinationsBlockPlan(string path) => Assert.NotEmpty(WindowsInstallPlanner.Create(Inspect(Asset(path)), Variables, "en_US").Blockers);
    [Fact] public void UnknownAttributesAndOverlappingTargetsAreNotSilentlyAccepted()
    {
        var bad = Asset() with { Xml = Asset().Xml.Replace("recursive=", "mystery=\"true\" recursive=") };
        Assert.Single(WindowsInstallPlanner.Create(Inspect(bad), Variables, "en_US").Blockers);
        Assert.Single(WindowsInstallPlanner.Create(Inspect(Asset(), Asset(@"[INSTALLDIR]\bin\nested")), Variables, "en_US").Blockers);
    }
    [Fact] public void ExactLocaleSelectedAndDuplicatesRejected()
    {
        var operation = Registry() with { Xml = Registry().Xml.Replace("<Value>[INSTALLDIR]</Value>", "<LocalizedValue><Language locale=\"fr_FR\">bonjour</Language><Language locale=\"en_US\">hello</Language></LocalizedValue>") };
        Assert.Equal("hello", Assert.Single(WindowsInstallPlanner.Create(Inspect(operation), Variables, "en_US").Registry).Data);
        Assert.NotEmpty(WindowsInstallPlanner.Create(Inspect(operation), Variables, "ja_JP").Blockers);
        operation = operation with { Xml = operation.Xml.Replace("fr_FR", "en_US") };
        Assert.NotEmpty(WindowsInstallPlanner.Create(Inspect(operation), Variables, "en_US").Blockers);
    }
    [Theory] [InlineData("ff00", "REG_BINARY", false)] [InlineData("", "REG_BINARY", false)]
    [InlineData("f", "REG_BINARY", true)] [InlineData("zz", "REG_BINARY", true)] [InlineData("1", "REG_UNKNOWN", true)]
    public void RegistryTypesHaveExplicitValidation(string value, string type, bool blocked) =>
        Assert.Equal(blocked, WindowsInstallPlanner.Create(Inspect(Registry(value, type)), Variables, "en_US").Blockers.Count > 0);
    [Fact] public void RuntimeAndUnknownCommandsRemainBlocked()
    {
        var report = Inspect(new("Commands/RunProgram", "<RunProgram/>"), new("Commands/Mystery", "<Mystery/>"));
        Assert.Equal(2, WindowsInstallPlanner.Create(report, Variables, "en_US").Blockers.Count);
    }
    [Fact] public void FalseConditionProducesNoOperationsAndUnknownConditionBlocks()
    {
        var report = Inspect(Asset());
        var falsePlan = WindowsInstallPlanner.Create(report with { Manifest = report.Manifest with { Condition = "false" } }, Variables, "en_US");
        Assert.False(falsePlan.Applicable); Assert.Empty(falsePlan.Assets);
        Assert.NotEmpty(WindowsInstallPlanner.Create(report with { Manifest = report.Manifest with { Condition = "[Unknown]==true" } }, Variables, "en_US").Blockers);
    }
    [Fact] public void IdenticalRegistryWritesCoalesceAndMimeKeySlashesArePreserved()
    {
        var operation = Registry() with { Xml = Registry().Xml.Replace("Test.File", "MIME\\Database\\Content Type\\application/x-test") };
        var plan = WindowsInstallPlanner.Create(Inspect(operation, operation), Variables, "en_US");
        Assert.Empty(plan.Blockers); Assert.EndsWith("application/x-test", Assert.Single(plan.Registry).Key);
    }
    [Fact] public void VariableExpansionIsBoundedBeforeAllocation()
    {
        var variables = Variables; variables["Large"] = new string('x', 32767);
        var plan = WindowsInstallPlanner.Create(Inspect(Registry(string.Concat(Enumerable.Repeat("[Large]", 40)))), variables, "en_US");
        Assert.Contains(plan.Blockers, b => b.Reason.Contains("size limit"));
    }
    [Fact] public void RecursiveVariablesAreRejected()
    {
        var variables = Variables; variables["INSTALLDIR"] = "[StagingFolder]";
        Assert.Throws<InvalidDataException>(() => WindowsInstallPlanner.Create(Inspect(Asset()), variables, "en_US"));
    }
    [Fact] public void DuplicateRegistryTargetsBlockInsteadOfOverwriting() =>
        Assert.Single(WindowsInstallPlanner.Create(Inspect(Registry(), Registry("other")), Variables, "en_US").Blockers);
}
