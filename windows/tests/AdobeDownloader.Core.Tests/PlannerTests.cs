namespace AdobeDownloader.Core.Tests;

public class PlannerTests
{
    internal static ProductBuild Build(string code, string version = "1.0", string platform = "win64", params Dependency[] deps) =>
        new(code, code, version, version, "1.0", platform, "ccm", "LS20", code + version + platform,
            new Uri("https://ccmdls.adobe.com"), ["en_US"], deps, "hdPackage");
    internal static PackageAsset Package(string name = "test", string condition = "", string family = "64-bit") =>
        new(name, name + ".zip", "core", family, condition, 3, new Uri("https://ccmdls.adobe.com/" + name + ".zip"), "");
    internal static ApplicationManifest Manifest(ProductBuild build, IReadOnlyList<PackageAsset>? packages = null,
        IReadOnlyList<Dependency>? dependencies = null) =>
        new(build.SapCode, build.ProductVersion, build.Platform, packages ?? [Package()], dependencies ?? [], "{}");
    private static DownloadPlanner Planner(Func<ProductBuild, ApplicationManifest>? fetch = null) =>
        new((build, _) => Task.FromResult(fetch is null ? Manifest(build) : fetch(build)));

    [Fact] public async Task SelectsNewestCompatibleBaseNumericallyAndOrdersDependencyFirst()
    {
        var root = Build("APP", deps: [new Dependency("DEP", "1.0")]);
        var catalog = new[] { root, Build("DEP", "1.9"), Build("DEP", "1.10"), Build("DEP", "2.0") with { BaseVersion = "2.0" } };
        var plan = await Planner().CreateAsync(root, catalog, "en_US", "10.0.22000");
        Assert.Equal(new[] { "DEP", "APP" }, plan.Products.Select(x => x.Build.SapCode));
        Assert.Equal("1.10", plan.Products[0].Build.ProductVersion);
        Assert.Equal(6, plan.TotalBytes);
    }

    [Fact] public async Task UsesWin32SharedManifestAndSelectsX64PayloadForTarget()
    {
        var root = Build("APP", deps: [new Dependency("ACR", "1.0")]);
        var shared = Build("ACR", platform: "win32");
        var plan = await Planner(b => b.SapCode == "APP" ? Manifest(b) : Manifest(b,
            [Package("x64", "[OSProcessorFamily]==64-bit && [OSVersion]>=10.0"),
             Package("old", "[OSVersion]<10.0"), Package("shared", family: "32-bit")]))
            .CreateAsync(root, [root, shared], "en_US", "10.0.22000");
        Assert.Equal(new[] { "x64", "shared", "test" }, plan.Downloads.Select(x => x.Package.Name));
        Assert.False(plan.Products[0].Decisions.Single(x => x.Package == "old").Included);
    }

    [Fact] public async Task HonorsPinnedManifestVersion()
    {
        var root = Build("APP", deps: [new Dependency("DEP", "1.0")]);
        var plan = await Planner(b => Manifest(b, dependencies: b.SapCode == "APP" ? [new("DEP", "1.0", "1.1")] : []))
            .CreateAsync(root, [root, Build("DEP", "1.1"), Build("DEP", "1.2")], "en_US", "10.0");
        Assert.Equal("1.1", plan.Products[0].Build.ProductVersion);
    }

    [Fact] public async Task MissingDependencyFailsWithoutFallbackToDifferentBase()
    {
        var root = Build("APP", deps: [new Dependency("DEP", "5.0")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Planner().CreateAsync(root, [root, Build("DEP")], "en_US", "10.0"));
    }

    [Fact] public async Task DetectsCycle()
    {
        var a = Build("A", deps: [new Dependency("B", "1.0")]); var b = Build("B", deps: [new Dependency("A", "1.0")]);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Planner().CreateAsync(a, [a, b], "en_US", "10.0"));
        Assert.Contains("cycle", ex.Message);
    }

    [Fact] public async Task SharedDiamondDependencyDownloadedOnce()
    {
        var a = Build("A", deps: [new("B", "1.0"), new("C", "1.0")]);
        var b = Build("B", deps: [new("D", "1.0")]); var c = Build("C", deps: [new("D", "1.0")]); var d = Build("D");
        var plan = await Planner().CreateAsync(a, [a, b, c, d], "en_US", "10.0");
        Assert.Single(plan.Products.Where(p => p.Build.SapCode == "D"));
        Assert.Equal(4, plan.Downloads.Count);
    }

    [Fact] public async Task ConflictingDiamondConstraintsFail()
    {
        var a = Build("A", deps: [new("B", "1.0"), new("C", "1.0")]);
        var b = Build("B", deps: [new("D", "1.0", "1.1")]); var c = Build("C", deps: [new("D", "1.0", "1.2")]);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Planner().CreateAsync(a,
            [a, b, c, Build("D", "1.1"), Build("D", "1.2")], "en_US", "10.0"));
        Assert.Contains("Conflicting", ex.Message);
    }

    [Fact] public async Task AmbiguousGuidsFail()
    {
        var root = Build("APP", deps: [new Dependency("DEP", "1.0")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Planner().CreateAsync(root,
            [root, Build("DEP"), Build("DEP") with { BuildGuid = "other" }], "en_US", "10.0"));
    }

    [Fact] public async Task ArmPlanDoesNotIncludeX64Payload()
    {
        var root = Build("ARMAPP", platform: "winarm64");
        var plan = await Planner(b => Manifest(b, [Package("arm", family: "arm64"), Package("intel", family: "x64")]))
            .CreateAsync(root, [root], "en_US", "10.0");
        Assert.Equal("arm", Assert.Single(plan.Downloads).Package.Name);
    }

    [Fact] public async Task BacktracksEarlierDependencyWhenLaterProductPinsOlderVersion()
    {
        var root = Build("ROOT", deps: [new("A", "1.0"), new("Z", "1.0")]);
        var later = Build("Z", deps: [new("A", "1.0", "1.1")]);
        var plan = await Planner().CreateAsync(root, [root, Build("A", "1.1"), Build("A", "1.2"), later], "en_US", "10.0");
        Assert.Equal("1.1", plan.Products.Single(p => p.Build.SapCode == "A").Build.ProductVersion);
    }

    [Fact] public async Task UnknownModuleSelectionStopsPlanning()
    {
        var root = Build("APP");
        await Assert.ThrowsAsync<InvalidDataException>(() => Planner().CreateAsync(root, [root], "en_US", "10.0",
            options: new SelectionOptions(["unknown"])));
    }

    [Fact] public async Task NativeArmManifestUses64BitAsBitness()
    {
        var root = Build("ARM", platform: "winarm64");
        var plan = await Planner(b => Manifest(b, [Package("legacy_x64", "[OSProcessorFamily]==64-bit")]))
            .CreateAsync(root, [root], "en_US", "10.0");
        Assert.Single(plan.Downloads);
    }
    [Fact] public async Task SharedWin32DoesNotAuthorize64BitPayloadOnArm()
    {
        var root = Build("ARM", platform: "winarm64", deps: [new("SHARED", "1.0")]);
        var shared = Build("SHARED", platform: "win32");
        await Assert.ThrowsAsync<InvalidDataException>(() => Planner(b => Manifest(b,
            [Package(family: b.Platform == "winarm64" ? "arm64" : "64-bit")]))
            .CreateAsync(root, [root, shared], "en_US", "10.0"));
    }
    [Fact] public async Task DeltaWithoutTrustedBaselineFallsBackToFullAndExplainsWhy()
    {
        var root = Build("APP");
        var full = Package() with { Deltas = [new("delta", "0.9", 1, new("https://ccmdls.adobe.com/delta.zip"), new("https://ccmdls.adobe.com/diff.json"), "")] };
        var plan = await Planner(b => Manifest(b, [full])).CreateAsync(root, [root], "en_US", "10.0");
        Assert.Equal(full.Url, Assert.Single(plan.Downloads).Package.Url);
        var decision = Assert.Single(Assert.Single(plan.Products).Decisions).Deltas!.Single();
        Assert.False(decision.Selected);
        Assert.Contains("no verified installed baseline", decision.Reason);
    }
    [Fact] public async Task ExplicitIncompatibleModuleFailsInsteadOfBeingSilentlyOmitted()
    {
        var root = Build("APP");
        var planner = Planner(b => Manifest(b, [Package(), Package("addon", family: "arm64") with { Type = "non-core" }]) with
            { Modules = [new("Addon", "Addon", "Deferred", false, false, ["addon"])] });
        await Assert.ThrowsAsync<InvalidDataException>(() => planner.CreateAsync(root, [root], "en_US", "10.0", options: new(["Addon"])));
    }

    [Fact] public async Task WrongLocaleFails()
    {
        var root = Build("APP");
        await Assert.ThrowsAsync<InvalidDataException>(() => Planner().CreateAsync(root, [root], "ja_JP", "10.0"));
    }

    [Theory]
    [InlineData("[OSVersion]>=10.0 && [InstallLanguage]==en_US", true)]
    [InlineData("[OSVersion]==10.0.22000.0", true)]
    [InlineData("false && true || true", true)]
    [InlineData("true || false && false", true)]
    [InlineData("!(true || false)", false)]
    [InlineData("([OSVersion]<10.0 || [InstallLanguage]=='fr_FR') && true", false)]
    [InlineData("[InstallLanguage]==fr_FR,en_US", true)]
    public void EvaluatesConditionsWithoutSkippingTokens(string expression, bool expected) =>
        Assert.Equal(expected, PackageConditions.Evaluate(expression, new Dictionary<string, string>
            { ["OSVersion"] = "10.0.22000", ["InstallLanguage"] = "en_US" }));

    [Theory]
    [InlineData("true || [Unknown]==x")] [InlineData("false && [Unknown]==x")]
    [InlineData("true trailing")] [InlineData("(true")] [InlineData("[OSVersion]>=")]
    [InlineData("runProgram('thing')")]
    [InlineData("[InstallLanguage]=='en_US")]
    public void UnsupportedConditionsFailClosed(string expression) => Assert.Throws<InvalidDataException>(() =>
        PackageConditions.Evaluate(expression, new Dictionary<string, string> { ["OSVersion"] = "10.0" }));
}
