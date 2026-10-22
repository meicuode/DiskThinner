# 测试 fixtures

用 JSON 描述目录树和期望结果（研发方案第 6 节）。匹配引擎和热点算法都用这里的数据测试，C# 版的运行器在
`tests/DiskThinner.Rules.Tests/FixtureTests.cs`（matching）和 `tests/DiskThinner.Storage.Tests/HotspotFixtureTests.cs`（hotspots）。
以后换成别的语言实现时，读同样的文件、做同样的检查即可。

## 共同约定

- 路径用正斜杠，从盘符开始：`C:/Users/demo/AppData/Local/Temp/x.tmp`。比较时不区分大小写。
- 大小可以是数字（字节），也可以是带单位的字符串：`"300M"`、`"-150M"`、`"4G"`、`"64K"`（1K = 1024 字节）。
- 目录树是一个列表，每项是：
  - `["路径", 大小]`：一个文件；
  - `["路径", 大小, ["标记", ...]]`：带标记的文件，标记有 `hard-linked`（有多个硬链接名）、`hard-link-secondary`（第二个及以后的硬链接名，只算一次时不计大小）、`metadata`（NTFS 元数据文件）、`reparse-point`；
  - `["路径/", 0]`：以 `/` 结尾的是空目录；
  - `{ "repeat": 300, "dir": "C:/…/Temp", "name": "f{i}.tmp", "size": "1M" }`：在一个目录里生成 300 个文件，`{i}` 换成 0、1、2……
  目录按需自动创建，大小由文件汇总。

## matching：规则匹配

```json
{
  "description": "说明",
  "tree": [ ... ],
  "cases": [
    {
      "name": "用例名",
      "rules": [ { "id": "uv", "dir": "%LOCALAPPDATA%/uv/cache", "kind": "package-cache", "risk": "low" } ],
      "expect": {
        "paths": { "C:/Users/demo/AppData/Local/uv/cache": { "rule": "uv", "root": true, "root_bytes": 4000 } },
        "totals": { "cleanable": 4000, "cleanable_by_risk": { "low": 4000 }, "roots": 1 }
      }
    }
  ]
}
```

规则的字段和 `rules/*.yaml` 一样：`id`；`dir` / `file` / `under` 三选一（路径令牌同规则文件）；`kind`、`risk`；可选 `tier`（默认 `reviewed`）、
`has_any`、`sibling_any`、`not_under`、`actions`（`[{ "delete": "contents" }]`、`[{ "guide": "说明" }]`、`[{ "command": "uv.cache-clean" }]`）。
不写 `actions` 时，风险不高于 `medium` 的规则默认带一个 `delete: contents`（只有可执行的规则才计入可清理），其余没有动作。

`expect.paths` 里每个路径可以检查：

| 字段 | 含义 |
|---|---|
| `rule` | 节点所属的规则 id；`null` 表示不属于任何规则 |
| `risk` | 综合规则和保护名单后的风险（`safe` / `low` / `medium` / `high` / `guide` / `never`）；`null` 表示未识别 |
| `protection` | 保护级别：`none` / `soft` / `guide` / `hard` |
| `cleanable` | 子树中可以清理的字节数 |
| `unexplained` | 子树中没有任何规则解释的字节数 |
| `root` | 是不是规则命中的那个节点（规则位置的根） |
| `root_bytes` / `root_cleanable` / `root_shared` | 节点所属规则位置的总占用、可清理部分、和别处用硬链接共享的部分 |

`expect.totals`：`cleanable`（整个扫描范围可清理）、`cleanable_by_risk`（按风险）、`roots`（规则位置的个数）。

另外，运行器对树里每个不是 `metadata` 的路径检查：逐路径的保护名单检查（`ProtectionCheck`）和引擎给出的保护级别一致。
保护名单用程序内置的那一份；路径令牌按 `C:/Users/<用户>`、`C:/Windows`、`C:/ProgramData`、`C:/Program Files` 展开。

## hotspots：快照对比的热点分解

```json
{
  "description": "说明",
  "cases": [
    {
      "name": "用例名",
      "before": [ ... ],
      "after": [ ... ],
      "stop_at": [ "C:/…/Docker/wsl" ],
      "expect": [ { "path": "C:/…/A/a.bin", "change": "300M" }, { "path": "C:/…/Local", "change": "150M", "remainder": true } ],
      "totals": { "net": "50M", "increase": "50M", "decrease": 0 }
    }
  ]
}
```

`before` 和 `after` 是同一个盘相隔一天的两次扫描。运行器分别用"不剪枝"（记录全部节点）和默认剪枝（目录 ≥ 50 MiB、文件 ≥ 100 MiB）
保存两次快照再对比，两种方式的热点列表都必须等于 `expect`（顺序按变化量从大到小）——这就是 M3 验收的"完整树对比和剪枝后对比一致"。
`stop_at` 是规则位置：分解到这些目录就停。`remainder: true` 表示"这个目录下除了上面列出的之外的其余部分"。`totals` 可选。
