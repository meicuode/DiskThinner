# 规则源文件

每个 YAML 文件描述一组位置：这里是什么、属于谁、删了有什么影响、该怎么处理。
`dtrule build rules --out rules.db` 校验后编译成规则库；主程序构建时会自动执行这一步。

```yaml
app:                          # 可选：这组规则属于哪个应用
  id: astral.uv
  name: uv
  vendor: Astral
  exes: [uv.exe]              # 用来判断是否安装、执行官方命令时找 exe
  processes: [uv.exe]         # 用来判断是否正在运行
locations:
  - id: astral.uv.cache       # 全局唯一，永远不改
    dir: '%LOCALAPPDATA%\uv\cache'   # dir（整个目录）/ file（文件）/ under（兜底，见下）三选一
    kind: package-cache
    risk: low
    tier: reviewed
    regenerates: true
    purpose: 这个位置的作用
    impact: 删除后的影响
    actions:
      - command: uv.cache-clean      # 只能引用内置白名单（src/DiskThinner.Rules/KnownCommands.cs）
      - delete: contents             # contents / children / self，可加 older_than_days、keep_latest
      - guide: 只展示说明的处理方式
        url: https://...
    relocate: { env: UV_CACHE_DIR }  # 从根上解决：把数据放到别的盘
    evidence:
      - url: https://docs.astral.sh/uv/concepts/cache/
```

- 路径必须以令牌开头：`%USERPROFILE%`、`%LOCALAPPDATA%`、`%APPDATA%`、`%LOCALLOW%`、`%PROGRAMDATA%`、`%WINDIR%`、`%SYSTEMDRIVE%`、`%PROGRAMFILES%`、`%PROGRAMFILES(X86)%`。用户相关的令牌对盘上所有用户都生效。
- 通配：`*` 段内任意字符，`?` 一个字符，`{a,b}` 任选其一，`**` 任意层目录（最多一次，必须单独一段）。
- `dir` / `file` 命中后整棵子树归这条规则；`under` 是兜底，目录下没被更具体规则认领的部分才归它。
- 条件：`has_any`（目录里有这些子项）、`sibling_any`（同级有这些子项）、`not_under`（不在这些位置下）。
- `kind`：cache、temp、log、crash-dump、update-cache、package-cache、build-output、model、vm-disk、session-history、browser-data、app-data、user-data、config、credential、program、system、backup、recycle-bin。
- `risk`：safe（会自动重建）、low（需重新下载）、medium（丢历史或登录态）、high（丢用户数据）、guide（只能用系统工具处理）、never（不能删）。
- `tier`：draft（草稿）、reviewed（审阅过，有依据）、verified（实测过）。
- 写死在程序里的保护名单（`src/DiskThinner.Rules/ProtectionList.cs`）优先级高于任何规则。
