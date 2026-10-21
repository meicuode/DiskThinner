# DiskThinner

一个轻量的 Windows C 盘空间分析与清理工具，要回答四个问题：哪里大、哪里在涨、是谁的、能不能删。

当前进度：**M4（清理计划与执行）已完成**。可以查看 C 盘哪里占用最大，每个位置是什么、归哪个软件、风险多大、该怎么处理，也可以按软件或按类别汇总；保存快照后能看到哪些目录在变大、按这个速度多少天写满；在清理计划里确认后删除或运行官方清理命令，每一项都写入操作记录。下一步是设置页和打包发布（M5）。

- 研发方案：[docs/dev-plan-v0.2.md](docs/dev-plan-v0.2.md)
- 阶段 0 验证报告：[docs/phase0-report.md](docs/phase0-report.md)
- M1 验收报告：[docs/m1-report.md](docs/m1-report.md)
- M2 上半验收报告：[docs/m2a-report.md](docs/m2a-report.md)
- M2 下半验收报告：[docs/m2b-report.md](docs/m2b-report.md)
- M3 验收报告：[docs/m3-report.md](docs/m3-report.md)
- M4 验收报告：[docs/m4-report.md](docs/m4-report.md)
- 规则格式：[rules/README.md](rules/README.md)

## 使用

```powershell
dotnet run --project src/DiskThinner.App
```

- 以管理员身份运行时直接读取 NTFS 主文件表（MFT），C 盘几秒扫完；普通权限下逐个目录遍历，较慢，结果为估算，界面上可以一键"以管理员身份重启"。
- 命令行 `--scan D:` 指定要扫描的磁盘（也可以是一个文件夹），`--walk` 强制使用遍历扫描。
- 快照存在 `%LOCALAPPDATA%\DiskThinner\snapshots.db`，每个 C 盘快照只记 50 MB 以上的目录和 100 MB 以上的文件，几十 KB。扫描整个盘后，距离上次快照超过 6 小时会自动保存一次（`--no-auto-snapshot` 关闭）；也可以点"创建快照"，或在增长分析页开启每日快照（Windows 计划任务）。命令行：

  ```powershell
  DiskThinner.exe snapshot [--drive C:]                 # 扫描并保存一次快照
  DiskThinner.exe snapshot --register [--time 12:00]    # 注册每日快照；以管理员身份注册时任务用最高权限运行（MFT 扫描）
  DiskThinner.exe snapshot --unregister                 # 移除每日快照
  ```

- 清理只在"清理计划"页由你确认后执行：先弹出确认框，列出删除后无法恢复的内容；**删除是永久的，不经过回收站**。执行前逐项复核（路径上不能有 junction 或符号链接、保护名单、规则仍然命中），不跟随链接，凭据、浏览器登录数据、程序和系统目录在保护名单里，任何规则都删不了。被占用的文件跳过，不强制结束程序。
- 每一项都可以改成"手动清理（打开所在位置）"：工具只用资源管理器打开目录，不删除任何东西；处理完点"重新测量"记下释放了多少。
- 每次删除、运行命令、手动清理和拦截都写进 `%LOCALAPPDATA%\DiskThinner\user.db` 的操作记录，只追加不修改，可以导出 CSV。
- 官方清理命令（uv、npm、pip、dotnet nuget 等）只运行内置白名单里的，程序必须是本机探测到的那个。用另一个管理员账户提权时，不清理用户目录、不运行命令。
- 判断"归哪个软件"时只在本机读取注册表卸载项、商店应用列表、PATH 里的程序和正在运行的程序路径，不联网，也不上传任何信息。
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
