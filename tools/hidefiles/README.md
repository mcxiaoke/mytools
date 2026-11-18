# HideFiles

Windows 文件加密工具（.NET 8 / WPF）。支持将文件封装为单文件加密容器：统一文件结构、隐藏原始类型，使用密码或恢复码解密。

## 两种加密模式

| 模式 | 做法 | 400MB 视频实测 | 说明 |
|---|---|---|---|
| **结构加密** | 原地 CTR 加密头部（默认 64KB，可调 4KB~1MB）与关键格式结构，不重写整个文件 | 加密 0.35s / 解密 0.13s | 适合大体积视频与图片，不可直接播放或预览 |
| **整体加密** | 全文件按 64KB 分块 AES-256-GCM 加密 | 加密 1.5s / 解密 1.2s | 适合任意格式文件，需密码或恢复码解密 |

## 快速上手

1. 运行 `HideFilesApp.exe`（需安装 .NET 8 Desktop Runtime）。
2. 在「设置」中设置密码，并**保存恢复码**（忘记密码时的解密凭据）。
3. 在「结构加密」拖入文件 → 点击「加密」；解密点击「解密」。
4. 需要全文件加密时切换到「整体加密」→ 点击「加密」/「解密」。
5. 可选：在设置中添加右键菜单。

## 命令行

```bash
hidefiles setpin <密码> [--current <当前密码>]   # 首次设置输出恢复码；修改密码需提供当前密码
hidefiles hide <路径...> --pin <密码>            # 结构加密
hidefiles encrypt <路径...> --pin <密码>         # 整体加密
hidefiles unhide <路径...> --pin <密码>          # 解密（也可用 --recovery <恢复码>）
hidefiles scan [路径...]                         # 查看状态（带 --pin 时显示原始文件名）
hidefiles dir-hide <目录...>                     # 伪装目录内部的子目录名
hidefiles dir-unhide <目录...>                   # 还原目录内部的子目录名
```

`unhide` / `restore` / `decrypt` 等价，按容器格式自动识别；`--dry-run` 只列出待处理文件。

## 右键菜单

提供快捷操作菜单：「结构加密」「结构解密」「整体加密」「整体解密」「打开 HideFiles」。支持在设置中添加或移除。

## 目录名伪装（独立操作）

将文件夹内的子目录名添加 `_hf_` 前缀，可原样还原：

- GUI：「设置」→「目录名伪装」→ 伪装 / 还原（选择文件夹）
- CLI：`hidefiles dir-hide <目录>` / `hidefiles dir-unhide <目录>`

已伪装的目录会自动跳过，重复执行不会二次处理；仅修改所选目录**内部**的子目录名。该功能仅降低可读性，不提供加密保护。

## 支持格式

- 结构加密：MP4 / M4V / MOV、MKV / WebM、JPG / JPEG、PNG。
- 整体加密：支持任意文件格式。
- 其他格式在结构加密模式下会提示改用整体加密。

## 安全机制

- 密码经 PBKDF2 派生密钥；配置文件不保存明文密码或主密钥。
- **修改密码不影响已加密文件**：仅重新封装主密钥，恢复码保持不变，此前加密的文件仍可用新密码解密。
- 恢复码为主密钥的 Base32 编码，可脱离密码解密文件。
- 加密后文件不可直接查看内容，解密后恢复原样。
- 目标文件重名时自动添加编号 `(1)`，不覆盖已有文件。

## 项目结构

```text
hidefiles/
├── publish.ps1                  发布脚本：测试 → 打包 → SHA256
├── src/
│   ├── HideFiles.Core/          容器、密钥、格式定位、目录伪装、批处理
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
dotnet test                  # 单元测试（55 项）
dotnet build -c Release      # 构建
.\publish.ps1                # 打包发布
```

容器格式与密钥体系参见 [docs/架构方案.md](docs/架构方案.md)。
