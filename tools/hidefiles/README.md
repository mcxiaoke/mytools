# HideFiles

Windows 文件保护小工具（.NET 8 / WPF）。把文件装进**单文件自加密容器**：统一魔数、不暴露原始格式、可改名搬运，用 PIN 或恢复码还原。

## 两种保护模式

| 模式 | 做法 | 400MB 视频实测 | 说明 |
|---|---|---|---|
| **结构锁**（默认） | 原地 CTR 加密前 64KB 头部（可调 4KB~1MB）+ 格式关键结构，不重写整个文件 | 混淆 0.35s / 还原 0.13s | 媒体主体仍是明文，能挡住双击播放与通用工具 |
| **整体加密** | 全文件按 64KB 分块 AES-256-GCM | 加密 1.5s / 解密 1.2s | 没有 PIN / 恢复码无法还原 |

耗时含进程启动与 PBKDF2（约 0.1s）；结构锁的增量只来自固定的 64KB 读写，与文件总大小基本无关。

## 快速上手

1. 解压 `dist/HideFilesApp-1.0.0-win-x64.zip` 并运行 `HideFilesApp.exe`（需已安装 .NET 8 Desktop Runtime）。
2. 「规则与系统设置」设置 PIN，并**离线保存恢复码**（忘记 PIN 时唯一凭据）。
3. 「结构锁工作台」拖入文件 →「混淆文件」；要撤销点「还原文件」。
4. 需要强保护时切到「整体加密工作台」→「加密文件」/「解密文件」。
5. 可选：同页安装右键菜单（四个动作 +「在 HideFiles 中管理…」）。

## 命令行

```bash
hidefiles setpin <PIN> [--current <旧PIN>]   # 首次设置输出恢复码；改 PIN 只重封装主密钥
hidefiles hide <路径...> --pin <PIN>         # 混淆（结构锁）
hidefiles encrypt <路径...> --pin <PIN>      # 加密（整体）
hidefiles unhide <路径...> --pin <PIN>       # 还原/解密（也可用 --recovery <恢复码>）
hidefiles scan [路径...]                     # 查看状态（带 --pin 时显示原始文件名）
hidefiles dir-hide <目录...>                 # 混淆目录内部的子目录名（可逆、幂等）
hidefiles dir-unhide <目录...>               # 还原目录内部的子目录名
```

`unhide` / `restore` / `decrypt` 等价，按容器魔数自动识别；`--dry-run` 只列出待处理文件。

## 右键菜单

「混淆文件（结构锁）」「还原文件（结构锁）」「加密文件（整体加密）」「解密文件（整体加密）」，外加「在 HideFiles 中管理…」。菜单只从 GUI 设置页安装/卸载。

## 目录名混淆（独立操作）

把文件夹内的子目录名改成 `_hf_…` 形式，可原样还原：

- GUI：「规则与系统设置」→「目录名混淆」→ 混淆/还原（选择文件夹）
- CLI：`hidefiles dir-hide <目录>` / `hidefiles dir-unhide <目录>`

已混淆的目录会自动跳过，重复执行不会二次混淆；只改所选目录**内部**的子目录，不动所选目录自身。它只降低可读性，不提供加密保护。

## 支持格式

- 结构锁：MP4 / M4V / MOV、MKV / WebM、JPG / JPEG、PNG。
- 整体加密：任意文件。
- 其它格式用结构锁会明确报错并提示改用整体加密，不会静默产生"假保护"。

## 安全要点

- PIN 经 PBKDF2（60 万次）派生密钥；config 不保存明文 PIN 或主密钥。
- **修改 PIN 不会重建主密钥**：只重新封装同一主密钥，恢复码不变，历史文件仍可用新 PIN 还原（需要当前 PIN，CLI 用 `--current`）。
- 恢复码是主密钥的 Base32，可脱离 PIN 还原；GUI 还原时可切换为"使用恢复码"。
- 加密后不能直接播放/预览，必须先还原。
- 目标文件重名时自动加 `(1)`，不覆盖已有文件。

## 项目结构

```text
hidefiles/
├── publish.ps1                  发布：测试 → 打包 → SHA256
├── src/
│   ├── HideFiles.Core/          容器、密钥、格式定位、目录名混淆、批处理
│   ├── HideFiles.Cli/           命令行 hidefiles.exe
│   └── HideFiles.App/           WPF 界面 HideFilesApp.exe
├── tests/HideFiles.Tests/       xUnit 单元测试
├── docs/
│   ├── 架构方案.md              容器格式与密钥体系
│   └── CHANGES-YYYYMMDD.md      变更记录
└── dist/                        发布产物
```

## 构建与测试

```powershell
dotnet test                  # 全部单元测试（55 项）
dotnet build -c Release      # 构建
.\publish.ps1                # 打包 dist/*.zip 与 SHA256SUMS.txt
```

容器格式与密钥体系见 [docs/架构方案.md](docs/架构方案.md)。
