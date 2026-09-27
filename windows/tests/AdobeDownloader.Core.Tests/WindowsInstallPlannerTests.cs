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
    private static InstallOperation Shortcut(string name = "Test App", string locale = "en_US") => new("Commands/Shortcut",
        $"<Shortcut><Target>[INSTALLDIR]\\app.exe</Target><Directory>[INSTALLDIR]\\links</Directory><Name><Language locale=\"{locale}\">{name}</Language></Name></Shortcut>");
    [Fact] public void ShellOperationsHaveTypedPreviewAndExplicitRecoveryBlocker()
    {
        var icon = new InstallOperation("Commands/FolderIcon", "<FolderIcon><FolderPath>[INSTALLDIR]</FolderPath><IconPath>[INSTALLDIR]\\app.ico</IconPath></FolderIcon>");
        var plan = WindowsInstallPlanner.Create(Inspect(Shortcut(), icon), Variables, "en_US");
        Assert.Equal(@"C:\Apps\Test\links\Test App.lnk", Assert.Single(plan.Shortcuts!).LinkPath);
        Assert.Equal(@"C:\Apps\Test\app.ico", Assert.Single(plan.FolderIcons!).IconPath);
        Assert.Equal(2, plan.Blockers.Count);
        Assert.All(plan.Blockers, b => Assert.EndsWith("/Recovery", b.Kind));
        Assert.False(plan.CanExecute);
    }
    [Theory] [InlineData("../escape")] [InlineData("CON")] [InlineData("bad:stream")] [InlineData("")]
    public void ShortcutNamesCannotEscapeDirectory(string name)
    {
        var plan = WindowsInstallPlanner.Create(Inspect(Shortcut(name)), Variables, "en_US");
        Assert.Empty(plan.Shortcuts!); Assert.Single(plan.Blockers);
    }
    [Fact] public void ShortcutRequiresExactLocaleAndRejectsDuplicates()
    {
        Assert.Empty(WindowsInstallPlanner.Create(Inspect(Shortcut(locale: "fr_FR")), Variables, "en_US").Shortcuts!);
        var duplicate = Shortcut() with { Xml = Shortcut().Xml.Replace("</Name>", "<Language locale=\"en_US\">Other</Language></Name>") };
        Assert.Empty(WindowsInstallPlanner.Create(Inspect(duplicate), Variables, "en_US").Shortcuts!);
        var plan = WindowsInstallPlanner.Create(Inspect(Shortcut(), Shortcut()), Variables, "en_US");
        Assert.Single(plan.Shortcuts!); Assert.Contains(plan.Blockers, b => b.Reason.Contains("Duplicate shortcut"));
    }
    [Theory] [InlineData("<Extra/>")] [InlineData("<Target>[INSTALLDIR]\\other.exe</Target>")]
    public void ShortcutRejectsUnknownOrDuplicateFields(string field)
    {
        var operation = Shortcut() with { Xml = Shortcut().Xml.Replace("</Shortcut>", field + "</Shortcut>") };
        Assert.Empty(WindowsInstallPlanner.Create(Inspect(operation), Variables, "en_US").Shortcuts!);
    }
    [Fact] public void UserRegistryCapturesProcessSidAndRetainsExecutionBoundary()
    {
        var operation = Registry() with { Xml = Registry().Xml.Replace("HKEY_CLASSES_ROOT", "HKEY_CURRENT_USER") };
        var variables = Variables; variables["UserSid"] = "untrusted-variable";
        var plan = WindowsInstallPlanner.Create(Inspect(operation), variables, "en_US");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User!.Value, Assert.Single(plan.Registry).OwnerSid);
        Assert.Equal("Commands/Registry/UserContext", Assert.Single(plan.Blockers).Kind);
        Assert.False(plan.CanExecute);
    }
    [Theory] [InlineData("isUserPreferences")] [InlineData("isRecursiveDelete")]
    public void UserPreferenceFlagsRemainBlockedUntilTheirSemanticsAreImplemented(string flag)
    {
        var operation = Registry() with { Xml = Registry().Xml.Replace("<Registry>", $"<Registry {flag}=\"true\">") };
        var plan = WindowsInstallPlanner.Create(Inspect(operation), Variables, "en_US");
        Assert.Empty(plan.Registry); Assert.Single(plan.Blockers);
    }
}
