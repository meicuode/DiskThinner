# DiskThinner

一个轻量的 Windows C 盘空间分析与清理工具，要回答四个问题：哪里大、哪里在涨、是谁的、能不能删。

当前进度：**M2 上半（规则）已完成**。可以查看 C 盘哪里占用最大，每个位置是什么、风险多大、该怎么处理；快照和清理功能还在开发中，程序不会删除任何文件。

- 研发方案：[docs/dev-plan-v0.2.md](docs/dev-plan-v0.2.md)
- 阶段 0 验证报告：[docs/phase0-report.md](docs/phase0-report.md)
- M1 验收报告：[docs/m1-report.md](docs/m1-report.md)
- M2 上半验收报告：[docs/m2a-report.md](docs/m2a-report.md)
- 规则格式：[rules/README.md](rules/README.md)

## 使用

```powershell
dotnet run --project src/DiskThinner.App
```

- 以管理员身份运行时直接读取 NTFS 主文件表（MFT），C 盘几秒扫完；普通权限下逐个目录遍历，较慢，结果为估算，界面上可以一键"以管理员身份重启"。
- 命令行 `--scan D:` 指定要扫描的磁盘（也可以是一个文件夹），`--walk` 强制使用遍历扫描。
- DiskThinner 目前不会删除任何文件。"打开所在位置，自己清理"只是用资源管理器打开目录。
- 界面原型：[prototype/index.html](prototype/index.html)（双击用浏览器打开）

## 构建

需要 Windows 10/11 和 .NET 10 SDK（版本见 `global.json`）。

```powershell
dotnet build DiskThinner.slnx
dotnet test DiskThinner.slnx

# 规则维护
dotnet run --project tools/dtrule -- validate rules
dotnet run --project tools/dtrule -- probe rules --drive C:\ --out probe.md
```

主程序构建时会自动用 dtrule 校验 `rules/` 并编译成内置规则库；规则有错误时构建失败。

## 目录

| 目录 | 内容 |
|---|---|
| `src/` | 产品代码 |
| `tests/` | 单元测试 |
| `spikes/` | 阶段 0 的技术验证程序，不进入产品 |
| `docs/` | 设计文档和验证报告 |
| `prototype/` | HTML 界面原型 |

## 许可证

MIT
