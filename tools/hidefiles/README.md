# CarroCasket 密匣

Windows 文件混淆与加密工具（.NET 8 / WPF）。界面显示名为 **CarroCasket 密匣**；
程序集与可执行文件仍沿用 `CarroCasket.exe` / `CarroCasketCli.exe`，配置目录为 `%APPDATA%\com.mcxiaoke\CarroCasket`。

两个工作台：

- **混淆**：原地打乱文件头部与关键结构，几十毫秒级完成，适合大体积音视频；主体仍是明文，防打开不防提取。
- **加密**：全文件 AES-256-GCM 加密，密码学级保护，适合任意格式。

两种模式产出的都是**单文件自包含容器**：统一文件结构、隐藏原始类型，凭密码或恢复码即可还原。

## 快速上手

1. 运行 `CarroCasket.exe`（需安装 .NET 8 Desktop Runtime）。
2. 在「设置」中设置密码，并**保存恢复码**（忘记密码时的唯一还原凭据）。
3. 需要快速隐藏大文件 → 「混淆」页拖入文件 → 点击「混淆」；还原点「还原」。
4. 需要真正保密 → 「加密」页 → 点击「加密」/「解密」。
5. 可选：在设置中添加右键菜单。

## 两种模式对比

| 模式 | 做法 | 400MB 视频实测 | 适用与边界 |
|---|---|---|---|
| **混淆** | 原地 CTR 加密头部（默认 64KB，可调 4KB~1MB）与关键格式结构，不重写整个文件 | 混淆 0.35s / 还原 0.13s | 仅 MP4/MKV/JPG/PNG 等媒体格式；防双击播放与通用工具，不防专门分析 |
| **加密** | 全文件按 64KB 分块 AES-256-GCM 加密 | 加密 1.5s / 解密 1.2s | 任意格式，密码学级保护 |

> 混淆页里不支持的格式会直接标记为「**不支持**」（灰色），不会被算作失败。换到「加密」页即可处理。

## 命令行

```bash
CarroCasketCli setpin <密码> [--current <当前密码>]   # 首次设置输出恢复码；修改密码需提供当前密码
CarroCasketCli hide <路径...> --pin <密码>            # 混淆（仅媒体格式，可选 --deterministic）
CarroCasketCli encrypt <路径...> --pin <密码>         # 加密（任意格式，可选 --deterministic）
CarroCasketCli unhide <路径...> --pin <密码>          # 还原/解密（也可用 --recovery <恢复码>）
CarroCasketCli scan [路径...]                         # 查看状态（带 --pin 时显示原始文件名）
CarroCasketCli dir-hide <目录...>                     # 伪装目录内部的子目录名
CarroCasketCli dir-unhide <目录...>                   # 还原目录内部的子目录名
CarroCasketCli fn-hide <路径...> [--keep-ext]         # 混淆文件名（可选 --keep-ext 保留扩展名）
CarroCasketCli fn-unhide <路径...>                   # 还原文件名
```

`unhide` / `restore` / `decrypt` 等价，按容器格式自动识别；`--dry-run` 只列出待处理文件；`--deterministic`（或 `--det`）开启确定性加密，保证同一文件+密码产生完全相同的密文输出（适合网盘同步与去重校验）。

## 右键菜单

菜单项三条，每条同时覆盖正向与还原，无需先判断文件处于什么状态：

| 菜单项 | 行为 |
|---|---|
| 文件混淆 / 还原 | 未混淆 → 混淆；已混淆 → 还原 |
| 文件加密 / 解密 | 未加密 → 加密；已加密 → 解密 |
| 打开 CarroCasket | 只把文件带入对应工作台列表，不自动处理 |

在「设置」中添加或移除。旧版注册的四个单项动词仍可用（升级后不失效）。

## 辅助工具：目录名与文件名混淆（独立操作）

主界面提供独立的「工具」标签页，每个功能一个 Group 分组：

- **目录名混淆**：
  - GUI：「工具」→「目录名混淆」→ 混淆目录名 / 还原目录名（支持递归多层子目录）
  - CLI：`CarroCasketCli dir-hide <目录>` / `CarroCasketCli dir-unhide <目录>`
- **文件名混淆**：
  - 与目录名混淆算法完全一致（Base32 + 掩码 + CRC16 校验尾），生成 `_hf_` 前缀名称。
  - GUI：「工具」→「文件名混淆」→ 混淆文件夹 / 还原文件夹 / 混淆文件 / 还原文件
  - 可选「保留扩展名」：默认关闭（整体混淆隐藏格式与类型）；开启后保留原有扩展名。
  - 智能自适应还原：自动识别整体混淆或保留扩展名两种模式，无需人工配置。
  - CLI：`CarroCasketCli fn-hide <路径...> [--keep-ext]` / `CarroCasketCli fn-unhide <路径...>`

已混淆的项会自动跳过，重复执行保证幂等；仅修改名称，不损坏文件数据。
混淆名带校验尾，只有本工具生成的名字才会被还原——用户自建的 `_hf_` 开头项不会被误改名。
该功能仅降低可读性，不提供密码学加密保护。

## 支持格式

- 混淆：MP4 / M4V / MOV、MKV / WebM、JPG / JPEG、PNG。
- 加密：支持任意文件格式。

## 安全机制

- 密码经 PBKDF2 派生密钥；配置文件不保存明文密码或主密钥。
- 密码校验以「能否解开主密钥」为准，不依赖可被离线穷举的哈希。
- **修改密码不影响已加密文件**：仅用新密码重新封装同一主密钥，恢复码保持不变。
  在**本机**上，此前加密的文件仍可用新密码解密；把文件拷到**其它机器**（或本机 config 已丢失）时，
  新密码无法解开旧文件，需使用原密码或恢复码。
- 恢复码为主密钥的 Base32 编码，可脱离密码解密文件；界面按 4 字符分组显示，复制后 60 秒自动清空剪贴板。
- 配置文件损坏时会保留 `config.json.corrupt-*` 备份并提示，不会静默覆盖密钥材料。
- 加密后文件不可直接查看内容，解密后恢复原样。
- 目标文件重名时自动添加编号 `(1)`，不覆盖已有文件。
- **确定性加密可选**：设置页提供「确定性加密」开关（CLI 对应 `--deterministic`）。开启后由文件前 64KB 指纹与密码确定性派生密钥材料与 Nonce，同一个文件 + 同一个密码在任意机器上均生成逐字节一致的密文，便于网盘同步与去重校验；默认关闭（保持密码学语义安全）。

## 项目结构

```text
hidefiles/
├── publish.ps1                  发布脚本：测试 → 打包 → SHA256
├── src/
│   ├── HideFiles.Core/          容器、密钥、格式定位、目录伪装、批处理
│   ├── HideFiles.Cli/           命令行 CarroCasketCli.exe
│   └── HideFiles.App/           WPF 界面 CarroCasket.exe
├── tests/
│   ├── HideFiles.Tests/         xUnit 单元测试
│   └── HideFiles.UiQa/          界面验收（真实渲染，无人工交互）
├── docs/
│   ├── 架构方案.md              容器格式与密钥体系
│   └── CHANGES-YYYYMMDD.md      变更记录
└── dist/                        发布产物
```

## 构建与测试

```powershell
dotnet test                                   # 单元测试（135 项）
dotnet run -c Release -p tests/HideFiles.UiQa # 界面验收（退出码 0 = 通过）
dotnet build -c Release                       # 构建
.\publish.ps1                                 # 打包发布
```

容器格式与密钥体系参见 [docs/架构方案.md](docs/架构方案.md)。
