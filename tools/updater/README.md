# updater — 通用极简 Windows 应用自动更新工具

单文件、零依赖的 Windows 应用程序原地自动更新工具。主程序退出前把它拉起来，由它等待主程序释放文件锁、解压更新包覆盖安装目录、保护数据配置，再重新拉起新版本。

---

## 项目版本

| 实现版本 | 目录 | 产物体积 | 特性与状态 | 详细说明与使用指南 |
| :--- | :--- | :--- | :--- | :--- |
| **Rust 版 (推荐)** | [`rust/`](file:///c:/Home/Projects/mytools/tools/updater/rust) | **~366 KB** | **主力维护**（零额外依赖、可选原生 GUI、中英文自适应） | 详见 [rust/README.md](file:///c:/Home/Projects/mytools/tools/updater/rust/README.md) |
| **Go 版** | [`go/`](file:///c:/Home/Projects/mytools/tools/updater/go) | ~2.3 MB | 历史版本（纯静默） | 详见 [go/README.md](file:///c:/Home/Projects/mytools/tools/updater/go/README.md) |

---

## 快速调用示例

```text
updater.exe --pid <PID> --zip <ZIP> --target <DIR> --launch <EXE> [options]
```

详细参数说明、各语言（Flutter / Rust / Go / C# / Electron）调用示例、`.updatekeep` 规则以及打包规范，请参阅主力版本的完整使用指南：

👉 **[Rust 版完整使用指南 (rust/README.md)](file:///c:/Home/Projects/mytools/tools/updater/rust/README.md)**
