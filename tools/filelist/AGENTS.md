# AGENTS.md

本文件供 AI 编码助手使用，说明本项目的结构、开发与测试流程。

## 项目概览

FileList 是一个轻量级文件目录索引与搜索 Web 服务：单 Go 二进制分发，前端为原生静态页（Alpine.js + Pico.css，零 CDN 依赖），**无前端构建步骤**。

## 目录结构

| 路径 | 说明 |
|------|------|
| `src/*.go` | 后端主包：`main.go` 入口、`server.go` HTTP/API、`indexer.go` 增量索引、`config.go` 配置、`log.go` 日志 |
| `src/internal/ops/` | 运维面板子包（编译期边界，不反向依赖主包） |
| `src/web/` | 前端资产，经 `//go:embed web/*` 编入二进制 |
| `src/web/index.html`、`src/web/static/app.js`、`app.css` | 目录浏览主页与脚本样式 |
| `src/web/static/reader.html`、`src/web/static/ops/` | 阅读器与运维面板的独立资产 |
| `src/*_test.go` | Go 单元测试与接口测试 |
| `tests/e2e/` | Playwright 端到端测试（`specs/` 用例、`fixtures.js` 夹具、`test-server.js` 服务管理） |
| `build.ps1` / `Makefile` | 构建脚本（PowerShell / Make） |
| `deploy.ps1` | 交叉编译 + SCP + 重启远程 systemd |
| `docs/` | 设计与变更文档；`temp/` 存放临时产物 |

## 环境要求

Go 1.26+；E2E 需要 Node.js 与本机 Microsoft Edge（Playwright 使用 `channel: msedge`，不下载浏览器）。

## 后端开发流程

```powershell
cd src
go build ./...          # 编译检查
go vet ./...            # 静态检查
gofmt -l .              # 格式检查（目标：无输出；需要时 gofmt -w <文件>）
go run . -config ../config.yaml   # 本地运行
```

- 构建产物统一走脚本，不要手工 `go build` 到项目根：
  - `./build.ps1`（当前平台）、`./build.ps1 -Target all`（Linux + Windows 交叉编译）、`./build.ps1 -Target linux -Clean`
  - Linux/macOS 可用 `make build` / `make build-all`
- 产物落在 `build/`；首次运行前复制 `config.sample.yaml` 为 `config.yaml`。
- 新增依赖后确保 `go.mod` / `go.sum` 一并更新。

## 前端开发流程

- 前端是**直接嵌入二进制的静态资源**，没有 npm 依赖、打包器或热重载。修改 `src/web/**` 后**必须重新编译**（`./build.ps1`）才能看到效果。
- 保持零外部 CDN 依赖：第三方库以本地文件形式放在 `src/web/static/`（如 `alpine.min.css/js`、`pico.min.css`、`qrcode.min.js`）。
- 样式与交互沿用既有约定：Pico.css 语义化标签 + Alpine.js 声明式指令，避免引入新的框架或全局状态。
- 前端只做体验优化，**安全校验一律以服务端为准**（前端二次确认不算安全边界）。
- 改动 `src/web/` 中的 HTML 模板时，注意 `src/web_placeholders_test.go` 会校验模板占位符，需同步更新。

## 测试流程

```powershell
# 1) Go 单元测试与接口测试
cd src
go test -v ./...
go test -race ./...      # 涉及索引/并发/会话时建议加跑

# 2) 端到端浏览器测试（自动编译、自动准备夹具与服务）
.\tests\run-e2e.ps1                    # 无头全量
.\tests\run-e2e.ps1 --headed           # 带界面调试
.\tests\run-e2e.ps1 --ui               # Playwright UI 运行器
.\tests\run-e2e.ps1 specs/upload.spec.js   # 单个用例集
```

- E2E 会自动检测源码是否比 `build/filelist.exe` 新并重新编译，测试结束后自动清理临时目录与进程。
- 报告与截图输出在 `temp/e2e-report/`、`tests/e2e/temp/`。
- 新增后端接口时补 Go 测试；新增或改动用户可见交互时补 `tests/e2e/specs/` 对应用例。
- 提交前确保上述测试通过；若某项无法运行，需在变更说明中注明原因。

## 代码约定

- 交流与文档使用简体中文；代码注释可用中文，标识符、日志与 commit message 使用英文。
- 所有文件使用 UTF-8；仓库启用 `.gitattributes`（`* text=auto eol=lf`），**编辑后保持 LF 行尾**，不要把整文件行尾改成 CRLF。
- 文档/报告放 `docs/`，临时与中间产物放 `temp/`，不要在项目根目录堆放临时文件。
- 重要代码变更需在 `docs/CHANGES-YYYYMMDD.md`（不存在则新建）顶部追加变更摘要，时间取本机真实时间（GMT+8）。
- 新增文档类文件时禁止覆盖同名已有文件，重名则追加日期时间戳与序号。
- 未提交到 Git 的文件在编辑或删除前，先备份到 `temp/backups/`。
- 禁止自行执行 `git commit` / `git push`，除非用户明确要求。
