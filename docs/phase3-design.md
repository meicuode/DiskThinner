# 阶段 3 设计：AI 辅助（本轮：AI 分析提示词）

状态：决策点按推荐采用（2026-10-30，用户回复"按推荐来"）：F1–F8 取第 10 节的推荐项。
依据：
- `docs/dev-plan-v0.2.md`（3.8 节、第 5 节"阶段 3"）；
- `prototype/index.html`（AI 页、设置的 AI 一组、"发送给 AI 的内容"对话框、清理计划的"AI 建议"组）；
- 现有代码（main `07a67aa`，v0.3.0）；
- 用户的两条要求：AI 做成可选的、相对独立的模块，以后不想要时能比较容易切干净；本轮先做"生成 AI 分析提示词"，接口配置（含中转站）和对话逻辑先不做，只把做法规划好。

## 0. 摘要

本轮只做一件事：**为规则不认识的目录生成一段 AI 分析提示词**。用户在空间浏览里选中一个未识别的目录，点"生成 AI 分析提示词"。DiskThinner 收集这个目录的统计信息，脱敏后拼成一段固定格式的提示词。用户复制提示词，粘贴到自己常用的 AI 对话里（DeepSeek、通义千问、Kimi、豆包、ChatGPT、Claude 等）。

- **不联网**：DiskThinner 本身不发送任何东西，也不需要 API Key 和接口配置。产品程序集不引用 `System.Net.Http`，由架构测试检查。
- **只发统计信息**：只有大小、文件数、时间、子目录、扩展名分布、程序的产品名和签名、写入监控看到的进程等，不读任何文件内容。用户名、计算机名换成占位符。提示词在对话框里完整显示，可以先删改再复制。
- **回答格式固定**：提示词要求 AI 先用几句话说结论，再给一段固定字段的 JSON（归属、作用、风险、建议、依据、置信度）。风险只有五档，**没有"安全"这一档**，最低是"需重新下载"。以后无论是通过接口直接调用 AI，还是用户把回答粘贴回来，都用这同一个格式和同一套校验。
- **可切除**：
  - 逻辑放在新的类库 `DiskThinner.AI`，界面放在 `src/DiskThinner.App/Ai/` 文件夹；
  - 主程序其他代码只通过一个接口 `IOptionalAi` 使用 AI 功能，没有 AI 模块时这个接口为 null，相关入口都不出现；
  - 一个 MSBuild 属性 `DiskThinnerAi=false` 就能构建出不带 AI 的版本，CI 每次都构建并测试这个版本，保证随时能切干净。
- **只规划、本轮不做**：接口配置（OpenAI 兼容接口、中转站、Anthropic）、发送预览、一键分析和结果卡片、粘贴回答、保存为本地规则、加入清理计划、追问和只读工具、MCP。做法和顺序写在第 5.7 节。

本轮约 3 人日。

## 1. 需求

### 1.1 功能需求（本轮）

- **FR-1 单个目录**：空间浏览的详情里，规则不认识的目录有一个按钮"生成 AI 分析提示词"。这里的"规则不认识"包括两种：完全未识别的目录；知道归谁、但没有规则说明的目录。
  - 两段说明文字里的"AI 分析会在后续版本加入"去掉，换成这个按钮。
  - 规则认识的目录、文件、受硬保护的目录不显示这个按钮。
- **FR-2 批量**：下面两个地方有按钮"为最大的 10 处生成提示词"，一次生成一段提示词，覆盖按大小排在前 10 的未识别位置：
  - 空间浏览"按归属汇总"里的"未识别"组；
  - 概览的"未识别"卡片。
- **FR-3 对话框"AI 分析提示词"**：
  - 标题写分析对象：单个目录写路径；批量写"10 个未识别的位置（共 7.4 GB）"。
  - 正文是一个等宽字体的多行文本框，放完整的提示词。文本框可以编辑，复制时复制编辑后的内容；"恢复原文"把编辑撤回。
  - 底部左侧写字数和"用户名已替换 · 不含文件内容"，右侧两个按钮：主按钮"复制提示词"（复制后两秒内显示"已复制"）和"关闭"。
  - 文本框上方一句说明："复制后粘贴到任意 AI 对话里。DiskThinner 不会联网发送。目录名里可能有项目名、公司名，复制前请看一遍。"
  - 收集信息在后台进行，期间显示"正在收集信息…"，可以关闭对话框（取消收集）。
- **FR-4 证据**：按第 5.3 节收集。
- **FR-5 脱敏**：按第 5.4 节处理，不能关闭。所有字符串在写进提示词之前都经过同一个脱敏函数。
- **FR-6 提示词**：按第 5.5 节的模板生成。单个目录和批量各一个模板，模板是嵌入程序的文本文件，按界面语言选择（现在只有中文，英文在阶段 4 补）。
- **FR-7 设置**："AI"一组只放说明：
  - 提示词怎么用；
  - DiskThinner 不联网，信息只有用户自己粘贴时才离开本机；
  - 接口配置在以后的版本提供。
- **FR-8 不带 AI 模块的版本**：
  - FR-1、FR-2 的按钮不出现，说明文字里不提 AI；
  - 设置里没有"AI"一组；
  - 其他功能不变。

### 1.2 非功能需求

| 项目 | 要求 |
|---|---|
| 生成时间 | 单个目录不超过 1 秒，包括签名检查和占用查询；批量 10 处不超过 5 秒。都在后台线程，界面不卡 |
| 提示词长度 | 单个目录不超过 6,000 字；批量不超过 16,000 字，超出时逐级减少每处的明细（第 5.5 节） |
| 不联网 | 产品程序集都不引用 `System.Net.Http`、`System.Net.Sockets`，由架构测试检查 |
| 体积 | 依赖运行库版解压后仍不超过 10 MB。0.3.0 是 9.4 MB，本轮预计增加不到 100 KB |
| 可切除 | `-p:DiskThinnerAi=false` 构建和测试都通过，输出目录里没有 `DiskThinner.AI.dll`，CI 每次检查 |
| 无障碍 | 对话框的文本框、按钮有可访问名称；复制成功用文字告知（读屏软件能读到"已复制"） |

### 1.3 验收标准

1. 本机 C 盘上选一个未识别的目录：生成的提示词里没有本机用户名、计算机名，也没有 `C:\Users\<真实用户名>`。证据的字段和第 5.3 节一致，长度在限制以内。
2. 把这段提示词贴进一个 AI 对话（DeepSeek 网页版或任意一个）：回答里有结论，也有一段能被 `JsonDocument` 解析、字段齐全的 JSON。这一条需要用户手工做一次；不做的话，报告里写明没有验证。
3. 批量提示词覆盖 10 处，不超过 16,000 字。
4. `pwsh tools\verify\run.ps1 noai` 通过：构建、测试都通过，输出里没有 AI 程序集，界面上没有 AI 入口（App 测试检查）。
5. CI 全绿，包括新的"不带 AI"检查和架构测试。

### 1.4 不做的事（本轮）

- 不联网，不存 API Key，不做接口配置、中转站、测试连接。
- 不做发送预览对话框、结果卡片、"AI 分析"页面。原型里的这些内容保留，标为"规划"。
- 不解析 AI 的回答，不把 AI 的结论加入清理计划，不保存为本地规则。
- 不做追问、只读工具、MCP。
- 不读文件内容，不发送完整的文件清单，也不发送完整的已装软件清单（只发和目录对得上的软件，以及命令行工具，第 5.3 节）。
- 复制提示词不写操作记录（F7）。

## 2. 查证过的事实

### 2.1 现有代码里能直接用的数据

| 需要的信息 | 来源 |
|---|---|
| 大小、文件数、子目录数、最后写入时间、子目录和文件名 | `NodeTree` / `Node`（`AllocSize`、`FileCount`、`DirCount`、`LastWriteUtc`）。树里**没有创建时间**，生成时用 `Directory.GetCreationTimeUtc` 读一次，读不了就不写 |
| 规则命中 | `Classification`：`RuleOf`、`Unexplained`、`UnexplainedHotspots`、`Roots` |
| 推断的归属和依据 | `OwnerMap.Resolve`、`Owner.Evidence`；已装软件清单在 `OwnerMap.Inventory.Apps`，带来源（注册表、商店、npm、scoop、命令行工具、进程） |
| 程序的产品名、公司名、签名 | `ExeIdentityReader.Read` / `ReadSignature`：有缓存；不联网检查吊销，不会卡住；大文件要几百毫秒 |
| 写入监控 | `MonitorEvidence.For(WriteIndex, path, isDir)`：现成的证据文字，和详情里的 ETW 证据一致 |
| 当前占用 | `RestartManager.WhoLocks`：一次最多登记 200 个文件 |
| 路径脱敏 | `PathDisplay.Shorten`：当前用户的目录换成 `%LOCALAPPDATA%`、`%APPDATA%`、`%LOCALLOW%`、`%USERPROFILE%` |
| 保护名单 | `Classification.ProtectionOf(node)` |

### 2.2 要改的地方

- `DetailPanel` 有两处"AI 分析会在后续版本加入"：单个目录在 `BuildFor` 末尾的说明里，组在 `BuildForGroup` 的"未识别"提示里。
- `SettingsPage` 的"AI"一组和"本地规则"一行现在都是占位文字。
- 概览的"未识别"卡片现在没有按钮。
- `PlanGroup.Ai` 已经有了，以后用。
- `AuditLog.ParseAction` 遇到不认识的代码会显示成"拦截"。以后加 `ai_send` 这类代码之前要先改成"未知操作"（第 5.7.7 节）。

### 2.3 外部接口（为以后的部分查的）

- DeepSeek 的 OpenAI 兼容接口支持 `response_format: {"type": "json_object"}`，要求提示词里出现"json"并给出格式示例（[DeepSeek JSON Output](https://api-docs.deepseek.com/guides/json_mode)）。
- Anthropic 的接口支持按 JSON Schema 约束输出（[Claude structured outputs](https://platform.claude.com/docs/en/agent-sdk/structured-outputs)）。

以后通过接口调用时可以强制输出 JSON。本轮是网页对话，只能靠提示词要求，所以模板里写出完整的格式示例。

- 官方 C# MCP SDK `ModelContextProtocol` 2.3.0（2026-10-21 发布）依赖：
  - `ModelContextProtocol.Core` 依赖 `Microsoft.Extensions.AI.Abstractions` 和 `Microsoft.Extensions.Logging.Abstractions`；
  - 主包另外依赖 `Microsoft.Extensions.Hosting.Abstractions` 和 `Microsoft.Extensions.Caching.Abstractions`。

  这些依赖可能超出 10 MB 的余量（只剩约 0.6 MB），所以 MCP 怎么实现以后再定（第 5.7.6 节）。

## 3. 原型核对

### 3.1 原型里已有的

- "AI 分析"页：左边是工具查到的事实和其他未识别目录，右边是分析结果卡片和追问。
- 设置的"AI"一组：接口类型、接口地址、模型、API Key、测试连接、发送前预览、MCP 服务。
- "发送给 AI 的内容"对话框：证据 JSON。
- 清理计划的"AI 建议"组；操作记录里的"AI 推断 · 用户逐项确认"。
- 概览"未识别"卡片的"逐个用 AI 分析"，"建议先处理"里未识别一行的"AI 分析"。

### 3.2 本轮对原型的修改（和本文档一起提交）

1. 新增对话框"AI 分析提示词"，左侧导航的"原型：对话框"里有入口。内容按 FR-3，示例是 `%LOCALAPPDATA%\ms-playwright` 的提示词（节选）。
2. 空间浏览的原型说明里加一块"未识别目录的详情"示例：有"生成 AI 分析提示词"按钮。
3. 概览：
   - "未识别"卡片的按钮改成"为最大的 10 处生成提示词"；
   - "建议先处理"里未识别一行的按钮改成"查看"，和现在的程序一致。
4. 设置的"AI"一组：
   - 上面是本轮的说明（FR-7）；
   - 下面是以后的接口配置，整体标"规划"。接口类型的说明里写明"OpenAI 兼容接口也可以填中转站的地址"，并加一个"启用 AI 接口"开关，默认关闭。
5. "AI 分析"页、"发送给 AI 的内容"对话框：
   - 页面顶部加"规划，本轮不做"的说明；
   - 提供方下拉框去掉"通过 MCP：Claude Code"（MCP 是别的工具调用 DiskThinner，不是 DiskThinner 的提供方）；
   - 原型说明里风险下限一条去掉"（待确认）"，改成引用 F4。
6. 工具栏的"AI 分析"按钮去掉；左侧导航的"AI 分析"移到"原型：规划"分组里。

## 4. 总体设计

### 4.1 工程结构

```
src/
  DiskThinner.AI/                 新：AI 的逻辑，不引用 WinForms，不联网
    Evidence/EvidenceCard.cs        证据卡（记录类型，JSON 源生成）
    Evidence/EvidenceCollector.cs   从扫描树、规则、归属、已装软件收集证据
    Redactor.cs                     脱敏
    PromptBuilder.cs                套模板，控制长度
    Prompts/analyze-directory.v1.zh-Hans.txt   单个目录的模板（嵌入资源）
    Prompts/analyze-batch.v1.zh-Hans.txt       批量的模板（嵌入资源）
    AnswerFormat.cs                 回答的 JSON 格式（本轮只用于测试和模板里的示例）
  DiskThinner.App/
    OptionalAi.cs                   新：IOptionalAi 接口和创建入口（始终编译，不引用 DiskThinner.AI）
    Ai/                             新：只在 DiskThinnerAi=true 时编译
      AiFeature.cs                    实现 IOptionalAi；把写入监控、占用查询接到收集器上
      PromptDialog.cs                 "AI 分析提示词"对话框
tests/
  DiskThinner.AI.Tests/           新：脱敏、证据、提示词、回答格式
  DiskThinner.App.Tests/Ai/       新：对话框和入口（只在 DiskThinnerAi=true 时编译）
```

`DiskThinner.AI` 引用 Core、Scanner、Rules、Inventory，不引用 Monitor 和 Cleanup。写入监控的文字和占用查询由 `AiFeature` 通过委托传进去，这样测试里可以用假数据。

### 4.2 可选模块的接缝

```csharp
// src/DiskThinner.App/OptionalAi.cs（始终编译）
internal interface IOptionalAi
{
    /// <summary>为一个或多个目录打开"AI 分析提示词"对话框。</summary>
    void ShowPrompt(IWin32Window owner, AiPromptRequest request);

    /// <summary>设置里"AI"一组的内容。</summary>
    Control CreateSettingsCard();
}

internal sealed record AiPromptRequest(NodeTree Tree, IReadOnlyList<int> Nodes, Classification Classification,
    OwnerMap Owners, DiskThinner.Monitor.WriteIndex? Writes, string Title);

internal static partial class OptionalAi
{
    public static IOptionalAi? Create()
    {
        IOptionalAi? ai = null;
        CreateCore(ref ai);
        return ai;
    }

    // 没有实现时，编译器连同调用一起去掉
    static partial void CreateCore(ref IOptionalAi? ai);
}

// src/DiskThinner.App/Ai/AiFeature.cs（只在 DiskThinnerAi=true 时编译）
internal static partial class OptionalAi
{
    static partial void CreateCore(ref IOptionalAi? ai) => ai = new AiFeature();
}
```

- 用的是 C# 的分部方法：没有 AI 文件夹时，`CreateCore` 没有实现，调用被编译器去掉，`Create()` 返回 null。不用 `#if`，不用反射，单文件发布和裁剪都不受影响。
- `MainForm` 启动时调用一次 `OptionalAi.Create()`，把结果交给几个入口：
  - 空间浏览详情：`DetailPanel.AiAvailable` 加一个事件；
  - 概览的"未识别"卡片；
  - 设置页。
  
  为 null 时这些入口都不出现。
- 构建开关：
  - `Directory.Build.props`：`<DiskThinnerAi Condition="'$(DiskThinnerAi)' == ''">true</DiskThinnerAi>`。
  - `DiskThinner.App.csproj`：
    ```xml
    <ProjectReference Include="..\DiskThinner.AI\DiskThinner.AI.csproj" Condition="'$(DiskThinnerAi)' == 'true'" />
    <Compile Remove="Ai\**" Condition="'$(DiskThinnerAi)' != 'true'" />
    ```
  - `DiskThinner.App.Tests.csproj` 同样处理 `Ai\**`。
- **切除步骤**（写进 README 的"构建"一节）：
  1. 删掉 `src/DiskThinner.AI`、`tests/DiskThinner.AI.Tests`、`src/DiskThinner.App/Ai`、`tests/DiskThinner.App.Tests/Ai`；
  2. 删掉 slnx 里的两行和上面的条件项；
  3. 运行 `i18n extract`，去掉不再使用的界面文字。

  `OptionalAi.cs` 可以留着（永远返回 null），也可以连同几个入口一起删。
- **CI 的检查**（ci.yml 新增一个 job `no-ai`）：
  - `dotnet build src/DiskThinner.App -c Release -p:DiskThinnerAi=false`；
  - 检查输出里没有 `DiskThinner.AI.dll`；
  - `dotnet test tests/DiskThinner.App.Tests -c Release -p:DiskThinnerAi=false`。

  本机用 `tools/verify/run.ps1 noai`，输出放到 `artifacts/noai/`，不和平常的构建混在一起。

### 4.3 数据流

```
选中目录 → MainForm 拼 AiPromptRequest → IOptionalAi.ShowPrompt
  → PromptDialog（后台）→ EvidenceCollector：扫描树 + 规则 + 归属 + 已装软件 + 签名 + 写入监控文字 + 占用
  → Redactor（每个字符串）→ PromptBuilder（模板 + 证据 JSON，控制长度）→ 文本框 → 用户复制
```

扫描树在扫描结束后只读，后台线程读它没有问题。重新扫描时，界面先关闭打开着的对话框，再换掉扫描树。

## 5. 方案

### 5.1 证据卡的格式

证据卡用 System.Text.Json 源生成序列化。输出带缩进，中文不转义（`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`；只用来显示和复制，不嵌进网页）。字段名用英文 snake_case：AI 对英文字段名理解得更稳定，以后加英文界面时也不用改格式。值里的说明文字跟随界面语言。

### 5.2 入口和对话框

| 入口 | 分析对象 | 条件 |
|---|---|---|
| 空间浏览详情："生成 AI 分析提示词" | 选中的目录 | 是目录；`RuleOf(n)` 为 null；`Unexplained(n) == AllocSize`（和现在显示"没有规则认识这里"的条件相同）；不在硬保护里 |
| "按归属汇总"的"未识别"组："为最大的 10 处生成提示词" | 组里按大小排前 10 的位置，不含"其余部分" | 组是未识别组 |
| 概览"未识别"卡片：同上 | `UnexplainedHotspots(100 MB)` 里前 10 处，不含"其余部分" | 至少有 1 处 |

对话框是普通的模态窗口：
- 宽 760、高 560（96 DPI，按 DPI 缩放），可以调整大小；
- 文本框用 `Theme.Mono`；
- "复制提示词"用 `Clipboard.SetText`，复制失败（剪贴板被别的程序占用）时重试 3 次，还不行就提示"复制失败，请在文本框里全选后按 Ctrl+C"。

### 5.3 证据字段

单个目录的证据（括号里是批量时每处的上限）：

| 字段 | 内容 | 上限 |
|---|---|---|
| `format` | `"diskthinner.evidence/v1"` | |
| `os` | "Windows 11 25H2"（版本号来自注册表的 DisplayVersion） | |
| `path` | 脱敏后的路径 | |
| `size`、`size_bytes`、`files`、`dirs` | 占用、文件数、子目录数 | |
| `created`、`last_write`、`last_write_days_ago` | 创建日期（读不到就不写）、子树里最后写入的日期、距今天数 | |
| `children` | 直接子项：名称、类型、大小、文件数、最后写入日期；前 6 个子目录再带一层（各 4 项） | 12 项（批量 6 项，不带第二层） |
| `other_children` | 没列出的子项：个数、合计大小 | |
| `extensions` | 按占用的扩展名分布，比例保留两位小数，其余合并成 `other` | 8 种（批量 5 种） |
| `executables` | 子树里最大的几个 `.exe`：相对路径、产品名、公司名、文件说明、版本、签名者、签名是否有效 | 5 个（批量 2 个） |
| `owner_guess` | DiskThinner 的推断：名称、厂商、来源（推断 / 实测），以及依据的文字 | 依据最多 4 条 |
| `installed_matches` | 已装软件里名称或安装位置和目录名、子目录名对得上的。用 `Owners` 里同样的通用词表排除 cache、data 之类的词，词至少 3 个字符 | 5 个 |
| `dev_tools` | 本机的命令行工具和 npm 全局包：名称、版本（帮助 AI 判断"这是不是某个开发工具下载的东西"） | 15 个（批量时整段提示词只写一次） |
| `rules_inside` | 目录里规则认识的子位置：相对路径、规则编号、类别、风险 | 5 个 |
| `protected_inside` | 目录里在保护名单上的子位置：相对路径、原因（"SSH 密钥"等） | 5 个 |
| `monitoring` | 写入监控覆盖了多久（`MonitorEvidence` 的文字），从没监控过时写"未开启写入监控" | |
| `writers` | 写入监控看到的写入进程（进程链、新建文件数、估算增长） | 3 条 |
| `open_by` | 最大的 64 个文件现在被哪些程序打开（Restart Manager） | 5 个 |

不收集的信息：文件内容、完整的文件清单、注册表里的值、完整的已装软件清单、环境变量。

时间：
- 子树遍历是 O(子树节点数)，上限 200 万个节点，超出时只统计前 200 万个，并写 `"truncated": true`；
- 签名检查每个文件几十到几百毫秒，所以限制个数；
- 占用查询一次约 0.1 秒。

### 5.4 脱敏

脱敏由 `Redactor` 统一处理。`EvidenceCollector` 产生的每个字符串，包括写入监控、归属依据这些现成的文字，在序列化前都经过它。这样新加字段时不会漏掉。顺序如下：

1. **路径令牌**：当前用户的目录换成 `%LOCALAPPDATA%` 等，复用 `PathDisplay.Shorten`。
2. **其他用户的个人目录**：`C:\Users\<名字>\` 换成 `C:\Users\<user2>\`。同一个名字在一段提示词里编号固定。`Public`、`Default` 不换。
3. **用户名和计算机名**：当前用户名、显示名（读得到时）、计算机名出现在任何地方都换成 `<user>`、`<computer>`，不区分大小写。
   - 长度 3 个字符以上的按子串替换；
   - 更短的只在整个路径段相等时替换，免得把普通单词弄坏。
4. **其他个人信息**：
   - 邮箱地址换成 `<email>`；
   - Windows 账户 SID（`S-1-5-21-…`）换成 `<sid>`；
   - 长度 32 以上的十六进制串和 GUID 不换（经常是软件的缓存目录名，对判断有用）。

这四步不能关闭。目录名里的项目名、公司名识别不了，所以对话框里提醒用户复制前看一遍，并允许直接删改。

### 5.5 提示词模板

单个目录的模板（`analyze-directory.v1.zh-Hans.txt`，`{{evidence}}` 换成证据 JSON）：

````text
你是熟悉 Windows 软件和开发工具的磁盘空间顾问。下面是磁盘清理工具 DiskThinner 在一台 Windows 电脑上找到的一个占用较大、但它的规则库不认识的目录。请判断：它是什么、是哪个软件创建的、能不能删、怎么删最稳妥。

关于这些信息：
- 只有目录结构和统计数字，没有任何文件内容。
- 用户目录写成了 %USERPROFILE%、%LOCALAPPDATA%、%APPDATA% 这样的形式；<user>、<computer>、<email> 是替换掉的个人信息。
- owner_guess 是 DiskThinner 自己的推测，不一定对。writers 是写入监控实际看到的写入进程；它为空不代表没有程序写入，要结合 monitoring 看监控了多久。

请遵守：
1. 只根据给出的信息和你确定知道的事实判断。拿不准就直说，并告诉我还应该查看什么（例如某个子目录里有哪些文件）。不要编造软件名、路径或命令。
2. 风险只能从下面五档里选一档，不要说"完全安全"：
   - redownload：删了以后会重新下载或重新生成（缓存、安装包、旧版本、模型、工具链）
   - history：会丢失历史记录、登录状态、设置这类不太重要的数据
   - user_data：可能有我自己的数据（文档、项目、存档、聊天记录、数据库）
   - program：程序本身或正在使用的运行环境，应该卸载，不要直接删除
   - unknown：判断不了
3. 优先推荐软件自带的清理功能或官方命令，其次才是手动删除。如果只建议删除其中一部分（例如旧版本的子目录），写出具体路径，路径必须在这个目录里面。
4. 写清楚删除前要关闭哪些程序、删除后会发生什么。
5. 如果能通过环境变量或软件设置把它放到别的盘，也告诉我。

请用中文回答。先用三到五句话说结论，然后在一个 json 代码块里按下面的格式给出结果。字段都要有，不知道的写 null 或空数组：
```json
{
  "path": "被分析的目录，照抄",
  "owner": "归属的软件或组件",
  "vendor": "厂商",
  "purpose": "这个目录存的是什么",
  "risk": "redownload | history | user_data | program | unknown",
  "confidence": "high | medium | low",
  "suggestion": "建议怎么处理",
  "delete": [{ "path": "建议删除的路径", "reason": "理由" }],
  "commands": ["官方清理命令"],
  "close_first": ["删除前要关闭的程序"],
  "relocate": "放到别的盘的方法，没有写 null",
  "basis": ["判断依据"],
  "check_before_delete": ["删除前建议我自己确认的事"]
}
```

目录信息：
```json
{{evidence}}
```
````

批量模板（`analyze-batch.v1.zh-Hans.txt`）的开头和规则相同，区别如下：
- "一个目录"改成"下面 N 个目录"，按大小排列；
- 结论按目录逐条写，每条一两句；
- 结果是一个 json 数组，每个元素的格式同上；
- 证据是 `{"format": ..., "os": ..., "dev_tools": [...], "items": [...]}`，`dev_tools` 只写一次。

长度控制：先按正常上限生成。超长时按顺序减少明细：去掉子目录的第二层 → `children` 减到 6 项 → `executables` 减到 2 个 → `extensions` 减到 5 种 → `dev_tools` 减到 8 个。批量时再减少处数，最少 5 处，并在标题里写明实际处数。

### 5.6 回答格式

模板里的 JSON 格式就是以后所有 AI 回答的格式，在 `AnswerFormat.cs` 里定义（记录类型 + 源生成）。本轮只做两件事：
- 测试里检查模板中的示例和 `AnswerFormat` 一致（字段名、`risk` 和 `confidence` 的取值）；
- 生成提示词时把模板里的示例原样带上。

风险对应到 DiskThinner 的风险等级：

| AI 的 `risk` | DiskThinner | 说明 |
|---|---|---|
| `redownload` | 需重新下载（`Risk.Low`） | 最低一档，AI 不能给出"安全"（F4） |
| `history` | 无法恢复（`Risk.Medium`） | |
| `user_data` | 用户数据（`Risk.High`） | 不进清理计划 |
| `program` | 不能删（`Risk.Never`） | 建议卸载 |
| `unknown`、缺失、不认识的值 | 不能删 | |

### 5.7 以后的部分（只规划，本轮不做）

下面是"规划好怎么做"，具体排期等用户决定。每一块都放在 `DiskThinner.AI` 和 `App/Ai/` 里，不改变第 4.2 节的接缝。只有本地规则（5.7.4）和操作记录的新类型（5.7.7）放进核心项目，因为它们在切掉 AI 以后仍然有用，或者必须仍然能读。

#### 5.7.1 接口配置和中转站

- **两种接口**：
  - OpenAI 兼容的 Chat Completions：`POST {接口地址}/chat/completions`，`Authorization: Bearer`。DeepSeek、通义、Kimi、智谱、豆包、OpenRouter、Ollama，以及各种中转站（one-api、new-api 等）都走这一种。中转站就是一个自定义的接口地址，加上它发的 Key 和它支持的模型名，不需要单独的类型。
  - Anthropic Messages：`POST {接口地址}/v1/messages`，`x-api-key`、`anthropic-version`。
- **配置**：
  - 放在单独的 `%LOCALAPPDATA%\DiskThinner\ai.json`，不放进 settings.json，切掉 AI 时整个文件不再使用；
  - 可以有多个配置，每个包括名称、接口类型、接口地址、模型、Key、可选的额外请求头（有的中转站需要）、超时；
  - Key 用 DPAPI（`ProtectedData`，当前用户）加密后存 base64，日志和异常信息里永远不出现 Key；
  - 有一个"启用 AI 接口"总开关，默认关闭。关闭时不创建 `HttpClient`。
- **网络**：
  - 只有 `DiskThinner.AI` 引用 `System.Net.Http`，架构测试相应放宽到这一个程序集；
  - 必须用 HTTPS，只有 `localhost`、`127.0.0.1`、`::1`（Ollama）可以用 HTTP；
  - 不自动跟随跳转到别的主机；默认用系统代理；
  - 请求超时 60 秒，回答最多读 256 KB；
  - 只用 `HttpClient` + System.Text.Json 源生成，不引入 Microsoft.Extensions.AI（依赖多，体积不够）。
- **测试连接**：发一个最短的请求（`max_tokens: 1`），显示状态码和用时。401、404、模型不存在、证书错误分别给出说明。

#### 5.7.2 发送预览、一键分析、结果卡片、缓存

- 用的是同一个证据卡和同一个模板。区别在于：不再让用户复制，而是先弹原型里的"发送给 AI 的内容"对话框，用户确认后再发送。"发送前预览"默认打开，可以关闭；脱敏关不掉。
- 支持 JSON 模式的接口带上 `response_format`（OpenAI 兼容）或结构化输出（Anthropic）；不支持的从回答里取第一个 json 代码块。
- 结果卡片就是原型的右栏。回答按 5.7.3 校验，没通过的字段不显示，并写明"AI 的回答格式不对"。
- 缓存放在 `%LOCALAPPDATA%\DiskThinner\ai.db`，按"证据卡的哈希 + 模型"作键，同样的证据不重复请求，保留 30 天。
- 批量分析（"全部分析"）按大小逐个请求：
  - 每个都弹预览，或者勾选"本次不再预览"；
  - 可以中途停止；
  - 请求之间间隔 1 秒，避免触发接口的限流。

#### 5.7.3 粘贴回答、加入清理计划

- **粘贴回答**：对话框里加"粘贴 AI 的回答"，用户把网页 AI 的回答整段贴回来，工具取出 json 代码块后显示成结果卡片。这样不配置接口也能用后面的功能。
- **校验**（接口返回和粘贴回来的都一样）：
  - 字段类型和取值都要对，字符串有长度上限；
  - `path` 要和被分析的目录一致；
  - `delete` 里的每个路径展开后都必须在被分析的目录里面，并且在扫描树里存在；
  - 不能在保护名单上，不能是硬链接共享的部分；
  - `commands` 只作为文字显示，永远不执行。
- **风险下限**：按 5.6 节的表对应，最低"需重新下载"。
- **加入清理计划**：
  - 进 `PlanGroup.Ai`，默认不勾选，确认对话框里逐项勾选；
  - 处理方式只有"直接删除"和"手动清理"；
  - 执行前的复核不能用规则编号（`Verifier` 现在依赖 `RuleIndex`），要另加一条"AI 项"的复核：路径仍然存在、仍然在被分析的目录里、仍然不在保护名单上、大小没有比分析时大出一倍以上。

#### 5.7.4 本地规则（核心功能）

- 放在 `user.db` 的新表 `local_rule`，代码在 Rules / Storage，不在 AI 模块里，切掉 AI 以后还能用，也能手写、导入、导出。
- 一条本地规则就是一条 `LocationRule`：
  - `Tier.Draft`，编号以 `local.` 开头；
  - 风险不低于"需重新下载"；
  - 动作只有删除和引导，不允许运行命令；
  - 另外记来源（`ai` / `manual` / `import`）和创建时间。
- 加载时和内置规则合并成一个 `RuleSet`，内置规则优先。规则库格式不变，`RuleEngine` 不用改。
- "保存为本地规则"只能从通过了 5.7.3 校验的结果保存，保存前给用户看将要写入的规则。

#### 5.7.5 追问和只读工具

- 用接口的函数调用：OpenAI 兼容接口用 `tools`，Anthropic 用 `tool_use`。
- 工具都是只读的，结果都经过脱敏：
  - `list_children(path)`；
  - `get_write_history(path)`；
  - `search_installed(query)`；
  - `lookup_rule(path)`；
  - `find_files(pattern)`：只在被分析的目录里找，最多 200 条，只返回名称、大小、时间。
- 每轮最多调用 8 次工具，每次调用都显示在对话里（原型的 `toolcall` 行）。
- 对话只放在内存里，关掉就没了。
- 没有任何会删除或修改东西的工具。

#### 5.7.6 MCP

- `DiskThinner.exe mcp`：通过 stdio 让 Claude Code、Codex、Kiro、Cursor 等工具调用 DiskThinner 的只读功能。工具有 `list_top_dirs`、`get_growth`、`get_evidence`、`lookup_rule`、`list_unrecognized`，返回的内容同样脱敏。
- 先验证 WinExe 在被别的进程重定向标准输入输出时能正常读写。不行的话改成单独的控制台程序 `dtmcp.exe`。
- 用官方 SDK 还是自己实现：
  - 官方 SDK（第 2.3 节）可能放不进 10 MB；
  - 只做 `initialize`、`tools/list`、`tools/call` 三个方法的话，自己实现约 300 行，没有依赖。

  到时实测体积再定（P1）。

#### 5.7.7 操作记录

- 新增 `AuditAction.AiSend`（代码 `ai_send`），记录发送：目标路径、接口主机、模型、字数；不记录内容。新增 `AiAdopt`（`ai_adopt`），记录采纳结论、保存本地规则。
- AI 建议的项目被删除时，仍然记成 `Delete`，依据写"AI 推断 · 用户逐项确认"。
- 先改 `ParseAction`：不认识的代码显示"未知操作"。这样切掉 AI 以后，旧记录仍然能读，也不会被显示成"拦截"。这一改动放进核心的 Storage 项目。

#### 5.7.8 顺序和估算

| 顺序 | 内容 | 估算 |
|---|---|---|
| 1 | 粘贴回答 + 校验 + 结果卡片（不需要联网） | 1.5 人日 |
| 2 | 本地规则（核心）+ 保存为本地规则 | 1.5 人日 |
| 3 | 加入清理计划（AI 项的复核） | 1 人日 |
| 4 | 接口配置、中转站、DPAPI、测试连接、发送预览、操作记录 | 2 人日 |
| 5 | 追问和只读工具 | 2 人日 |
| 6 | MCP | 1.5 人日 |

1 到 3 不联网，可以先做。以后的待定问题：
- **P1**：MCP 用官方 SDK 还是自己实现（看体积）。
- **P2**：真实模型怎么测：用户提供 Key；本机装 Ollama；或者只用假的接口服务器（CI 上只用假的）。
- **P3**："全部分析"是否允许"本次不再预览"。

## 6. 错误处理

| 情况 | 处理 |
|---|---|
| 读创建时间、签名、版本信息失败 | 这个字段不写，其余照常 |
| 占用查询失败 | `open_by` 写空数组，并加 `"open_by_unavailable": true` |
| 目录在扫描后被删掉了 | 仍按扫描树生成，路径后注明"扫描时存在，现在已不存在"，对话框顶部显示同样的提示 |
| 收集时关闭对话框 | 取消收集（CancellationToken），不留后台任务 |
| 生成超时（单个 5 秒，批量 15 秒） | 用已经收集到的部分生成，`"partial": true`，对话框里提示"有些信息没来得及收集" |
| 剪贴板被占用 | 重试 3 次，每次间隔 100 毫秒，仍失败就提示手动复制 |
| 模板资源缺失（构建错误） | 测试覆盖，不在运行时兜底 |

## 7. 输入校验和不变量

- 证据卡里每个字符串都经过 `Redactor`。测试遍历证据卡的所有字符串属性，检查没有当前用户名、计算机名。
- 提示词只在用户点"复制提示词"时进入剪贴板，不写文件、不写日志。日志只记"生成了提示词：单个 / 批量 N 处，字数"。
- 证据里的路径都是脱敏后的显示路径，不能直接用来删除。以后要加入清理计划时，用扫描树里的节点和真实路径，不用 AI 给回来的字符串（5.7.3）。
- 没有 AI 模块时，`MainForm` 里拿到的 `IOptionalAi` 是 null，所有入口都用这一个值判断。

## 8. 测试和验证

### 8.1 单元测试（`DiskThinner.AI.Tests`，CI 上跑）

- 脱敏：
  - 当前用户目录换成令牌；其他用户的目录编号固定；
  - 用户名按子串替换、短用户名只替换整个路径段，计算机名同样处理；
  - 邮箱、SID 被替换，GUID 不替换；
  - 子目录名、exe 产品名、写入监控文字里出现的用户名都被替换。
- 证据：用 `tests/Shared/Fixtures.cs` 的方式构造扫描树，检查：
  - 子目录排序、上限、`other_children` 的合计；
  - 扩展名比例加起来是 1；
  - 第二层子目录；
  - `rules_inside`、`protected_inside`；
  - 200 万节点的截断标记（用小的上限测）。
- 提示词：
  - 模板里的占位符都被替换，证据 JSON 能被 `JsonDocument` 解析回来；
  - 超长时按 5.5 节的顺序减少明细，最后不超过上限；
  - 批量时 `dev_tools` 只出现一次。
- 回答格式：模板示例里的字段名、`risk` 的取值和 `AnswerFormat` 一致；用 `JsonDocument` 解析一段真实回答的样例（测试数据）。

### 8.2 App 测试

- 有 AI 模块时：
  - 未识别目录的详情里有按钮，规则认识的目录、文件、硬保护目录里没有；
  - 未识别组和概览卡片有批量按钮；
  - 对话框用假的收集器能生成文本；"复制提示词"把文本框里（编辑后）的内容放进剪贴板，测试里用替换掉的剪贴板接口检查。
- 没有 AI 模块时（`-p:DiskThinnerAi=false`）：
  - 上面的按钮都没有；
  - 详情和组的说明文字里没有"AI"；
  - 设置里没有"AI"一组。
- 架构测试：遍历主程序引用的所有 DiskThinner 程序集，`GetReferencedAssemblies()` 里都没有 `System.Net.Http`、`System.Net.Sockets`。

### 8.3 手工检查（报告里写明结果）

- 本机 C 盘选 2 个未识别目录，加上 1 次批量，人工检查提示词里没有个人信息。
- 可选：用户把其中一段贴进 DeepSeek 网页版，看回答是否按格式给出（验收标准 2）。
- 截图：用 `--page explorer --select <路径> --show-prompt` 打开对话框截图。这是新增的验证参数，和现有的 `--show-confirm` 一样。

## 9. 子步骤

| 子步骤 | 内容 | 验证 | 提交 |
|---|---|---|---|
| 3.0 设计（本文档） | 设计、决策点；按第 3.2 节改原型并截图 | 原型截图 | "阶段 3.0：AI 分析提示词的设计和原型" |
| 3.1 可选模块的骨架（0.5 人日） | `DiskThinner.AI`、`App/Ai`、`OptionalAi`、构建开关、设置里的说明、CI 的 `no-ai`、架构测试、`run.ps1 noai` | 两种构建和测试都通过 | "阶段 3.1：可选的 AI 模块" |
| 3.2 证据卡和脱敏（1 人日） | `EvidenceCollector`、`Redactor`、`AnswerFormat` | 单元测试 | "阶段 3.2：证据卡和脱敏" |
| 3.3 提示词和对话框（1 人日） | 模板、`PromptBuilder`、`PromptDialog`、三个入口、说明文字、验证参数 | App 测试、本机手工检查、截图 | "阶段 3.3：AI 分析提示词" |
| 3.4 收尾（0.5 人日） | `docs/phase3-report.md`、README（用法和切除步骤）、dev-plan、known-issues | CI 全绿 | "文档：阶段 3 提示词报告" |

合计约 3 人日。做完后进入阶段 4。第 5.7 节的部分等用户决定什么时候做。

## 10. 待确认的决策点

| # | 问题 | 选项 | 推荐 |
|---|---|---|---|
| F1 | AI 模块怎么做到可切除 | A 逻辑放类库 `DiskThinner.AI`，界面放 `App/Ai/` 文件夹，构建开关 + 分部方法接缝（第 4.2 节），CI 每次构建不带 AI 的版本；B 运行时插件（反射加载 dll，单文件发布和裁剪不好用）；C 单独的程序（要复制一份扫描和规则的逻辑） | A |
| F2 | 本轮做多少 | A 只做提示词，复制到任意 AI 对话，不联网、不配置；B 同时做"粘贴回答"和结果卡片（多 1.5 人日） | A（按你的要求；B 排在第 5.7.8 节第 1 位） |
| F3 | 入口 | A 单个目录（空间浏览详情），加上批量（未识别组、概览卡片，最大的 10 处）；B 只做单个目录 | A（网页对话一次贴一段，批量更省事） |
| F4 | 风险档位（原型里"待确认"那一条） | A 提示词里没有"安全"这一档，最低"需重新下载"，以后的校验也按这个下限；B 允许 AI 说"会自动重建" | A |
| F5 | 证据带不带本机软件信息 | A 按第 5.3 节：带对得上的已装软件和命令行工具清单（AI 判断准一些）；B 都不带（暴露得少，判断差一些） | A（对话框里能看到、能删） |
| F6 | 提示词能不能改 | A 文本框可以直接删改再复制，有"恢复原文"；B 只读 | A |
| F7 | 复制提示词要不要写操作记录 | A 不写（工具没有发送，内容用户都看得到）；B 写一条 | A（以后真正通过接口发送时写 `ai_send`） |
| F8 | 原型里的 AI 页、发送预览、结果卡片 | A 保留，标"规划"；B 先删掉 | A |

## 11. 风险

| 风险 | 影响 | 应对 |
|---|---|---|
| AI 编造软件名或命令 | 用户按错误的建议删除 | 提示词要求拿不准就直说、写出依据和"删除前自己确认的事"；本轮 DiskThinner 不执行 AI 的任何建议，删除仍然走规则和清理计划 |
| 目录名里有隐私（项目名、客户名） | 粘贴到外部 AI 时泄露 | 对话框里明确提醒，可以直接删改；脱敏规则不能关；不发文件内容和完整清单 |
| 网页 AI 对长度有限制 | 批量提示词贴不进去 | 批量上限 16,000 字，超出时逐级减少明细和处数 |
| 签名检查慢 | 生成超过 1 秒 | 个数有上限，有超时；签名结果有缓存 |
| 可切除的接缝慢慢被破坏 | 以后切不干净 | CI 的 `no-ai` 每次构建和测试；架构测试；核心代码只能通过 `IOptionalAi` 使用 AI |
| 以后加接口和 MCP 时体积超过 10 MB | 违反 1.2 节的目标 | 不用 Microsoft.Extensions.AI；MCP 先实测体积（P1） |
