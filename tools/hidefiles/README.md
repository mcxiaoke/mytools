# HideFiles

Windows 文件保护小工具（.NET 8 / C# 12 / WPF-UI）。把文件装入**单文件自加密容器**：统一魔数、不泄露原格式、可独立搬运，用 PIN 或恢复码还原。

提供两种**彼此独立**的保护模式：

- **结构锁（默认，极速）**：原地加密前 64KB 头部（可在设置中调至 4KB~1MB）+ 格式关键结构，不重写整个文件。400MB 视频混淆约 0.4 秒、还原约 0.1 秒，适合"防大部分人"和日常批量处理。
- **整体加密（可选，最强）**：整文件 AES-256-GCM 分块加密，没有 PIN/恢复码无法提取任何内容，约 1.5–2.7 GB/s。

两种模式都以**原生右键菜单**提供四个独立动作，GUI 也分成两个独立工作台。

## 快速上手

1. 安装：解压 `dist/HideFilesApp-1.0.0-win-x64.zip`，运行 `HideFilesApp.exe`。
2. 打开 GUI →「规则与系统设置」→ 设置 PIN，并**离线保存生成的恢复码**（忘记 PIN 时唯一凭据）。
3. 在「结构锁工作台」拖入文件 → 点「混淆文件」；要撤销点「还原文件」。
4. 需要强保护时切到「整体加密工作台」→「加密文件」/「解密文件」。
5. （可选）在设置页点"安装右键菜单"，资源管理器和文件夹右键即出现四个动作。

## 命令行

```bash
hidefiles setpin <PIN> [--current <旧PIN>]       # 首次设置生成恢复码；改 PIN 只重封装主密钥
hidefiles hide <路径...> --pin <PIN>            # 混淆（结构锁，极速）
hidefiles unhide <路径...> --pin <PIN>          # 还原（自动识别容器）
hidefiles encrypt <路径...> --pin <PIN>         # 加密（整体 AES-256-GCM）
hidefiles decrypt <路径...> --recovery <恢复码> # 解密（也可用 --pin）
hidefiles scan [路径...]                        # 扫描文件状态
hidefiles dir-hide <目录...>                    # 混淆目录内部的子目录名（可逆、幂等）
hidefiles dir-unhide <目录...>                  # 还原目录内部的子目录名
hidefiles --help
```

`unhide` / `restore` / `decrypt` 等价，容器自带模式标记会自动识别；命令名只是便于记忆。

## 右键菜单（四项）

| 菜单项 | 动作 |
|---|---|
| 混淆文件 (结构锁·极速) | `--hide` |
| 还原文件 (结构锁) | `--unhide` |
| 加密文件 (整体·最强) | `--encrypt` |
| 解密文件 (整体) | `--decrypt` |

外加"在 HideFiles 中管理..."，用 GUI 打开并加入对应工作台。菜单只能从 GUI 的设置页安装/卸载（CLI 不提供菜单命令）。

## 目录名混淆（独立操作）

把文件夹内的子目录名改成 `_hf_…` 形式，可原样还原：

- GUI：「规则与系统设置」→「目录名混淆」→ 混淆/还原目录名（选择文件夹）
- CLI：`hidefiles dir-hide <目录>` / `hidefiles dir-unhide <目录>`

已混淆的目录会被自动跳过，重复执行不会二次混淆；只改所选目录**内部**的子目录，不动所选目录自身。它只降低可读性，不提供加密保护——本工具自身即可还原。

## 支持格式

- 结构锁：MP4 / M4V / MOV、MKV / WebM、JPG / JPEG、PNG（解析其关键结构并加密）。
- 整体加密：任意文件（按字节流整体加密，与格式无关）。
- 其它格式用结构锁会明确报错并提示改用整体加密，不会静默产生"假保护"。

## 安全要点

- PIN 经 PBKDF2（60 万次）派生密钥；config 不保存明文 PIN 或主密钥。
- **修改 PIN 不会重建主密钥**：只用新 PIN 重新封装同一主密钥，恢复码保持不变，此前保护的文件仍可用新 PIN 还原（修改时需提供当前 PIN，CLI 用 `--current`）。
- 恢复码是主密钥的 Base32，可脱离 PIN 还原文件；请与 PIN 分开离线保存。GUI 还原时可切换为"使用恢复码"。
- 加密后不能直接播放/预览，必须先还原。
- 不兼容早期 v1 格式；旧文件请先用旧版本还原后再重新处理。

## 性能参考

| 场景 | 结构锁 | 整体加密 |
|---|---|---|
| 小文件（几 MB）还原 | ~0.15s | ~0.15s |
| 2GB 视频混淆/还原 | ~0.6s / ~0.3s | ~1.6s / ~1.2s |
| 吞吐量 | 与文件大小基本无关 | 1.5–2.7 GB/s |

（含进程启动与 PBKDF2 约 0.1s；结构锁耗时取决于被加密的关键结构大小。）

## 项目结构

```text
hidefiles/
├── publish.ps1                  自动化发布（测试→打包→SHA256）
├── src/
│   ├── HideFiles.Core/          加密容器、格式定位、密钥、右键菜单、批处理
│   ├── HideFiles.Cli/           命令行 hidefiles.exe
│   └── HideFiles.App/           WPF GUI HideFilesApp.exe
├── tests/HideFiles.Tests/       xUnit 单元测试
├── docs/
│   ├── 架构方案.md              与代码一致的设计说明
│   └── CHANGES-YYYYMMDD.md      变更记录
└── dist/                        发布产物
```

## 构建与测试

```powershell
dotnet test                          # 运行全部单元测试（16 项）
dotnet build -c Release              # 构建
.\publish.ps1                        # 打包 dist/*.zip 与 SHA256SUMS.txt
```

更完整的格式容器规范与密钥体系见 [docs/架构方案.md](docs/架构方案.md)。
