namespace AdobeDownloader.Core.Tests;

public class ModuleTests
{
    private static ApplicationManifest Manifest => PlannerTests.Manifest(PlannerTests.Build("APP"),
        [PlannerTests.Package("main"), PlannerTests.Package("optional") with { Type = "non-core", Alias = "alias" },
         PlannerTests.Package("feature") with { Type = "non-core", Features = ["Extra"] }]) with
        { Modules = [new("Addon", "Addon", "Deferred", true, false, ["alias"])] };
    [Fact] public void OptionalModulesAndFeaturesRequireSelection() =>
        Assert.Equal(new[] { "main" }, ModuleSelection.Select(Manifest, "APP", new(), new HashSet<string>()));
    [Fact] public void ExplicitConsentSelectionIncludesAliasReferenceAndFeature() =>
        Assert.Equal(3, ModuleSelection.Select(Manifest, "APP", new(["Addon"], ["Extra"]), new HashSet<string>()).Count);
    [Fact] public void RequiredConsentModuleIsNotImplicitlyAccepted() =>
        Assert.Throws<InvalidDataException>(() => ModuleSelection.Select(Manifest with
        { Modules = [new("Addon", "Addon", "Immediate", true, true, ["alias"])] }, "APP", new(), new HashSet<string>()));
    [Fact] public void MissingModulePackageFailsClosed() =>
        Assert.Throws<InvalidDataException>(() => ModuleSelection.Select(Manifest with
        { Modules = [new("Addon", "Addon", "Deferred", false, false, ["missing"])] }, "APP", new(), new HashSet<string>()));
    [Fact] public void QualifiedDependencySelectionResolves() =>
        Assert.Contains("optional", ModuleSelection.Select(Manifest, "ROOT", new(["APP:Addon"]), new HashSet<string>()));
    [Fact] public void MandatoryCoreCannotBeOmittedByFeatureMetadata() =>
        Assert.Contains("main", ModuleSelection.Select(Manifest with { Packages = [PlannerTests.Package("main") with { Features = ["Extra"] }] , Modules = [] }, "APP", new(), new HashSet<string>()));
    [Fact] public void CoreLabelCannotBypassModuleConsent() =>
        Assert.Throws<InvalidDataException>(() => ModuleSelection.Select(Manifest with
        { Modules = [new("Addon", "Addon", "Deferred", true, false, ["main"])] }, "APP", new(), new HashSet<string>()));
    [Fact] public void MissingDeploymentTypeFailsClosed() =>
        Assert.Throws<NotSupportedException>(() => ModuleSelection.Select(Manifest with
        { Modules = [new("Addon", "Addon", "", false, false, ["alias"])] }, "APP", new(), new HashSet<string>()));
}
