# HideFiles

Windows 极速自包含文件特征混淆与还原小工具（基于 .NET 8 / C# 12 / WPF-UI）。

---

## 核心特性

- **极速就地特征混淆（In-Place Obfuscation）**：
  不进行低效的全文件加密，仅针对前 64KB 头部进行特征掩码覆写，毫秒级摧毁视频容器与多媒体关键元数据。500MB+ 视频处理耗时低于 5ms，上千个文件秒级批处理。
- **单文件自包含（In-File Stash 协议）**：
  原始头部特征、原始文件名及扩展名加密打包追加于文件尾部，自带 8 字节私有魔数与 CRC32 校验。脱离工具元数据数据库也能随时无损还原，防二次混淆。
- **灵活规则与伪装映射**：
  内置视频（`.mp4`, `.mkv` -> `.dat`）、图片（`.png`, `.jpg` -> `.tmp`）、音频（`.flac`, `.mp3` -> `.bin`）等规则组，支持自定义扩展名与伪装名。
- **Windows 资源管理器折叠右键菜单**：
  支持一键写入当前用户注册表级联二级菜单（Cascading Menu），桌面与文件右键整洁不杂乱。
- **现代化 WPF 界面 + 极速 CLI**：
  支持拖拽放置批量处理，提供完整命令行接口便于自动化批处理。

---

## 解决方案结构

参考成熟的 .NET 8 项目组织形式：

```text
hidefiles/
├── Directory.Build.props              # 统一版本控制与构建参数
├── HideFiles.slnx                     # 解决方案文件
├── publish.ps1                        # 自动化发布打包脚本
├── src/
│   ├── HideFiles.Core/                # 跨平台核心类库 (就地混淆、元数据编解码、规则引擎、右键菜单)
│   ├── HideFiles.Cli/                 # 命令行程序 (hidefiles.exe)
│   └── HideFiles.App/                 # 现代化 WPF GUI 客户端 (HideFilesApp.exe)
├── tests/
│   └── HideFiles.Tests/               # xUnit 单元测试与端到端集成测试 (100% 通过)
└── docs/                              # 技术方案与性能基准测试报告
```

---

## 命令行用法

```bash
# 混淆指定文件或目录
hidefiles hide <paths...> [--dry-run] [--hash-name]

# 还原指定文件或目录
hidefiles unhide <paths...> [--dry-run]

# 扫描并输出路径下文件的混淆状态
hidefiles scan [paths...]

# 一键安装/卸载 Windows 资源管理器级联折叠右键菜单
hidefiles menu install
hidefiles menu uninstall

# 查看帮助
hidefiles --help
```

---

## 构建与测试

```powershell
# 运行全部单元测试
dotnet test

# 构建整个解决方案
dotnet build -c Release

# 一键打包发布（生成 dist/*.zip 与 SHA256SUMS.txt）
.\publish.ps1
```
