using DiskThinner.Inventory;
using DiskThinner.Rules;
using DiskThinner.Scanner;
using static DiskThinner.Rules.Tests.Helpers;

namespace DiskThinner.Rules.Tests;

/// <summary>归属引擎：规则写明的应用、按已装软件 / 目录名 / 程序版本信息推断、优先级。</summary>
public class OwnerTests
{
    private const string Local = @"C:\Users\demo\AppData\Local";
    private const string Roaming = @"C:\Users\demo\AppData\Roaming";

    internal static InstalledApp App(string name, string? publisher = null, string? location = null, string? exe = null,
        AppSource source = AppSource.Registry, string? id = null, string? raw = null) => new()
    {
        Name = name,
        Publisher = AppNames.CleanPublisher(publisher),
        InstallLocation = location,
        ExePath = exe,
        Source = source,
        Id = id,
        RawName = raw,
    };

    internal static OwnerMap Build(Classification c, IReadOnlyList<InstalledApp> apps, Dictionary<string, ExeIdentity>? identities = null) =>
        OwnerMap.Build(c, new InventorySnapshot(apps, TimeSpan.Zero, []), new OwnerInferenceOptions
        {
            ReadIdentity = p => identities is not null && identities.TryGetValue(p, out var id) ? id : null,
            ResolveIndirect = _ => null,
        });

    internal static Classification ClassifyWith(NodeTree tree, IReadOnlyList<AppInfo> apps, params LocationRule[] rules) =>
        new RuleEngine(new RuleSet(apps, rules), PathContext.Default).Classify(tree);

    private static int N(Classification c, string path) => c.Tree.FindPath(path);

    [Fact]
    public void RuleAppOwnsTheWholeSubtree()
    {
        var tree = Tree(($@"{Local}\uv\cache\wheels\a.whl", 100));
        var c = ClassifyWith(tree, [new AppInfo("uv", "uv", "Astral", ["uv.exe"], [])], Rule("dev.uv.cache", MatchMode.Dir, @"%LOCALAPPDATA%\uv\cache") with { AppId = "uv" });
        var owners = Build(c, []);

        var cache = N(c, $@"{Local}\uv\cache");
        Assert.Equal("uv（Astral）", owners.OwnerOf(cache)?.Display);
        Assert.Equal(OwnerSource.Rule, owners.OwnerOf(cache)?.Source);
        Assert.Equal(cache, owners.Resolve(N(c, $@"{Local}\uv\cache\wheels\a.whl"))?.Root);
        Assert.True(owners.IsOwnerRoot(cache));
    }

    [Fact]
    public void InstallLocationInsideTheDirGivesInferredOwner()
    {
        var tree = Tree(
            ($@"{Local}\Perplexity\Comet\Application\comet.exe", 10 << 20),
            ($@"{Local}\Perplexity\Comet\User Data\Default\Cache\data_1", 50),
            ($@"{Local}\Perplexity\Comet\User Data\Default\Preferences", 1));
        var c = Classify(tree, Rule("gen.cache", MatchMode.Dir, @"%LOCALAPPDATA%\**\Cache"));
        var owners = Build(c, [App("Perplexity Comet", "Perplexity AI, Inc.", $@"{Local}\Perplexity\Comet\Application")]);

        var dir = N(c, $@"{Local}\Perplexity");
        var owner = owners.OwnerOf(dir);
        Assert.Equal("Perplexity Comet", owner?.Name);
        Assert.Equal("Perplexity AI", owner?.Vendor);
        Assert.Equal(OwnerSource.Inferred, owner?.Source);
        Assert.Equal(OwnerEvidenceKind.InstalledApp, owner?.Evidence.Single().Kind);
        Assert.True(owners.IsOwnerRoot(dir));

        // 通用规则命中的缓存也归这个应用（而不是"多个程序"），归属在上级确定
        var cache = N(c, $@"{Local}\Perplexity\Comet\User Data\Default\Cache");
        Assert.Equal("Perplexity Comet", owners.OwnerOf(cache)?.Name);
        Assert.Equal(dir, owners.Resolve(cache)?.Root);
    }

    [Fact]
    public void DirNameMatchesMainExeName()
    {
        var tree = Tree(
            ($@"{Roaming}\Code\User\settings.json", 10),
            ($@"{Local}\Programs\Microsoft VS Code\Code.exe", 100));
        var vscode = App("Microsoft Visual Studio Code", "Microsoft Corporation", $@"{Local}\Programs\Microsoft VS Code", $@"{Local}\Programs\Microsoft VS Code\Code.exe");
        var owners = Build(Classify(tree), [vscode]);
        var c = owners.Classification;

        var data = owners.OwnerOf(N(c, $@"{Roaming}\Code"));
        Assert.Equal("Microsoft Visual Studio Code", data?.Name);
        Assert.Equal(OwnerEvidenceKind.DirName, data?.Evidence.Single().Kind);
        Assert.Equal("Microsoft Visual Studio Code", owners.OwnerOf(N(c, $@"{Local}\Programs\Microsoft VS Code"))?.Name);
    }

    [Fact]
    public void VendorDirWithSeveralAppsIsRefinedOneLevelDown()
    {
        var tree = Tree(
            ($@"{Local}\JetBrains\Rider2024.1\log\a.log", 10),
            ($@"{Local}\JetBrains\Toolbox\x.dat", 10));
        var owners = Build(Classify(tree), [App("JetBrains Rider 2024.1", "JetBrains s.r.o."), App("PyCharm", "JetBrains s.r.o.")]);
        var c = owners.Classification;

        var vendor = owners.OwnerOf(N(c, $@"{Local}\JetBrains"));
        Assert.True(vendor?.Several);
        Assert.Equal("JetBrains（多个应用）", vendor?.Display);
        Assert.Equal("JetBrains Rider 2024.1", owners.OwnerOf(N(c, $@"{Local}\JetBrains\Rider2024.1"))?.Name);

        // 认不出的二级目录沿用厂商
        Assert.Equal(N(c, $@"{Local}\JetBrains"), owners.Resolve(N(c, $@"{Local}\JetBrains\Toolbox\x.dat"))?.Root);
    }

    [Fact]
    public void StorePackagesMatchByFamilyName()
    {
        var tree = Tree(
            ($@"{Local}\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json", 10),
            (@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.23.0.0_x64__8wekyb3d8bbwe\wt.exe", 100));
        var owners = Build(Classify(tree), [App("Windows Terminal", source: AppSource.Store, id: "Microsoft.WindowsTerminal_8wekyb3d8bbwe")]);
        var c = owners.Classification;

        var data = owners.OwnerOf(N(c, $@"{Local}\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe"));
        Assert.Equal("Windows Terminal", data?.Name);
        Assert.Equal(OwnerEvidenceKind.StoreApp, data?.Evidence.Single().Kind);
        Assert.Equal("Windows Terminal", owners.OwnerOf(N(c, @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.23.0.0_x64__8wekyb3d8bbwe\wt.exe"))?.Name);
    }

    [Fact]
    public void ProgramVersionInfoNamesUnknownDirs()
    {
        var exe = $@"{Local}\FooTool\bin\foo.exe";
        var tree = Tree((exe, 2 << 20), ($@"{Local}\FooTool\data.bin", 10 << 20));
        var owners = Build(Classify(tree), [], new() { [exe] = new ExeIdentity(exe, "Foo Studio", "Foo", null, "1.0") });

        var owner = owners.OwnerOf(N(owners.Classification, $@"{Local}\FooTool"));
        Assert.Equal("Foo Studio", owner?.Display);
        var evidence = owner!.Evidence.Single();
        Assert.Equal(OwnerEvidenceKind.Program, evidence.Kind);
        Assert.Equal(exe, evidence.ExePath);
        Assert.Contains(@"bin\foo.exe：产品名 Foo Studio", evidence.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsFromDifferentVendorsAreNamedOneByOne()
    {
        var claude = @"C:\Users\demo\.local\bin\claude.exe";
        var uv = @"C:\Users\demo\.local\bin\uv.exe";
        var tree = Tree((claude, 5 << 20), (uv, 5 << 20));
        var apps = new[]
        {
            App("claude", "Anthropic", @"C:\Users\demo\.local\bin", claude, AppSource.CommandLine, raw: "Claude Code"),
            App("uv", null, @"C:\Users\demo\.local\bin", uv, AppSource.CommandLine),
        };
        var owners = Build(Classify(tree), apps, new() { [claude] = new ExeIdentity(claude, "Claude Code", "Anthropic", null, null) });
        var c = owners.Classification;

        Assert.Null(owners.OwnerOf(N(c, @"C:\Users\demo\.local")));
        Assert.Equal("Claude Code", owners.OwnerOf(N(c, claude))?.Name);
        Assert.True(owners.IsOwnerRoot(N(c, claude)));
        Assert.Null(owners.OwnerOf(N(c, uv)));
    }

    [Fact]
    public void SystemRulesAreNotOverriddenByInference()
    {
        var exe = @"C:\ProgramData\Microsoft\Windows Defender\Platform\MsMpEng.exe";
        var tree = Tree((exe, 5 << 20));
        var c = Classify(tree, Rule("sys.defender", MatchMode.Under, @"%PROGRAMDATA%\Microsoft\Windows Defender", Risk.Never, Kind.System));
        var owners = Build(c, [App("Microsoft Defender", "Microsoft Corporation", @"C:\ProgramData\Microsoft\Windows Defender\Platform", exe, AppSource.Process)],
            new() { [exe] = new ExeIdentity(exe, "Microsoft Defender", "Microsoft", null, null) });

        var owner = owners.OwnerOf(N(c, exe));
        Assert.Equal("Windows", owner?.Name);
        Assert.Equal(OwnerSource.Rule, owner?.Source);
    }

    [Fact]
    public void GenericRulesFallBackToGenericOwners()
    {
        var tree = Tree(($@"{Local}\Temp\a.tmp", 10), (@"C:\Users\demo\src\app\node_modules\x\index.js", 10));
        var c = Classify(tree,
            Rule("gen.user-temp", MatchMode.Dir, @"%LOCALAPPDATA%\Temp", Risk.Safe, Kind.Temp),
            Rule("gen.node-modules", MatchMode.Dir, @"%USERPROFILE%\**\node_modules", Risk.Low, Kind.BuildOutput));
        var owners = Build(c, []);

        Assert.Equal("多个程序", owners.OwnerOf(N(c, $@"{Local}\Temp"))?.Name);
        Assert.Equal("你的项目", owners.OwnerOf(N(c, @"C:\Users\demo\src\app\node_modules"))?.Name);
    }

    [Fact]
    public void ProgramsInsideRuleAppLocationsDoNotNameTheParent()
    {
        var python = $@"{Roaming}\uv\python\cpython-3.12\python.exe";
        var tree = Tree((python, 5 << 20), ($@"{Roaming}\uv\settings.toml", 1));
        var c = ClassifyWith(tree, [new AppInfo("uv", "uv", "Astral", [], [])],
            Rule("dev.uv.python", MatchMode.Dir, @"%APPDATA%\uv\python", Risk.Low, Kind.Program) with { AppId = "uv" });
        var owners = Build(c, [App("Python", "Python Software Foundation", Path.GetDirectoryName(python), python, AppSource.Process)],
            new() { [python] = new ExeIdentity(python, "Python", "Python Software Foundation", null, null) });

        // 外面一层按目录名归规则里的应用 uv，而不是归里面运行的 Python
        var parent = owners.OwnerOf(N(c, $@"{Roaming}\uv"));
        Assert.Equal("uv", parent?.Name);
        Assert.Equal(OwnerSource.Inferred, parent?.Source);
        Assert.Equal(OwnerEvidenceKind.DirName, parent?.Evidence.Single().Kind);
        Assert.Equal("uv", owners.OwnerOf(N(c, python))?.Name);
        Assert.Equal(OwnerSource.Rule, owners.OwnerOf(N(c, python))?.Source);
    }

    [Fact]
    public void GenericAppNamesDoNotClaimDirsByPrefix()
    {
        var tree = Tree((@"C:\Program Files (x86)\Windows Kits\10\x.txt", 10), (@"C:\Program Files (x86)\Rider Tools\y.txt", 10));
        var owners = Build(Classify(tree), [App("Windows", "Microsoft", source: AppSource.Process), App("Rider", "JetBrains")]);
        var c = owners.Classification;

        Assert.Null(owners.OwnerOf(N(c, @"C:\Program Files (x86)\Windows Kits")));
        Assert.Equal("Rider", owners.OwnerOf(N(c, @"C:\Program Files (x86)\Rider Tools"))?.Name); // 普通的软件名照常按开头匹配
    }

    [Fact]
    public void DotDirsNamedAfterRuleAppsBelongToThem()
    {
        var tree = Tree((@"C:\Users\demo\.gradle\caches\x.jar", 10), (@"C:\Users\demo\.gradle\gradle.properties", 1));
        var c = ClassifyWith(tree, [new AppInfo("gradle", "Gradle", null, [], [])],
            Rule("dev.gradle.caches", MatchMode.Dir, @"%USERPROFILE%\.gradle\caches", Risk.Low, Kind.PackageCache) with { AppId = "gradle" });
        var owners = Build(c, []);

        Assert.Equal("Gradle", owners.OwnerOf(N(c, @"C:\Users\demo\.gradle\gradle.properties"))?.Name);
        Assert.Equal(N(c, @"C:\Users\demo\.gradle"), owners.Resolve(N(c, @"C:\Users\demo\.gradle\gradle.properties"))?.Root);
    }

    [Fact]
    public void RuleAppEvidenceFindsProgramsOnThisMachine()
    {
        var tree = Tree(($@"{Local}\uv\cache\a.whl", 100));
        var c = ClassifyWith(tree, [new AppInfo("uv", "uv", "Astral", ["uv.exe"], [])], Rule("dev.uv.cache", MatchMode.Dir, @"%LOCALAPPDATA%\uv\cache") with { AppId = "uv" });
        var owners = Build(c, [App("uv", null, @"C:\Users\demo\.local\bin", @"C:\Users\demo\.local\bin\uv.exe", AppSource.CommandLine)]);

        var evidence = owners.AppEvidence("uv").Single();
        Assert.Equal(OwnerEvidenceKind.CommandLine, evidence.Kind);
        Assert.StartsWith("uv.exe 位于", evidence.Text, StringComparison.Ordinal);
    }
}
