# DiskThinner

一个轻量的 Windows C 盘空间分析与清理工具，要回答四个问题：哪里大、哪里在涨、是谁的、能不能删。

当前**阶段 0（准备与技术验证）已完成**，下一步是 M1（扫描与空间浏览）。还不能用于实际清理。

- 研发方案：[docs/dev-plan-v0.2.md](docs/dev-plan-v0.2.md)
- 阶段 0 验证报告：[docs/phase0-report.md](docs/phase0-report.md)
- 界面原型：[prototype/index.html](prototype/index.html)（双击用浏览器打开）

## 构建

需要 Windows 10/11 和 .NET 10 SDK（版本见 `global.json`）。

```powershell
dotnet build DiskThinner.slnx
dotnet test DiskThinner.slnx
```

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
