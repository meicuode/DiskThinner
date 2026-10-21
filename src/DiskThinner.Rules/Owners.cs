using System.Diagnostics;
using DiskThinner.Inventory;
using DiskThinner.Scanner;

namespace DiskThinner.Rules;

/// <summary>归属从哪来（原型的来源标签）：规则 = 内置规则写明的应用；推断 = 按已装软件、目录名和程序签名推断。</summary>
public enum OwnerSource
{
    Rule,
    Inferred,
}

public enum OwnerEvidenceKind
{
    /// <summary>注册表卸载项里的已装软件。</summary>
    InstalledApp,

    /// <summary>商店应用。</summary>
    StoreApp,

    /// <summary>PATH 里的命令行工具。</summary>
    CommandLine,

    /// <summary>正在运行的进程。</summary>
    Process,

    /// <summary>程序文件自己的版本信息（产品名、公司名）。</summary>
    Program,

    /// <summary>目录名和软件名一致。</summary>
    DirName,
}

/// <summary>一条归属证据。<see cref="ExePath"/> 不为空时界面可以再去查这个程序的签名。</summary>
public sealed record OwnerEvidence(OwnerEvidenceKind Kind, string Text, string? ExePath = null)
{
    public string KindText => Kind switch
    {
        OwnerEvidenceKind.InstalledApp => "已装软件",
        OwnerEvidenceKind.StoreApp => "商店应用",
        OwnerEvidenceKind.CommandLine => "命令行工具",
        OwnerEvidenceKind.Process => "运行中",
        OwnerEvidenceKind.Program => "程序",
        _ => "目录名",
    };
}

/// <summary>一个目录或文件归哪个应用。</summary>
public sealed record Owner(string Name, string? Vendor, OwnerSource Source, string Key, IReadOnlyList<OwnerEvidence> Evidence)
{
    /// <summary>同一厂商的多个应用共用的目录（如 %LOCALAPPDATA%\Microsoft），只能说到厂商。</summary>
    public bool Several { get; init; }

    /// <summary>界面上显示的文字："uv（Astral）"、"Microsoft（多个应用）"。</summary>
    public string Display => Several
        ? $"{Name}（多个应用）"
        : Vendor is null || Name.Contains(Vendor, StringComparison.OrdinalIgnoreCase) ? Name : $"{Name}（{Vendor}）";

    /// <summary>归属汇总时用的键：同名的应用（不管来自规则还是推断）合并到一起。</summary>
    internal static string NameKey(string name) => "n:" + AppNames.Key(name);
}

/// <summary>归属推断的可替换部分（测试里换成假的，不读真实文件）。</summary>
public sealed class OwnerInferenceOptions
{
    /// <summary>读取程序文件的版本信息。</summary>
    public Func<string, ExeIdentity?> ReadIdentity { get; init; } = ExeIdentityReader.Read;

    /// <summary>解析商店应用的间接显示名。</summary>
    public Func<string?, string?> ResolveIndirect { get; init; } = InventoryCollector.ResolveIndirectName;

    /// <summary>目录至少这么大才去读里面程序的版本信息。</summary>
    public long MinBytesForProgramProbe { get; init; } = 1L << 20;

    /// <summary>没有归属的 exe 文件至少这么大才单独读它的版本信息。</summary>
    public long MinBytesForFileProbe { get; init; } = 4L << 20;
}

/// <summary>
/// 归属引擎（研发方案 3.3 节）：每个节点归哪个应用。
///
/// 优先级：规则写明了应用 → 系统和用户文件规则（Windows、你自己的文件）→ 推断 → 通用规则的兜底文字（多个程序）。
/// 推断只在几类"应用目录"位置做：%LOCALAPPDATA%、%APPDATA%、%LOCALLOW% 下的一、二级目录，Programs 和 Packages 下的目录，
/// 用户目录下的点目录（.ollama 等），ProgramData、Program Files 下的一、二级目录。依据依次是：
/// 商店应用的包族名、已装软件的安装位置和正在运行的程序在不在这个目录里、目录名和软件名（或主程序名）一致、目录里程序文件的产品名。
/// 都对不上的大 exe 文件单独读它自己的版本信息。推断出的归属由更深处的位置覆盖。
/// </summary>
public sealed class OwnerMap
{
    private readonly Classification _c;
    private readonly Dictionary<int, Owner> _inferred;
    private readonly Owner?[] _ruleOwners;
    private readonly bool[] _ruleIsFixed;
    private readonly InventorySnapshot _inventory;
    private readonly Dictionary<string, IReadOnlyList<OwnerEvidence>> _appEvidence = [];

    private OwnerMap(Classification c, InventorySnapshot inventory, Dictionary<int, Owner> inferred, TimeSpan duration)
    {
        _c = c;
        _inventory = inventory;
        _inferred = inferred;
        Duration = duration;
        var rules = c.Engine.Rules;
        _ruleOwners = new Owner?[rules.Locations.Count];
        _ruleIsFixed = new bool[rules.Locations.Count];
        for (var i = 0; i < rules.Locations.Count; i++)
        {
            (_ruleOwners[i], _ruleIsFixed[i]) = RuleOwner(rules, rules.Locations[i]);
        }
    }

    public Classification Classification => _c;

    public InventorySnapshot Inventory => _inventory;

    public TimeSpan Duration { get; }

    /// <summary>推断出归属的位置数。</summary>
    public int InferredCount => _inferred.Count;

    public IReadOnlyDictionary<int, Owner> InferredRoots => _inferred;

    /// <summary>只有规则、没有已装软件信息时的归属（测试和规则没加载 Inventory 时用）。</summary>
    public static OwnerMap RulesOnly(Classification c) => new(c, InventorySnapshot.Empty, [], TimeSpan.Zero);

    /// <summary>节点的归属；不知道时返回 null。</summary>
    public Owner? OwnerOf(int node) => Resolve(node)?.Owner;

    /// <summary>节点的归属和"归属是在哪个节点上确定的"（规则命中的位置或推断的位置）。</summary>
    public (Owner Owner, int Root)? Resolve(int node)
    {
        if (node < 0)
        {
            return null;
        }

        var tree = _c.Tree;
        var ownRule = _c.RuleIndexOf(node);
        if (ownRule >= 0 && RuleOwnerIsFixed(ownRule))
        {
            return (_ruleOwners[ownRule]!, _c.RootOf(node)!.Node); // 系统和用户文件规则：不让推断覆盖
        }

        for (var n = node; n >= 0; n = n == tree.Root ? -1 : tree[n].Parent)
        {
            if (_c.IsRoot(n) && _c.RuleOf(n) is { AppId: not null } && _ruleOwners[_c.RuleIndexOf(n)] is { } appOwner)
            {
                return (appOwner, n);
            }

            if (_inferred.TryGetValue(n, out var inferred))
            {
                return (inferred, n);
            }
        }

        return ownRule >= 0 && _ruleOwners[ownRule] is { } fallback ? (fallback, _c.RootOf(node)!.Node) : null;
    }

    /// <summary>规则给出的归属（写明的应用、系统位置、用户文件或通用规则的兜底文字）。</summary>
    internal Owner? RuleOwnerAt(int rule) => _ruleOwners[rule];

    /// <summary>这条规则的归属是否优先于推断：系统位置和用户文件（不含写明应用的规则，它们按位置就近比较）。</summary>
    internal bool RuleOwnerIsFixed(int rule) => _ruleIsFixed[rule] && _ruleOwners[rule] is not null && _c.Engine.Rules.Locations[rule].AppId is null;

    /// <summary>归属是不是在这个节点上确定的（界面只在这里画来源标签，子项用浅色）。</summary>
    public bool IsOwnerRoot(int node) => Resolve(node) is { } r && r.Root == node;

    /// <summary>规则写明的应用在本机的证据：它的程序装在哪里、是否在运行（详情面板用）。</summary>
    public IReadOnlyList<OwnerEvidence> AppEvidence(string appId)
    {
        lock (_appEvidence)
        {
            if (_appEvidence.TryGetValue(appId, out var cached))
            {
                return cached;
            }

            var list = new List<OwnerEvidence>();
            if (_c.Engine.Rules.App(appId) is { DetectExes.Count: > 0 } app)
            {
                var exes = app.DetectExes.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var found in _inventory.Apps.Where(a => a.ExePath is { } p && exes.Contains(Path.GetFileName(p)))
                             .DistinctBy(a => (a.ExePath, a.Source)).Take(3))
                {
                    var where = PathDisplay.Shorten(Path.GetDirectoryName(found.ExePath!) ?? "");
                    var product = found.RawName is { } raw && !raw.StartsWith('@') && raw != found.Name ? $"，产品名 {raw}" : "";
                    list.Add(found.Source switch
                    {
                        AppSource.Process => new OwnerEvidence(OwnerEvidenceKind.Process, $"{Path.GetFileName(found.ExePath)} 正在运行，位于 {where}{product}", found.ExePath),
                        AppSource.Registry => new OwnerEvidence(OwnerEvidenceKind.InstalledApp, $"已安装 {found.RawName ?? found.Name}{(found.Version is null ? "" : " " + found.Version)}，程序位于 {where}", found.ExePath),
                        _ => new OwnerEvidence(OwnerEvidenceKind.CommandLine, $"{Path.GetFileName(found.ExePath)} 位于 {where}{product}", found.ExePath),
                    });
                }
            }

            _appEvidence[appId] = list;
            return list;
        }
    }

    /// <summary>规则给出的归属；第二个值表示它是否优先于推断（系统位置和用户文件）。</summary>
    private static (Owner?, bool) RuleOwner(RuleSet rules, LocationRule rule)
    {
        if (rules.App(rule.AppId) is { } app)
        {
            return (new Owner(app.Name, app.Vendor, OwnerSource.Rule, Owner.NameKey(app.Name), []), true);
        }

        if (rule.Id.StartsWith("sys.", StringComparison.Ordinal))
        {
            return rule.Kind == Kind.Program
                ? (new Owner("已安装的程序", null, OwnerSource.Rule, "f:programs", []), false)
                : (new Owner("Windows", null, OwnerSource.Rule, Owner.NameKey("Windows"), []), true); // 和按程序版本信息推断出的 Windows 合并
        }

        if (rule.Id.StartsWith("user.", StringComparison.Ordinal))
        {
            return (new Owner("你自己的文件", null, OwnerSource.Rule, "f:user", []), true);
        }

        return rule.Kind == Kind.BuildOutput
            ? (new Owner("你的项目", null, OwnerSource.Rule, "f:projects", []), false)
            : (new Owner("多个程序", null, OwnerSource.Rule, "f:several", []), false);
    }

    // =====================================================================
    // 推断
    // =====================================================================

    public static OwnerMap Build(Classification c, InventorySnapshot inventory, OwnerInferenceOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        options ??= new OwnerInferenceOptions();
        var inferrer = new Inferrer(c, inventory, options);
        var inferred = inferrer.Run(cancellationToken);
        return new OwnerMap(c, inventory, inferred, sw.Elapsed);
    }

    private enum Slot
    {
        /// <summary>%LOCALAPPDATA%\X 这类：X 可能是厂商，也可能是软件。</summary>
        Top,

        /// <summary>%LOCALAPPDATA%\X\Y：Y 通常是厂商下面的软件。</summary>
        Second,

        /// <summary>%LOCALAPPDATA%\Packages\包族名。</summary>
        Store,

        /// <summary>%USERPROFILE%\.ollama 这类点目录。</summary>
        DotDir,
    }

    /// <summary>不当作软件名的常见目录名（比较时用 <see cref="AppNames.Key"/>）。</summary>
    private static readonly HashSet<string> GenericNames =
    [
        "microsoft", "windows", "packages", "programs", "temp", "tmp", "cache", "caches", "log", "logs", "data", "config", "bin", "lib",
        "tools", "shared", "common", "commonfiles", "update", "updates", "crashdumps", "crashpad", "crashreports", "default", "local",
        "roaming", "locallow", "system", "appdata", "application", "applications", "app", "apps", "users", "user", "documents",
        "downloads", "desktop", "backup", "backups", "setup", "installer", "install", "plugins", "extensions", "profiles", "settings",
        "storage", "files", "programdata", "packagecache", "net", "dotnet", "sdk", "runtime", "share", "store", "service", "services",
        "client", "server", "host", "agent", "helper", "web", "home", "global", "machine", "vendor", "resources", "assets", "models",
        "history", "state", "db", "database", "keys", "certs", "fonts", "icons", "images", "media", "music", "video", "videos",
    ];

    private sealed class Inferrer
    {
        private readonly Classification _c;
        private readonly NodeTree _tree;
        private readonly OwnerInferenceOptions _options;
        private readonly List<AppEntry> _apps = [];
        private readonly Dictionary<string, List<AppEntry>> _under = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AppEntry> _store = new(StringComparer.OrdinalIgnoreCase);
        private readonly int _windowsNode;

        public Inferrer(Classification c, InventorySnapshot inventory, OwnerInferenceOptions options)
        {
            _c = c;
            _tree = c.Tree;
            _options = options;
            _windowsNode = _tree.FindPath(c.Engine.Context.WindowsDir);
            foreach (var app in inventory.Apps)
            {
                var entry = new AppEntry(app);
                _apps.Add(entry);
                if (app.Source == AppSource.Store && app.Id is { } family)
                {
                    _store.TryAdd(family, entry);
                    continue; // 商店应用的程序装在 WindowsApps 里，只按包族名对应数据目录
                }

                if (app.InstallLocation is { } location)
                {
                    // 安装位置的每一级上级目录都记一下，查"装在这个目录里的软件"时直接取
                    for (var p = location; p.Length > 3; p = Path.GetDirectoryName(p) ?? "")
                    {
                        if (!_under.TryGetValue(p, out var list))
                        {
                            _under[p] = list = [];
                        }

                        list.Add(entry);
                    }
                }
            }
        }

        public Dictionary<int, Owner> Run(CancellationToken ct)
        {
            var result = new Dictionary<int, Owner>();
            var slots = FindCandidates();
            var ruleApp = new bool[_c.Engine.Rules.Locations.Count];
            for (var i = 0; i < ruleApp.Length; i++)
            {
                ruleApp[i] = _c.Engine.Rules.Locations[i].AppId is not null;
            }

            // 自上而下走一遍：记下上面是否已经有确定的归属（规则写明的应用或推断出的具体应用），没有时才单独看大 exe
            var stack = new Stack<(int Node, int Cover)>();
            stack.Push((_tree.Root, 0));
            var visited = 0;
            while (stack.Count > 0)
            {
                var (n, cover) = stack.Pop();
                if ((++visited & 0xFFFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                ref var node = ref _tree[n];
                if (_c.IsRoot(n) && _c.RuleIndexOf(n) is var r && r >= 0 && ruleApp[r])
                {
                    continue; // 规则写明了应用，整棵子树都归它，不用再推断
                }

                if (cover < 2 && node.IsDirectory && slots.TryGetValue(n, out var slot) && Infer(n, slot) is { } owner)
                {
                    result[n] = owner;
                    cover = owner.Several ? Math.Max(cover, 1) : 2;
                }
                else if (cover < 2 && !node.IsDirectory && node.AllocSize >= _options.MinBytesForFileProbe && IsProgramFile(_tree.GetName(n))
                         && FromFileIdentity(n) is { } fileOwner)
                {
                    result[n] = fileOwner;
                }

                // 已经推断出具体应用的子树不再往下找；Windows 目录归系统规则
                if (!node.IsDirectory || cover >= 2 || n == _windowsNode)
                {
                    continue;
                }

                for (var ch = node.FirstChild; ch >= 0; ch = _tree[ch].NextSibling)
                {
                    if (_tree[ch].IsDirectory || _tree[ch].AllocSize >= _options.MinBytesForFileProbe)
                    {
                        stack.Push((ch, cover));
                    }
                }
            }

            return result;
        }

        private static bool IsProgramFile(ReadOnlySpan<char> name) =>
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.Contains(".exe.", StringComparison.OrdinalIgnoreCase);

        /// <summary>找出所有"应用目录"位置。</summary>
        private Dictionary<int, Slot> FindCandidates()
        {
            var slots = new Dictionary<int, Slot>();
            var ctx = _c.Engine.Context;

            void TopAndSecond(int container, bool second = true)
            {
                if (container < 0)
                {
                    return;
                }

                foreach (var x in _tree.Children(container))
                {
                    if (!_tree[x].IsDirectory)
                    {
                        continue;
                    }

                    slots.TryAdd(x, Slot.Top);
                    if (!second || GenericNames.Contains(AppNames.Key(_tree.GetName(x))) && !IsVendorContainer(x))
                    {
                        continue;
                    }

                    foreach (var y in _tree.Children(x))
                    {
                        if (_tree[y].IsDirectory)
                        {
                            slots.TryAdd(y, Slot.Second);
                        }
                    }
                }
            }

            // 扫描整个盘或 C:\Users 时逐个用户目录看；只扫描了一个用户目录时，根就是这个用户目录
            var profiles = _tree.FindPath(ctx.ProfilesRoot);
            var rootPath = _tree.GetPath(_tree.Root).TrimEnd('\\');
            IEnumerable<int> profileNodes = profiles >= 0 ? _tree.Children(profiles)
                : string.Equals(Path.GetDirectoryName(rootPath), ctx.ProfilesRoot, StringComparison.OrdinalIgnoreCase) ? [_tree.Root]
                : [];
            {
                foreach (var profile in profileNodes)
                {
                    if (!_tree[profile].IsDirectory)
                    {
                        continue;
                    }

                    var appData = _tree.FindChild(profile, "AppData");
                    var local = appData < 0 ? -1 : _tree.FindChild(appData, "Local");
                    var programs = local < 0 ? -1 : _tree.FindChild(local, "Programs");
                    var packages = local < 0 ? -1 : _tree.FindChild(local, "Packages");
                    TopAndSecond(local);
                    TopAndSecond(appData < 0 ? -1 : _tree.FindChild(appData, "Roaming"));
                    TopAndSecond(appData < 0 ? -1 : _tree.FindChild(appData, "LocalLow"));
                    if (programs >= 0)
                    {
                        slots.Remove(programs);
                        TopAndSecond(programs, second: false);
                    }

                    if (packages >= 0)
                    {
                        slots.Remove(packages);
                        foreach (var p in _tree.Children(packages))
                        {
                            slots[p] = Slot.Store;
                        }
                    }

                    foreach (var dot in _tree.Children(profile))
                    {
                        if (_tree[dot].IsDirectory && _tree.GetName(dot) is [ '.', _, _, .. ])
                        {
                            slots.TryAdd(dot, Slot.DotDir);
                        }
                    }

                    var scoop = _tree.FindChild(profile, "scoop");
                    var scoopApps = scoop < 0 ? -1 : _tree.FindChild(scoop, "apps");
                    TopAndSecond(scoopApps, second: false);
                }
            }

            TopAndSecond(_tree.FindPath(ctx.ProgramData));
            var programFiles = _tree.FindPath(ctx.ProgramFiles);
            TopAndSecond(programFiles);
            TopAndSecond(_tree.FindPath(ctx.ProgramFilesX86));

            // 商店应用的程序目录：Program Files\WindowsApps\<包全名>，按包族名对应
            var windowsApps = programFiles < 0 ? -1 : _tree.FindChild(programFiles, "WindowsApps");
            if (windowsApps >= 0)
            {
                slots.Remove(windowsApps);
                foreach (var p in _tree.Children(windowsApps))
                {
                    slots[p] = Slot.Store;
                }
            }

            return slots;
        }

        /// <summary>Microsoft、Google 这类目录名虽然在通用名单里，下面的二级目录（Edge、Chrome）仍然值得看。</summary>
        private bool IsVendorContainer(int dir) => AppNames.Key(_tree.GetName(dir)) is "microsoft" or "windows" && _tree.Children(dir).Any();

        private Owner? Infer(int node, Slot slot)
        {
            var name = _tree.GetNameString(node);
            if (slot == Slot.Store)
            {
                return FromStore(name);
            }

            var path = _tree.GetPath(node).TrimEnd('\\');
            return FromLocation(path, slot)
                ?? FromName(node, name, slot)
                ?? FromRuleApp(name, slot)
                ?? (_tree[node].AllocSize >= _options.MinBytesForProgramProbe ? FromPrograms(node) : null);
        }

        /// <summary>
        /// 目录名和内置规则里的应用同名（%LOCALAPPDATA%\uv、%USERPROFILE%\.gradle）：规则只认领了里面的缓存目录，
        /// 外面这一层也归这个应用。规则应用的名字是人工写的，短名字（uv、go）也可靠，所以不受长度限制。
        /// </summary>
        private Owner? FromRuleApp(string name, Slot slot)
        {
            var key = AppNames.Key(slot == Slot.DotDir ? name.TrimStart('.') : name);
            if (key.Length < 2 || GenericNames.Contains(key))
            {
                return null;
            }

            var apps = _c.Engine.Rules.Apps.Where(a => AppNames.Key(a.Name) == key || AppNames.Key(a.Id) == key).ToList();
            if (apps.Count != 1)
            {
                return null;
            }

            var app = apps[0];
            return new Owner(app.Name, app.Vendor, OwnerSource.Inferred, Owner.NameKey(app.Name),
                [new OwnerEvidence(OwnerEvidenceKind.DirName, $"目录名 {name} 和内置规则里的应用 {app.Name} 一致")]);
        }

        private Owner? FromStore(string dirName)
        {
            // Packages 下是包族名（名称_发布者），WindowsApps 下是包全名（名称_版本_架构_资源_发布者）
            var parts = dirName.Split('_');
            var family = parts.Length == 5 ? parts[0] + "_" + parts[4] : dirName;
            if (!_store.TryGetValue(family, out var entry))
            {
                return null;
            }

            var app = entry.App;
            var name = app.RawName is { } raw && raw.StartsWith('@') ? _options.ResolveIndirect(raw) ?? app.Name : app.Name;
            return new Owner(name, app.Publisher, OwnerSource.Inferred, Owner.NameKey(name),
                [new OwnerEvidence(OwnerEvidenceKind.StoreApp, $"已注册的商店应用 {name}（包 {family}）")]);
        }

        /// <summary>已装软件的安装位置、正在运行的程序在这个目录里。</summary>
        private Owner? FromLocation(string path, Slot slot)
        {
            if (!_under.TryGetValue(path, out var located))
            {
                return null;
            }

            // 装在规则写明了应用的位置里的程序不算（%APPDATA%\uv\python 里运行的 python.exe 不能让整个 uv 目录归 Python）
            located = located.Where(e => !InsideRuleApp(e.App.InstallLocation!)).ToList();
            if (located.Count == 0)
            {
                return null;
            }

            // 安装位置正好是这个目录的优先；否则看装在里面的那些
            var exact = located.Where(e => string.Equals(e.App.InstallLocation, path, StringComparison.OrdinalIgnoreCase)).ToList();
            var pool = exact.Count > 0 ? exact : located;
            var products = Products(pool);
            if (products.Count == 1)
            {
                var e = products[0];
                return new Owner(e.DisplayName, e.App.Publisher, OwnerSource.Inferred, Owner.NameKey(e.DisplayName), [LocationEvidence(e, path)]);
            }

            var vendors = products.Select(p => p.PublisherKey).Distinct().ToList();
            if (slot is Slot.Top or Slot.DotDir && vendors is [{ Length: > 0 }] && products[0].App.Publisher is { } vendor)
            {
                var evidence = products.Take(3).Select(p => LocationEvidence(p, path)).ToList();
                return new Owner(vendor, null, OwnerSource.Inferred, "v:" + vendors[0], evidence) { Several = true };
            }

            return null; // 多个不同厂商的软件装在一起（例如 ~\.local\bin），这一级不下结论
        }

        private bool InsideRuleApp(string location)
        {
            for (var n = _tree.FindPath(location); n >= 0; n = n == _tree.Root ? -1 : _tree[n].Parent)
            {
                if (_c.IsRoot(n) && _c.RuleOf(n) is { AppId: not null })
                {
                    return true;
                }
            }

            return false;
        }

        private static OwnerEvidence LocationEvidence(AppEntry e, string dir)
        {
            var app = e.App;
            var where = PathDisplay.Shorten(app.InstallLocation ?? dir);
            return app.Source switch
            {
                AppSource.Process => new OwnerEvidence(OwnerEvidenceKind.Process, $"{Path.GetFileName(app.ExePath)} 正在运行，位于 {where}", app.ExePath),
                AppSource.CommandLine => new OwnerEvidence(OwnerEvidenceKind.CommandLine, $"{Path.GetFileName(app.ExePath)} 位于 {where}" + (app.RawName is { } p ? $"，产品名 {p}" : ""), app.ExePath),
                AppSource.Registry => new OwnerEvidence(OwnerEvidenceKind.InstalledApp, $"已安装 {app.RawName ?? app.Name}" + (app.Publisher is null ? "" : $"（{app.Publisher}）") + $"，安装位置 {where}", app.ExePath),
                _ => new OwnerEvidence(OwnerEvidenceKind.InstalledApp, $"{app.SourceText} {app.Name}，位于 {where}", app.ExePath),
            };
        }

        /// <summary>
        /// 把同一个软件的多条记录合并（注册表里的"Microsoft Visual Studio Code"和进程里的"Visual Studio Code"是同一个），
        /// 优先保留注册表的那条。
        /// </summary>
        private static List<AppEntry> Products(IEnumerable<AppEntry> entries)
        {
            var result = new List<AppEntry>();
            foreach (var e in entries.OrderBy(e => e.App.Source == AppSource.Registry ? 0 : e.App.Source == AppSource.Process ? 1 : 2))
            {
                if (!result.Any(r => r.SameProduct(e)))
                {
                    result.Add(e);
                }
            }

            return result;
        }

        /// <summary>目录名和软件名一致：完全相同、是软件名开头的几个词、或等于主程序名（%APPDATA%\Code 对应 Code.exe）。</summary>
        private Owner? FromName(int node, string name, Slot slot)
        {
            var bare = slot == Slot.DotDir ? name.TrimStart('.') : name;
            var tokens = AppNames.Tokens(bare);
            var key = string.Concat(tokens);
            if (key.Length < 3 || GenericNames.Contains(key))
            {
                return null;
            }

            List<string>? withParent = null;
            var parentTokens = 0;
            if (slot == Slot.Second && _tree[node].Parent is var parent && parent >= 0)
            {
                var pt = AppNames.Tokens(_tree.GetName(parent));
                parentTokens = pt.Count;
                withParent = [.. pt, .. tokens];
            }

            var best = 0;
            var matches = new List<AppEntry>();
            foreach (var e in _apps)
            {
                if (e.App.Source == AppSource.Store)
                {
                    continue;
                }

                var score = 0;
                // 反过来（目录名以软件名开头）时，软件名必须比上级目录名多出词来，避免只叫厂商名的软件认领厂商下的所有目录；
                // 软件名本身是通用词（系统程序的产品名就叫"Windows"）时也不这样比，否则"Windows Kits"会归 Windows
                var reverse = e.Key.Length >= 4 && !GenericNames.Contains(e.Key);
                if (withParent is not null && (AppNames.StartsWithTokens(e.Tokens, withParent)
                        || reverse && e.Tokens.Count > parentTokens && AppNames.StartsWithTokens(withParent, e.Tokens)))
                {
                    score = 4; // Google\Chrome → Google Chrome；JetBrains\Rider2024.1 → JetBrains Rider
                }
                else if (e.Key == key || e.ExeKey == key && e.App.Source != AppSource.CommandLine)
                {
                    score = 3;
                }
                else if (key.Length >= 4 && AppNames.StartsWithTokens(e.Tokens, tokens) || reverse && AppNames.StartsWithTokens(tokens, e.Tokens))
                {
                    score = 2; // Postman → Postman Agent；Rider2024.1 → Rider
                }
                else if (slot is Slot.Top or Slot.DotDir && e.PublisherKey == key)
                {
                    score = 1; // JetBrains → JetBrains s.r.o. 的软件
                }

                if (score > best)
                {
                    best = score;
                    matches.Clear();
                }

                if (score == best && score > 0)
                {
                    matches.Add(e);
                }
            }

            if (best == 0)
            {
                return null;
            }

            // 一级目录名就是某个厂商名、这个厂商又装了好几个软件（%LOCALAPPDATA%\Google、%LOCALLOW%\Intel）：
            // 只说到厂商，下面的二级目录再细分；否则 Google 会被整个算成 Google Chrome
            if (best <= 2 && slot is Slot.Top or Slot.DotDir)
            {
                var byVendor = Products(_apps.Where(e => e.App.Source != AppSource.Store && e.PublisherKey == key));
                if (byVendor.Count >= 2)
                {
                    matches = byVendor;
                    best = 1;
                }
            }

            var products = Products(matches);
            var dirText = PathDisplay.Shorten(_tree.GetPath(node));
            if (best == 1 || products.Count > 1)
            {
                // 只对上了厂商，或者对上了同一厂商的几个软件
                var vendors = products.Select(p => p.PublisherKey).Distinct().ToList();
                if (vendors is not [{ Length: > 0 }] || products[0].App.Publisher is not { } vendor)
                {
                    return null;
                }

                if (products.Count == 1)
                {
                    var only = products[0];
                    return new Owner(only.DisplayName, vendor, OwnerSource.Inferred, Owner.NameKey(only.DisplayName),
                        [new OwnerEvidence(OwnerEvidenceKind.DirName, $"目录名 {name} 是已装软件 {only.App.RawName ?? only.App.Name} 的厂商名", only.App.ExePath)]);
                }

                return new Owner(vendor, null, OwnerSource.Inferred, "v:" + vendors[0],
                    [new OwnerEvidence(OwnerEvidenceKind.DirName, $"目录名 {name} 对应 {vendor} 的 {products.Count} 个已装软件：{string.Join("、", products.Take(3).Select(p => p.DisplayName))}")])
                { Several = true };
            }

            var e1 = products[0];
            var how = e1.Key == key || best == 4 ? "和" : e1.ExeKey == key ? "和主程序名" : "是";
            var tail = how == "是" ? " 的开头" : " 一致";
            return new Owner(e1.DisplayName, e1.App.Publisher, OwnerSource.Inferred, Owner.NameKey(e1.DisplayName),
                [new OwnerEvidence(OwnerEvidenceKind.DirName, $"{dirText} 的目录名{how}{e1.App.SourceText} {e1.App.RawName ?? e1.App.Name}{tail}", e1.App.ExePath)]);
        }

        /// <summary>目录里（三层以内）最大的几个程序文件：产品名一致（或公司一致）时归它。</summary>
        private Owner? FromPrograms(int dir)
        {
            var exes = new List<int>();
            var queue = new Queue<(int Node, int Depth)>();
            queue.Enqueue((dir, 0));
            var visited = 0;
            while (queue.Count > 0 && visited++ < 3000)
            {
                var (n, depth) = queue.Dequeue();
                foreach (var ch in _tree.Children(n))
                {
                    if (_c.IsRoot(ch) && _c.RuleOf(ch) is { AppId: not null })
                    {
                        continue; // 规则写明了归谁的子目录里的程序不代表上级目录
                    }

                    if (_tree[ch].IsDirectory)
                    {
                        if (depth < 3)
                        {
                            queue.Enqueue((ch, depth + 1));
                        }
                    }
                    else if (_tree[ch].AllocSize > 0 && IsProgramFile(_tree.GetName(ch)))
                    {
                        exes.Add(ch);
                    }
                }
            }

            var top = exes.OrderByDescending(e => _tree[e].AllocSize).Take(3)
                .Select(e => (Node: e, Id: _options.ReadIdentity(_tree.GetPath(e))))
                .ToList();
            var identities = top.Where(x => x.Id?.DisplayName is not null).ToList();

            // 认出来的程序要占这几个程序的大部分（没有版本信息的 uv.exe 和 claude.exe 放在一起时，不能整个目录归 Claude）
            var knownBytes = identities.Sum(x => _tree[x.Node].AllocSize);
            if (identities.Count == 0 || knownBytes < top.Sum(x => _tree[x.Node].AllocSize) * 0.8)
            {
                return null;
            }

            var first = identities[0];
            var productKey = AppNames.Key(first.Id!.DisplayName!);
            var sameProduct = identities.All(x => AppNames.Key(x.Id!.DisplayName!) == productKey);
            var companyKey = first.Id.Company is { } co ? AppNames.Key(co) : "";
            var sameCompany = companyKey.Length > 0 && identities.All(x => x.Id!.Company is { } c && AppNames.Key(c) == companyKey);
            if (!sameProduct && !sameCompany)
            {
                return null;
            }

            var evidence = identities.Take(2).Select(x => ProgramEvidence(x.Node, x.Id!, dir)).ToList();
            if (sameProduct)
            {
                var name = AppNames.CleanDisplayName(first.Id.DisplayName!);
                return new Owner(name, first.Id.Company, OwnerSource.Inferred, Owner.NameKey(name), evidence);
            }

            return new Owner(first.Id.Company!, null, OwnerSource.Inferred, "v:" + companyKey, evidence) { Several = true };
        }

        private Owner? FromFileIdentity(int file)
        {
            var path = _tree.GetPath(file);
            if (_options.ReadIdentity(path) is not { DisplayName: { } display } id)
            {
                return null;
            }

            var name = AppNames.CleanDisplayName(display);
            return new Owner(name, id.Company, OwnerSource.Inferred, Owner.NameKey(name), [ProgramEvidence(file, id, -1)]);
        }

        private OwnerEvidence ProgramEvidence(int file, ExeIdentity id, int relativeTo)
        {
            var path = _tree.GetPath(file);
            var shown = relativeTo >= 0 ? Path.GetRelativePath(_tree.GetPath(relativeTo), path) : Path.GetFileName(path);
            var parts = new List<string>();
            if (id.Product is { } p)
            {
                parts.Add("产品名 " + p);
            }
            else if (id.Description is { } d)
            {
                parts.Add("说明 " + d);
            }

            if (id.Company is { } c)
            {
                parts.Add("公司 " + c);
            }

            return new OwnerEvidence(OwnerEvidenceKind.Program, $"{shown}：{string.Join("，", parts)}", path);
        }
    }

    private sealed class AppEntry
    {
        public AppEntry(InstalledApp app)
        {
            App = app;
            // 命令行工具和进程的"名称"是 exe 名，产品名更好认
            DisplayName = app.Source is AppSource.CommandLine or AppSource.Process && app.RawName is { } product ? AppNames.CleanDisplayName(product) : app.Name;
            Tokens = AppNames.Tokens(DisplayName);
            Key = string.Concat(Tokens);
            PublisherKey = app.Publisher is { } p ? AppNames.Key(p) : "";
            ExeKey = app.ExePath is { } exe ? AppNames.Key(Path.GetFileNameWithoutExtension(exe)) : "";
        }

        public InstalledApp App { get; }

        public string DisplayName { get; }

        public List<string> Tokens { get; }

        public string Key { get; }

        public string PublisherKey { get; }

        public string ExeKey { get; }

        /// <summary>同名，或者同一厂商且一个名字包含另一个（"Microsoft Visual Studio Code" 和 "Visual Studio Code"）。</summary>
        public bool SameProduct(AppEntry other)
        {
            if (Key == other.Key && Key.Length > 0)
            {
                return true;
            }

            var samePublisher = PublisherKey.Length > 0 && PublisherKey == other.PublisherKey;
            return samePublisher && Key.Length >= 3 && other.Key.Length >= 3 && (Key.Contains(other.Key, StringComparison.Ordinal) || other.Key.Contains(Key, StringComparison.Ordinal))
                || ExeKey.Length > 0 && ExeKey == other.ExeKey && (samePublisher || PublisherKey.Length == 0 || other.PublisherKey.Length == 0);
        }
    }
}
