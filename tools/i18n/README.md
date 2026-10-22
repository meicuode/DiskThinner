# i18n：界面文字

界面文字的做法见 `src/DiskThinner.Core/L.cs`：源代码里的中文就是键，`L.T("清理计划")`、`L.F("扫描 {0} 项", count)`。
全部键在 `src/DiskThinner.Core/Strings.resx` 里，由这个工具从源代码生成。

```powershell
# 新写或改了界面文字后，更新 Strings.resx（CI 会检查它和源代码是否同步）
dotnet run --project tools/i18n -- extract src src/DiskThinner.Core/Strings.resx

# 检查：resx 是否同步、有没有没包成 L.T / L.F 的中文（CI 跑的就是这个）
dotnet run --project tools/i18n -- check src src/DiskThinner.Core/Strings.resx

# 一次性改写：把源代码里的中文字符串包成 L.T，插值字符串和字符串拼接改成 L.F（已经包过的不再处理）
dotnet run --project tools/i18n -- rewrite src
```

规则：

- 界面上显示的中文都要包：按钮、标签、提示、对话框、错误信息、导出文件里的表头。
- 不包：日志（`AppLog`、`SnapshotCommand.Log`、`Debug.WriteLine`），正则表达式（`Regex`、`[GeneratedRegex]`）。
- `case` 标签、模式匹配、`const`、特性参数、参数默认值里不能有中文：这些位置必须是编译期常量，翻译后就对不上了，改用枚举。
- 一句话写成一个 `L.F`，不要把句子拆成几段拼起来（"和" + 名称 + " 一致"），译文的语序可能不同。
- 规则库（`rules/*.yaml`）里的作用、影响等说明是数据，不在这里，以后单独翻译。

翻译：把 `Strings.resx` 复制成 `Strings.en.resx` 放在同一目录，只改 `value`；程序启动时设置 `L.Culture`。
