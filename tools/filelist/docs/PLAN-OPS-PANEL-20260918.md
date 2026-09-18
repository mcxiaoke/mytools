# 运维命令面板（Ops Panel）架构设计与实施方案

> 创建时间：2026-09-18 10:08 (GMT+8)
> 版本：**v2**（依据评审意见修订）
> 状态：方案设计完成，待实施
> 前置文档：`DESIGN.md`、`PLAN-NOVEL-READER-20260916.md`

---

## 一、 背景与评审结论

### 1.1 需求还原

使用者在日常运维中存在明确的工作流断点：

> 通过 filelist 在线编辑完配置文件（如 `/etc/nginx/nginx.conf`）后，需要立刻执行 `systemctl restart xxx.service` 使其生效。当前必须切回 SSH 客户端，且要重新 `cd` 到对应目录，上下文断裂。

因此本功能的真正价值不是"能在网页上跑命令"，而是 **「改完文件 → 一键生效」的闭环**，核心差异化点是 **当前浏览目录（cwd）上下文的自动继承**。

### 1.2 三条前提约束

| 编号 | 约束 | 对方案的影响 |
| :--- | :--- | :--- |
| C1 | 当前 filelist 就是 **root systemd 部署**（`deploy/filelist-root.service`） | 无需 sudoers/polkit 绕行；真交互终端（PTY）权限上完全可行 |
| C2 | **并非不能引入第三方库**，只是避免重型库 | 放弃"纯标准库手写"，改用成熟库降低维护成本 |
| C3 | **前后端都可引入库**，避免自己造轮子 | 后端 PTY + WebSocket；前端 xterm.js，不再手写 ANSI 解析 |
| **C4** | **（本次新增）前后端都要与主功能 filelist 低耦合、模块化** | **决定包结构、前端隔离方式与集成面**，见 §2 |

### 1.3 评审确认结论

| 编号 | 事项 | 结论 |
| :--- | :--- | :--- |
| 1 | 两阶段交付 | ✅ 同意 |
| 2 | 默认模式 | ✅ `allowlist`，并**尽量纳入只读命令**（`ls`/`cat`/`grep`/`df` 等），见 §6.4 |
| 3 | `terminalToken` 独立第二因子 | ✅ 采纳 |
| 4 | 前端资产方案 | ✅ 方案 C（esbuild 一次性打包 + 产物 vendor） |
| 5 | 目录级 `snippets` 预设命令 | ❌ 不做 |
| 6 | `SameSite=Strict` Cookie 加固 | ❌ 暂不做（**故 Origin 校验成为唯一防线**，见 §5.3） |

### 1.4 结论摘要

> **最终方案：独立 Go 子包 `internal/ops`（编译期边界）+ 独立前端资产（运行时边界）+ 唯一集成面「4 个文件、约 6 行」+ 双执行后端（exec/pty）+ 统一 WebSocket + xterm.js，分两阶段交付。**

---

## 二、 低耦合与模块化设计（C4，本次重点）

### 2.1 耦合面清单：目标「4 文件 + 6 行」

低耦合不是口号，必须量化为**可核查的接触点**。本方案对主功能的全部侵入面如下：

| # | 位置 | 改动 | 性质 |
| :--- | :--- | :--- | :--- |
| 1 | `src/config.go` | `Ops` 配置块 + 3 个派生方法 + 校验 | 数据定义（无法避免） |
| 2 | `src/server.go` | `Routes()` 中 **2 行** 路由注册 | 一行挂载，无逻辑 |
| 3 | `src/server.go` | `pageHTML()` 中 **1 行** 占位符替换 | 一行注入，无逻辑 |
| 4 | `src/web/index.html` | **1 行** `<script>` + **1 个** 空 `<div>` 挂载点 | 声明式挂载点 |
| 5 | `src/web/static/app.js` | **1 行** 事件广播（cwd 变化） | 单向通知，无逻辑 |
| 6 | `src/web/static/ops/` | 全新目录，主站零改动 | 完全隔离 |

**总计：5 个文件、约 6 行侵入式改动。** 所有业务逻辑（约 1500 行）位于独立子包与独立前端目录中。

### 2.2 后端：独立 Go 子包 + 编译期边界

**原方案的缺陷**：所有 `ops_*.go` 放在 `package main`，与 `server.go`、`indexer.go` 平级。Go 的 `package main` 内部没有任何访问控制，`ops.go` 可以随手引用 `indexer` 的私有字段，反之亦然。**没有编译期边界，耦合只是"暂时看起来干净"，半年后必然腐化。**

**重构方案**：抽出独立子包 `src/internal/ops`。

```
src/
├── main.go            # 零改动
├── server.go          # 仅新增 2 行路由 + 1 行占位符 + 适配器（约 40 行）
├── config.go          # 新增 Ops 配置块
├── indexer.go         # 零改动
├── utils.go           # 零改动
├── log.go             # 零改动
└── internal/ops/      # ★ 全新独立子包，约 1200 行
    ├── ops.go         # 包文档、Config、Logger/PathResolver 接口、New()
    ├── policy.go      # 命令规范化、白名单/deny 匹配、cwd 沙箱
    ├── session.go     # SessionRegistry、生命周期、环形缓冲、审计
    ├── exec.go        # 一次性执行（跨平台）
    ├── exec_unix.go   # 进程组 kill（Unix）
    ├── exec_windows.go# 进程 kill（Windows）
    ├── pty_unix.go    # creack/pty 实现
    ├── pty_windows.go # 空实现 stub
    ├── ws.go          # WebSocket 升级、Origin 校验、帧编解码
    ├── policy_test.go
    ├── session_test.go
    └── ws_test.go
```

**为什么用 `internal/`**：Go 强制规定 `internal` 下的包只能被**同一父目录树内**的代码导入。这带来两个硬保证：

1. 外部模块无法导入它 → 不会被意外当作公共 API；
2. 它位于 `package main` 之下，天然无法反向依赖主包 → **单向依赖，编译期强制**。

**依赖方向（关键）**：

```
package main (server.go)
      │
      │  依赖注入（只传接口与纯数据）
      ▼
internal/ops  ──→  仅依赖：标准库 + creack/pty + coder/websocket
      ✗           不依赖 indexer、不依赖 Server、不依赖 webFS
```

`internal/ops` **完全不知道 filelist 的存在**。它需要的一切（cwd 允许根、配置、日志写入器）通过 `Config` 与接口注入。

### 2.3 依赖注入：接口定义在消费方

`internal/ops` 需要的两个能力，均以**接口**声明在自己包内（Go 惯例：接口定义在使用方，而非实现方）：

```go
// internal/ops/ops.go

// Logger 是 ops 对日志的最小需求。由主包注入（包装既有 log 模块）。
type Logger interface {
    Info(msg string, kv ...any)
    Warn(msg string, kv ...any)
    Error(msg string, kv ...any)
}

// PathResolver 把虚拟路径解析为磁盘路径，并校验是否在允许的根内。
// 由主包注入（包装既有 indexer.MapVirtualToReal）。
// ops 包不导入 indexer，只依赖这个接口。
type PathResolver interface {
    // Resolve 将虚拟路径转为真实磁盘路径。
    Resolve(virtualPath string) (realPath string, err error)
    // AllowedRoots 返回允许作为 cwd 的磁盘根列表。
    AllowedRoots() []string
}
```

主包侧适配器（新增 `src/ops_adapter.go`，约 40 行）：

```go
// opsLogger 把既有 log 模块适配为 ops.Logger。
type opsLogger struct{}
func (opsLogger) Info(msg string, kv ...any)  { logInfo(msg, kv...) }
func (opsLogger) Warn(msg string, kv ...any)  { logWarn(msg, kv...) }
func (opsLogger) Error(msg string, kv ...any) { logError(msg, kv...) }

// opsPaths 把既有 indexer 的路径映射适配为 ops.PathResolver。
type opsPaths struct {
    idx *Indexer
    cfg *Config
}
func (p opsPaths) Resolve(v string) (string, error) { return p.idx.MapVirtualToReal(v) }
func (p opsPaths) AllowedRoots() []string {
    if len(p.cfg.Ops.CwdRoots) > 0 {
        return p.cfg.Ops.CwdRoots
    }
    roots := make([]string, 0, len(p.cfg.Roots))
    for _, r := range p.cfg.Roots {
        roots = append(roots, r.Path)
    }
    return roots
}
```

**收益**：`internal/ops` 的单元测试可用**假实现**（`fakeResolver`、`noopLogger`）完全脱离 filelist 运行，测试快、无副作用。这是模块化最实际的回报。

### 2.4 路由挂载：一行，且条件注册

```go
// server.go —— Routes() 中新增
if s.cfg.OpsEnabled() {
    mux.Handle("/api/ops/", s.opsHandler)  // ops.New(...).Handler()
}
```

**要点**：
- **条件注册**：`ops` 未启用时**根本不注册路由**，请求返回 404（而非 403）。未启用时攻击面为零，也不暴露"这里有个被关闭的功能"；
- `http.ServeMux` 的 `/api/ops/` 是子树匹配，`internal/ops` 内部用自己的子 mux 处理 `/ws` 等子路径，主包无需关心；
- 路由挂在 `authMiddleware` 之内（`Handler()` 中间件链已覆盖），**自动继承全局 token 鉴权**，无需重复实现——这是当前架构给的红利。

### 2.5 前端：三层隔离

**原方案的缺陷**：在 `index.html` 里直接写工具栏按钮 + 抽屉 DOM + Alpine 组件方法。这会让 `index.html`（已 640 行）与 `app.js`（已 1025 行）继续膨胀，且主站与 ops 的 DOM 互相纠缠。

**重构方案**：三层隔离，**主站只留一个空容器 + 一行脚本 + 一行广播**。

#### 第 1 层：DOM —— 一个空挂载点

`index.html` 中仅新增：

```html
<!-- 运维面板挂载点（未启用时容器为空，零影响） -->
<div id="ops-panel-root" data-ops-base="__FILELIST_BASE__"></div>
<script src="__FILELIST_BASE__/static/ops/ops.js?v=__FILELIST_VERSION__" defer></script>
```

**关键**：ops 的 DOM 由 `ops.js` **在运行时自己创建**并注入 `#ops-panel-root`，而非写在 `index.html` 里。主站模板完全不包含 ops 的结构，主站改布局不会波及 ops，反之亦然。

#### 第 2 层：JS —— 独立 Alpine 组件 + 独立注册

`ops.js` 自带 Alpine 注册，不修改 `app.js`：

```js
// ops.js —— 完全独立，不引用 app.js 的任何内部状态
document.addEventListener('alpine:init', () => {
  Alpine.data('opsPanel', () => ({ /* ops 自己的全部状态与方法 */ }));
});
```

`index.html` 中**不出现** `x-data="opsPanel"`；组件实例由 `ops.js` 自行挂载到 `#ops-panel-root`（写入 `x-data` 属性后调用 `Alpine.initTree`）。这样 `app.js` 与 `ops.js` 之间**零符号依赖**。

#### 第 3 层：通信 —— 事件总线，而非直接调用

cwd 跟随是本功能的核心，也是最容易产生硬耦合的地方。**绝不能**让 `ops.js` 直接读 `app.js` 的 `this.path`。

**方案：基于 `window` 的自定义事件总线**（零依赖、天然解耦）。

主站侧（`app.js` 新增 **1 行**，位于 `navigate()` 成功更新路径之后）：

```js
// 广播当前目录变化，供可选模块（如运维面板）订阅
window.dispatchEvent(new CustomEvent('filelist:navigate', { detail: { path: this.path } }));
```

ops 侧（`ops.js` 内）：

```js
window.addEventListener('filelist:navigate', (e) => {
  if (!this.cwdLocked) this.cwd = e.detail.path;   // 锁定状态下不跟随
});
```

**为什么用事件总线**：

| 特性 | 说明 |
| :--- | :--- |
| 单向依赖 | 主站只负责"广播"，不知道谁在听；ops 只负责"订阅"，不依赖主站内部 |
| 可选性 | ops 未启用时该事件无人订阅，**零开销**；主站代码无需任何条件判断 |
| 可测试 | ops 可自行 `dispatchEvent` 模拟导航，无需真实主站 |
| 契约极小 | 唯一契约是事件名 `filelist:navigate` 与 `detail.path` 字段，**两个符号** |

**契约固化**：事件名与 payload 结构需写入 `DESIGN.md`，作为主站与可选模块之间的稳定接口。未来若有其他可选模块（任务面板、日志查看器等），复用同一事件即可。

#### 前端目录结构

```
src/web/static/
├── app.js / app.css / alpine.min.js / pico.min.css   # 主站，仅 1 行改动
├── reader.html                                        # 既有阅读器
└── ops/                                               # ★ 全新独立目录
    ├── ops.js         # Alpine 组件 + 事件订阅 + 挂载逻辑
    ├── ops.css        # 面板与终端样式（含暗色适配）
    ├── ops.html       # /ops 独立页模板（阶段二）
    └── vendor/xterm/  # vendored xterm 产物（阶段二）
        ├── xterm.bundle.js
        ├── xterm.css
        └── README.md  # 版本、打包命令、升级步骤
```

### 2.6 配置：集中的 Ops 块

所有 ops 配置收在单一 `ops:` 块下（见 §6），主配置的 `server`/`index`/`roots` 等既有块**零改动**。派生方法与校验集中在 `config.go` 的一处区域，便于审查。

### 2.7 耦合度自检清单

实施完成后，用以下清单核查是否达标：

| 检查项 | 达标标准 |
| :--- | :--- |
| `internal/ops` 是否导入了 `indexer`/`Server`/`webFS`？ | ❌ 不应有（用 `go list -deps ./internal/ops` 核查） |
| `app.js` 中 `ops` 相关代码行数 | ≤ 1 行（仅事件广播） |
| `ops.js` 是否引用 `filelistApp` 等主站符号？ | ❌ 不应有（`base` 从 `data-ops-base` 读取） |
| `index.html` 中 ops 相关行数 | ≤ 3 行 |
| ops 单元测试是否需要启动 filelist 服务？ | ❌ 不需要（用假 Resolver） |
| 删除 `internal/ops/` 与 `static/ops/` 后主站能否正常构建运行？ | ✅ 能（仅剩 5 处声明式残迹） |

**可卸载性验证**（最强指标）：删除 `internal/ops/`、`static/ops/`，移除 5 处集成点后，主站应能无残留地正常构建运行。

---

## 三、 关键决策与理由

### 3.1 命名与路由

`webshell` 在安全领域特指攻击者投放的后门脚本。使用该命名会带来实际成本：内网 IDS / 企业 EDR / 云 WAF 会对含 `shell` 字样的路径产生告警；路由 `/shell` 是扫描器高频探测字典项。

**结论**：对外称「运维面板（Ops Panel）」，路由 `/ops`，配置块 `ops:`，API 前缀 `/api/ops/`。

### 3.2 形态：抽屉为主，独立页为辅

| 维度 | 小说阅读器 | 运维面板 |
| :--- | :--- | :--- |
| 交互性质 | 内容消费 | 操作反馈 |
| 上下文依赖 | 零（独立文件） | **强（当前浏览目录）** |
| 会话时长 | 长，沉浸 | 短，高频 |

- 独立标签页：必须重新 `cd`，上下文断裂；
- 模态框：`tail -f` 等长任务会把人锁在弹窗里。

**结论**：主形态为右侧可折叠抽屉（cwd 自动跟随）；提供「⤢ 弹出到新窗口」跳转 `/ops?cwd=<虚拟路径>` 用于长任务；两者共用同一后端 API 与前端逻辑。

### 3.3 传输协议：WebSocket 统一

- 交互式 PTY **必须**双向实时通信，SSE 无法承载键盘输入；
- 既然必须引入 WebSocket，exec 复用同一通道比维护 SSE + WebSocket 两条路径**更省维护成本**（契合 C3）；
- exec 输出是单向流，用 WebSocket 承载无额外代价。

**结论**：单端点 `/api/ops/ws`，首帧声明会话类型。

### 3.4 执行后端：双后端

| 后端 | 适用场景 | 输出特性 |
| :--- | :--- | :--- |
| `exec`（一次性） | 快捷命令、`systemctl restart`、`ls`、`grep` | 无回显噪声、无 ANSI 转义、**可拿到真实退出码** |
| `pty`（交互式） | `tail -f`、`journalctl -f`、`vim`、`top` | 有回显、含 ANSI 色彩、需 `$?` 探测退出码 |

用 PTY 跑一次性命令会遇到：输出混入 shell 回显、`\r\n` 行尾污染、拿不到真实退出码（需 `; echo $?` 哨兵）、`systemctl` 检测到 tty 后调起 `less` 分页器而挂起。用管道跑交互命令则 `top`/`vim` 直接不可用。

**结论**：双后端共享会话注册表与传输层；`exec` 为默认。

### 3.5 库选型

#### 后端

| 库 | 版本 | 许可 | 依赖量 | 选型理由 |
| :--- | :--- | :--- | :--- | :--- |
| `github.com/creack/pty` | v1.1.24 | MIT | 6 imports，零传递依赖 | Unix PTY 事实标准；Moby/Docker 长期生产使用；API 极简 |
| `github.com/coder/websocket` | v1.8.15 | ISC | **零依赖** | 原生 `context.Context`；并发写安全；内置 ping/pong；活跃维护 |

**排除项**：
- `gorilla/websocket` —— 原仓库 2022 年末已归档，安全补丁依赖社区分支，新项目不应构建在已归档依赖上；
- `golang.org/x/net/websocket` —— 自 2016 年起废弃；
- Windows ConPTY 库（`aymanbagabas/go-pty`、`Kodecable/crosspty`）—— 依赖 `microsoft/hcsshim`，明显更重，而部署目标是 Linux，用构建标签隔离即可；
- `x/crypto/ssh` 路线 —— 需额外管理主机密钥与凭据协商，复杂度高于收益。

**依赖总量**：`go.mod` 从 1 个直接依赖（`yaml.v3`）增至 3 个，且两个新库均无传递依赖（或极少），符合 C2「避免重型库」。

#### 前端

| 库 | 版本 | 用途 |
| :--- | :--- | :--- |
| `@xterm/xterm` | v6.x | 终端仿真与渲染（VT100 兼容、CJK 宽字符、ANSI 色彩） |
| `@xterm/addon-fit` | v0.11.x | 尺寸自适应容器 |
| `@xterm/addon-web-links` | v0.12.x | 终端内 URL 可点击 |

**`@xterm/addon-webgl` 默认不加载**：GPU 加速在大滚屏下性能更好，但 WebGL 上下文在老旧集显/远程桌面/虚拟机下会失败。作为可选开关，加载失败时静默回退默认 DOM 渲染器。

**为何不自研 ANSI 解析**：即使只支持 16 色 + 光标控制，也要处理 CSI/SGR/OSC 状态机、CJK 宽字符占两列、`\r` 覆写（进度条）、滚动区域，约 300~500 行且长期有边界 bug。这正是 C3 要求避免的。

### 3.6 前端资产交付：方案 C

`@xterm/xterm` v6 官方产物为 CJS（`lib/xterm.js`）与 ESM（`lib/xterm.mjs`），**不再提供可直接 `<script src>` 的 UMD 单文件**。项目当前无任何 npm 工具链，静态资源经 `//go:embed web/*` 内嵌。

| 方案 | 做法 | 评价 |
| :--- | :--- | :--- |
| A. 运行时引 CDN | `<script src="https://cdn...">` | ❌ 违背"零外部 CDN 依赖"原则；内网可能无法访问 |
| B. xterm v5.x（旧版含 UMD） | 直接 `<script>` 引用 | ⚠️ 锁定旧大版本，依赖上游打包格式 |
| **C. esbuild 打包为 IIFE 后 vendor（采用）** | 产出 `xterm.bundle.js` + `xterm.css` 提交到 `static/ops/vendor/xterm/` | ✅ 不依赖上游格式；构建与运行期均零工具链；可复现 |

**实施细节**：
- 打包命令固化为独立可选目标（`make vendor-xterm`），**不进入默认构建流程**；
- 产物提交进 Git，clone 后 `go build` 直接可用，贡献者无需 Node；
- 升级流程写入 `vendor/xterm/README.md`：改版本 → 重打包 → 提交产物 → 记录变更；
- `@xterm/xterm` 是纯前端资产，**不参与 `go.mod`**，需在 `DESIGN.md` 登记，避免后来者误判为 CDN 依赖。

### 3.7 跨平台：构建标签隔离

开发机为 Windows，部署目标为 Linux。`creack/pty` 在 Windows 上所有函数返回 `ErrUnsupported`（其包注释明确说明目标平台为 Unix 系）。

| 文件 | 构建标签 | 行为 |
| :--- | :--- | :--- |
| `pty_unix.go` | `//go:build !windows` | 真实 PTY 实现 |
| `pty_windows.go` | `//go:build windows` | 返回 `errPtyUnsupported`；启动时日志提示"当前平台不支持交互终端，仅启用一次性命令" |
| `exec_unix.go` / `exec_windows.go` | 各自标签 | 进程组 kill：Unix 用 `Setpgid`；Windows 降级为 `Process.Kill` |

前端在 `ptyAvailable === false` 时隐藏「交互终端」入口。保证 Windows 下 `go build` 与 `go test ./...` 全部通过。

---

## 四、 架构与模块设计

### 4.1 整体架构

```
┌──────────────────────────────────────────────────────────────┐
│                         浏览器                                │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  static/ops/ops.js  (独立 Alpine 组件 opsPanel)          │  │
│  │  ┌──────────────┐  ┌────────────────────────────────┐  │  │
│  │  │ 命令面板      │  │ xterm.js 终端视图              │  │  │
│  │  └──────┬───────┘  └───────────────┬────────────────┘  │  │
│  │         └──────────┬───────────────┘                   │  │
│  │                    ▼                                   │  │
│  │           单一 WebSocket 客户端 + 帧编解码               │  │
│  └────────────────────┬───────────────────────────────────┘  │
│         ▲             │                                       │
│         │ 订阅        │ ws://host/api/ops/ws                  │
│  ┌──────┴──────────┐  │                                       │
│  │ app.js 广播      │  │   ← 唯一前端契约：                     │
│  │ filelist:navigate│  │     'filelist:navigate' 事件           │
│  └─────────────────┘  │                                       │
└───────────────────────┼──────────────────────────────────────┘
                        │
┌───────────────────────▼──────────────────────────────────────┐
│  package main (server.go)                                     │
│    └─ authMiddleware（既有，自动覆盖 ops）                     │
│    └─ ops_adapter.go（约 40 行：Logger / PathResolver 适配）    │
│    └─ mux.Handle("/api/ops/", opsHandler)   ← 1 行             │
└───────────────────────┬──────────────────────────────────────┘
                        │ 接口注入（Logger, PathResolver）
┌───────────────────────▼──────────────────────────────────────┐
│  internal/ops  ★ 独立子包，不知道 filelist 存在                 │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  ws.go —— 升级 / Origin 校验 / 帧路由 / 心跳             │  │
│  └────────────────────┬───────────────────────────────────┘  │
│                       ▼                                       │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  session.go —— SessionRegistry（会话注册表 + 回滚缓冲）   │  │
│  └───────┬────────────────────────────────┬───────────────┘  │
│          ▼                                ▼                   │
│  ┌──────────────────┐            ┌──────────────────────┐    │
│  │ exec.go          │            │ pty_unix.go          │    │
│  │ 管道 + 退出码     │            │ creack/pty + Setsize │    │
│  └────────┬─────────┘            └──────────┬───────────┘    │
│           └────────────┬────────────────────┘                 │
│                        ▼                                      │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  policy.go —— 白名单/deny 匹配 + cwd 沙箱校验            │  │
│  └────────────────────┬───────────────────────────────────┘  │
│                       ▼                                       │
│          Logger / PathResolver（接口，由主包实现）              │
└──────────────────────────────────────────────────────────────┘
```

### 4.2 模块职责

| 模块 | 文件 | 职责 |
| :--- | :--- | :--- |
| 包入口 | `internal/ops/ops.go` | 包文档、`Config`、`Logger`/`PathResolver` 接口、`New()`、`Handler()` |
| 策略 | `internal/ops/policy.go` | 命令规范化、白名单/deny 匹配、cwd 沙箱校验、默认规则集 |
| 会话 | `internal/ops/session.go` | `SessionRegistry`、生命周期、环形缓冲、空闲回收、并发上限、审计日志 |
| 一次性执行 | `internal/ops/exec*.go` | `exec.CommandContext` + 管道、超时与进程组 kill、输出截断、退出码 |
| 交互执行 | `internal/ops/pty_*.go` | PTY 创建、读写泵、`Setsize` 窗口同步、信号转发；Windows stub |
| 传输 | `internal/ops/ws.go` | WebSocket 升级、Origin 校验、帧编解码、心跳、重连重放 |
| 主包适配 | `src/ops_adapter.go` | 把既有 `log` 与 `indexer` 适配为 ops 接口（约 40 行） |
| 前端组件 | `static/ops/ops.js` | Alpine 组件、事件订阅、DOM 自建、xterm 挂载 |
| 前端样式 | `static/ops/ops.css` | 面板与终端样式、暗色适配 |
| 终端资产 | `static/ops/vendor/xterm/` | vendored xterm 产物 |

### 4.3 会话模型

```go
type SessionKind string // "exec" | "pty"

type Session struct {
    ID        string
    Kind      SessionKind
    CWD       string        // 真实磁盘路径
    CreatedAt time.Time
    LastSeen  time.Time
    Cmd       *exec.Cmd     // exec 模式
    PTY       *os.File      // pty 模式（master 端）
    Ring      *RingBuffer   // 回滚缓冲，重连重放
    Exit      *ExitInfo     // 退出码 / 耗时 / 是否截断
    mu        sync.Mutex
}
```

**关键设计**：会话状态保存在服务端，浏览器刷新或网络抖动后可通过 `sessionId` 重新 `attach` 并重放回滚缓冲，**不丢失 `tail -f` 的上下文**。这是相比"每次请求新建进程"的关键体验差异。

---

## 五、 通信协议与安全

### 5.1 端点与帧

端点：`GET /api/ops/ws`（WebSocket 升级），所有帧为 JSON 文本。

**客户端 → 服务端**

| 帧 | 字段 | 说明 |
| :--- | :--- | :--- |
| `attach` | `sid?` | 附加到已有会话；省略则新建 |
| `exec` | `cmd`, `cwd`, `sid?` | 执行一次性命令 |
| `openpty` | `cwd`, `cols`, `rows`, `token` | 新建交互终端（需 `terminalToken`） |
| `input` | `data` | 键盘输入（PTY 模式） |
| `resize` | `cols`, `rows` | 终端尺寸变化 |
| `signal` | `name` | 发送信号，如 `INT` |
| `close` | `sid` | 主动结束会话 |

**服务端 → 客户端**

| 帧 | 字段 | 说明 |
| :--- | :--- | :--- |
| `ready` | `sid`, `kind`, `cwd`, `ptyAvailable` | 会话就绪 |
| `out` | `data` | 输出（exec 为纯文本；pty 含 ANSI 字节流） |
| `exit` | `code`, `durationMs`, `truncated` | 进程退出 |
| `err` | `code`, `msg` | 错误（`denied_by_policy` / `cwd_out_of_sandbox` / `concurrency_limit`） |
| `pong` | — | 心跳响应 |

### 5.2 安全模型

root 部署 + 真终端 = **通过浏览器可达的完整 root shell**。这是本项目引入的**风险等级最高**的功能，默认全部关闭。

#### L1 双前置条件（fail-closed）

**必须 `ops.enabled: true` 且 `server.token` 非空，功能才真正生效**。若误开 `ops.enabled` 却未配 `token`，服务端拒绝启用并在日志中告警。沿用既有 `ManageDelete()` 的设计语言。

#### L2 独立第二因子（终端模式专用）

`ops.terminalToken`：进入**交互式终端**时须额外输入此口令，与全局 `server.token` 解耦（沿用 `manage.deleteToken` 模式）。快捷命令（exec）仅需全局 token。

理由：快捷命令受白名单约束，爆炸半径可控；交互终端等价于完整 shell，需要显式二次确认意图。

#### L3 WebSocket Origin 校验（**唯一防线，最高优先级**）

**本次评审决定不启用 `SameSite=Strict` Cookie**，因此本层从"重要"升级为**唯一防线**。

已核实的既有实现事实（`server.go:345-352`）：token Cookie 当前为 `SameSite=Lax`。而 **Lax 不阻止跨站 WebSocket 连接**（WebSocket 握手是 GET，且 Lax 仅对顶层导航放行，对 WS 升级的判定在浏览器实现上并不可靠）。因此：

> **若不校验 Origin，使用者访问的任何恶意网页都能静默连上内网 filelist 的 `/api/ops/ws`，直接拿到 root shell。**

这是 Cross-Site WebSocket Hijacking（CSWSH），是本功能最现实的攻击面。**强制措施（缺一不可）**：

1. `websocket.AcceptOptions.OriginPatterns` 配置**显式白名单**，默认从请求 `Host` 推导，可由配置覆盖（应对反代场景）；
2. WS 升级请求必须**显式携带有效 token**（查询参数或子协议头），**不依赖 Cookie 隐式通过**；
3. `Origin` 缺失时的处理策略需明确（建议：拒绝，除非配置显式允许空 Origin）；
4. 该策略必须有**专门的测试用例**覆盖（见 §8.2）。

> 若未来启用 `SameSite=Strict`，可放宽第 2 条，但 Origin 校验仍应保留作为纵深防御。

#### L4 命令白名单（默认模式）

`deny` 内置默认危险规则（`rm -rf /`、`mkfs`、`dd if=`、fork bomb、`shutdown`/`reboot`、`> /dev/sd*`），显式写 `deny: []` 才取消——与既有 `defaultExcludeFiles` 行为风格一致。

匹配前需规范化命令（去首尾空白、折叠连续空白、统一大小写），并**拒绝含拼接符的命令**（`;`、`&&`、`||`、`|`、`` ` ``、`$(`、`>`、`<`），防绕过。

**明确认知边界**：白名单是真正的安全边界；cwd 沙箱不是。

#### L5 cwd 沙箱与资源限制

**cwd 沙箱**：工作目录限制在 `roots` 或独立配置 `ops.cwdRoots` 内。必须明确：**这只约束工作目录，`cat /etc/passwd` 依然可读**。故 L4 才是边界。

**资源限制**（防把服务器搞死，也防自伤）：

| 项目 | 默认值 | 说明 |
| :--- | :--- | :--- |
| 单命令超时 | `30s` | 超时后 SIGTERM → 3s 宽限 → SIGKILL **整个进程组** |
| 输出上限 | `1MB` | 超出截断并标记，防 `yes`/`cat /dev/zero` 打爆内存 |
| 并发会话上限 | `2` | 防自伤 |
| 空闲会话回收 | `30m` | 无输入超时自动销毁 |
| 回滚缓冲上限 | `256KB`/会话 | 重连重放用，超出丢最旧 |
| 环境变量 | **白名单透传** | 仅 `PATH`/`HOME`/`LANG`/`TERM`/`USER`；**绝不继承服务进程完整 env** |

**进程组 kill 必需**：Linux 下必须 `SysProcAttr{Setpgid: true}`，kill 时对 `-pgid` 发信号。否则 `bash -c "a | b"` 的子进程会成孤儿泄漏。

**环境变量白名单必需**：服务进程 env 含 `server.token`、`manage.deleteToken`、`ops.terminalToken`。若直接继承，跑一个 `env` 就能读走全部凭据。

#### L6 审计日志

每次执行记录结构化日志（经注入的 `Logger` 接口，落既有 `log` 模块）：

```json
{"time":"2026-09-18T10:08:12+08:00","event":"ops.exec","clientIP":"192.168.1.23",
 "cwd":"/data/nginx","command":"systemctl reload nginx","mode":"allowlist",
 "exitCode":0,"durationMs":412,"truncated":false}
```

PTY 会话额外记录 `sessionStart`/`sessionEnd`。这是出事后**唯一**能回溯的依据。

### 5.3 反代注意事项

```nginx
location /api/ops/ws {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;          # 供 Origin 推导
    proxy_buffering off;                  # 关键：否则输出被缓冲，实时性丧失
    proxy_read_timeout 3600s;             # 关键：否则 60s 空闲即被切断
    proxy_send_timeout 3600s;
}
```

Caddy 默认已正确处理 WebSocket，但需确认无 `flush_interval` 干扰。**反代场景下 Origin 与 Host 可能不一致，需通过 `ops.originPatterns` 显式配置**。

---

## 六、 配置设计

### 6.1 配置样例（追加到 `config.sample.yaml`）

```yaml
# Ops Panel — 运维命令面板（可选能力，默认关闭）
# ⚠️ 安全提示：本功能允许通过浏览器执行服务器命令。在 root 部署下，
#    交互式终端等价于完整 root shell。请务必设置 server.token，
#    并在交互终端模式下配置独立的 terminalToken。
ops:
  enabled: false           # 主开关。必须同时设置 server.token 才会真正生效
  mode: allowlist          # allowlist（默认，推荐）| free（任意命令，高风险）
  terminalToken: ""        # 交互式终端的独立第二因子。为空则禁用终端模式
  cwdRoots: []             # 允许的工作目录根（空 = 复用 roots 的磁盘路径）
  originPatterns: []       # WS Origin 白名单（空 = 从请求 Host 推导）
  timeout: 30s             # 单条命令超时
  maxOutput: 1MB           # 单会话输出上限，超出截断
  maxSessions: 2           # 并发会话上限
  idleTimeout: 30m         # 空闲会话自动回收
  scrollbackBytes: 256KB   # 断线重连回滚缓冲上限
  allow: []                # 追加到内置只读命令白名单之上（见 6.4）
  deny: []                 # 全局拒绝（free 模式同样生效）。空 = 使用内置默认危险规则
```

> 注意：**不含 `snippets`**（评审第 5 项决定不做）。

### 6.2 Go 配置结构

```go
Ops struct {
    Enabled        bool     `yaml:"enabled"`
    Mode           string   `yaml:"mode"`            // allowlist | free
    TerminalToken  string   `yaml:"terminalToken"`
    CwdRoots       []string `yaml:"cwdRoots"`
    OriginPatterns []string `yaml:"originPatterns"`
    Timeout        string   `yaml:"timeout"`
    MaxOutput      string   `yaml:"maxOutput"`
    MaxSessions    int      `yaml:"maxSessions"`
    IdleTimeout    string   `yaml:"idleTimeout"`
    Scrollback     string   `yaml:"scrollbackBytes"`
    Allow          []string `yaml:"allow"`           // 追加项
    Deny           []string `yaml:"deny"`            // 空 = 用内置默认
} `yaml:"ops"`
```

### 6.3 派生方法

```go
// OpsEnabled 报告运维面板是否真正可用。
// fail-closed：必须显式启用且全局 token 非空，否则整体拒绝。
func (c *Config) OpsEnabled() bool {
    return c.Ops.Enabled && strings.TrimSpace(c.Server.Token) != ""
}

// OpsTerminalEnabled 报告交互式终端是否可用（需独立第二因子）。
func (c *Config) OpsTerminalEnabled() bool {
    return c.OpsEnabled() && strings.TrimSpace(c.Ops.TerminalToken) != ""
}

// OpsFreeMode 报告是否处于无白名单的任意命令模式。
func (c *Config) OpsFreeMode() bool {
    return c.OpsEnabled() && strings.EqualFold(strings.TrimSpace(c.Ops.Mode), "free")
}
```

### 6.4 内置只读命令白名单（评审第 2 项）

评审要求「只读命令比如 `ls` 之类的应该尽量都加进来」。设计如下：

**原则**：内置一份**只读、无副作用、无参数注入风险**的命令白名单作为默认值，使用者的 `ops.allow` 在其**之上追加**（而非替换）。这样开箱即有基本可用性，无需手写一长串正则。

| 类别 | 命令 | 正则（要点） |
| :--- | :--- | :--- |
| 目录列举 | `ls` | `^ls( -[a-zA-Z]+)*( [\w./\-*?]+)*$` |
| 查看内容 | `cat`, `head`, `tail`, `less`(禁用), `wc` | `^(cat|head|tail|wc)( -[a-zA-Z0-9]+)*( [\w./\-]+)+$` |
| 搜索 | `grep`, `rg`, `find`(限深度) | `^(grep|rg)( -[a-zA-Z]+)*( [^;&|`$()]+)+$` |
| 文件信息 | `stat`, `file`, `du`, `df`, `tree` | `^(stat|file|du|df|tree)( -[a-zA-Z]+)*( [\w./\-]+)*$` |
| 进程/系统 | `ps`, `top`(禁用), `uptime`, `free`, `uname`, `hostname`, `whoami`, `id`, `date` | 无参数或仅 `-` 开头简单标志 |
| 网络 | `ss`, `netstat`, `ip`, `ping`(限次) | `^(ss|netstat|ip) (addr|route|link|...)$` |
| 服务只读 | `systemctl status\|is-active\|list-units\|show`, `journalctl -n/-u` | 显式枚举子命令 |
| 容器只读 | `docker ps\|images\|logs\|inspect`, `docker compose ps\|logs` | 显式枚举子命令 |
| 版本控制只读 | `git status\|log\|diff\|show\|branch\|remote` | 显式枚举子命令 |

**关键约束**：

1. **所有内置规则均不含拼接符**。匹配器在任何规则之前先做一次「拼接符扫描」，命中 `;`、`&&`、`||`、`|`、`` ` ``、`$(`、`>`、`<`、`&`、`\n` 的命令**直接拒绝**，不进入白名单匹配。这比逐条正则防御更可靠；
2. **明确禁用分页器**：`less`、`more`、`top`、`vi` 等交互式工具**不纳入 exec 白名单**（会挂起等待输入）。它们是 PTY 模式的目标场景；
3. **`journalctl` 必须显式加 `--no-pager`**，否则在非 tty 环境下行为不一致；
4. **内置白名单与使用者 `allow` 的合并方式**：`内置只读白名单 ∪ ops.allow`。若使用者需要收紧，用 `deny` 排除（deny 优先级最高）；
5. **`mode: free` 时**：内置白名单不再生效，仅 `deny` 约束。

> 设计意图：让「开箱即用」与「默认安全」同时成立——默认就能 `ls`、`cat`、`journalctl -u` 看日志，但不会意外放开 `rm`。

### 6.5 校验规则

| 条件 | 处理 |
| :--- | :--- |
| `ops.enabled: true` 但 `server.token` 为空 | **拒绝启用**，日志醒目警告，面板入口不渲染 |
| `ops.mode` 非 `allowlist`/`free` | 报错退出 |
| `ops.mode: free` | 每次启动输出高风险警告 |
| `terminalToken` 非空但等于 `server.token` | 警告：第二因子应独立设置 |
| `ops.cwdRoots` 中路径不存在 | 警告但继续（与 roots 处理一致） |
| `ops.originPatterns` 为空且反代场景 | 警告：建议显式配置 |

### 6.6 前端配置注入

沿用既有占位符机制（`__FILELIST_OPS__`），在 `pageHTML()` 中注入：

```go
opsConfigJSON, _ := json.Marshal(map[string]any{
    "enabled":      s.cfg.OpsEnabled(),
    "terminal":     s.cfg.OpsTerminalEnabled(),
    "mode":         s.cfg.Ops.Mode,
    "ptyAvailable": ptyAvailable,   // 由构建标签决定
    "maxOutput":    s.cfg.Ops.MaxOutput,
})
```

**注意**：`terminalToken` **绝不能**下发到前端。前端只提示"需要口令"并提交给服务端校验。

---

## 七、 前端交互设计

### 7.1 抽屉布局

```
┌─────────────────────────────────────────────┬──────────────────┐
│  FileList 工具栏                             │  运维面板  ⤢  ✕  │
│  [上传] [视图] [🔧 运维]                      ├──────────────────┤
│                                              │ 模式: [命令|终端] │
│  📁 nginx.conf                               ├──────────────────┤
│  📁 sites-available/                         │ cwd: /data/nginx │
│  📄 index.html                               │      🔒 跟随中   │
│                                              ├──────────────────┤
│                                              │ $ systemctl …    │
│                                              │ ● active (run)   │
│                                              │ exit 0 · 412ms   │
│                                              ├──────────────────┤
│                                              │ [命令输入]  ⏹ ⏎  │
└─────────────────────────────────────────────┴──────────────────┘
```

> 「🔧 运维」按钮由 `ops.js` **动态注入**工具栏（`ops.js` 在启动时查找 `.toolbar-left` 并追加按钮），`index.html` 无需包含该按钮——进一步降低耦合。

### 7.2 交互清单

| 交互 | 说明 |
| :--- | :--- |
| **cwd 自动跟随** | 订阅 `filelist:navigate` 事件；提供 🔒 锁定按钮暂停跟随 |
| **命令历史** | `↑`/`↓` 翻历史，按 cwd 分组存 `localStorage`；仅本地，不上传 |
| **模式切换** | 「命令」（exec，默认）与「终端」（pty，需口令） |
| **中断** | ⏹ 发送 `signal: INT`；超时由服务端兜底 |
| **退出码展示** | `exit 0 · 412ms`；非零用红色标记 |
| **输出截断提示** | 达到 `maxOutput` 时显示"输出已截断" |
| **断线重连** | 网络抖动后自动重连并重放回滚缓冲，提示"已恢复会话" |
| **尺寸自适应** | 抽屉宽度变化 → `fitAddon.fit()` → 发送 `resize`（防抖 100ms） |
| **主题适配** | 跟随主站暗色模式，xterm 主题由 CSS 变量驱动 |
| **危险命令二次确认** | `deny` 命中时前端弹窗确认（**仅防手滑，非安全边界**） |

### 7.3 默认模式

**默认「命令」模式**。理由：主场景（`systemctl restart`）在 exec 下体验更好（输出干净、有退出码、不被分页器卡住）；PTY 需第二因子，作为进阶操作更符合预期；exec 跨平台可用，终端模式仅 Unix。

---

## 八、 分步实施路线图

### 阶段一：一次性命令面板（MVP）

| 步骤 | 内容 | 预估量级 |
| :--- | :--- | :--- |
| 1 | `config.go`：`Ops` 配置块 + 派生方法 + 校验 + 内置只读白名单 | ~180 行 |
| 2 | `internal/ops/ops.go`：包文档、`Config`、`Logger`/`PathResolver` 接口、`New()`、`Handler()` | ~120 行 |
| 3 | `internal/ops/policy.go`：命令规范化、拼接符扫描、白名单/deny 匹配、cwd 沙箱 | ~200 行 |
| 4 | `internal/ops/session.go`：`SessionRegistry`、生命周期、环形缓冲、限流、审计 | ~280 行 |
| 5 | `internal/ops/exec*.go`：管道执行、超时、进程组 kill、退出码 | ~200 行 |
| 6 | `internal/ops/ws.go`：WS 升级、**Origin 校验**、帧编解码、心跳、重连重放 | ~260 行 |
| 7 | `src/ops_adapter.go`：`Logger`/`PathResolver` 适配器 | ~40 行 |
| 8 | `server.go`：条件注册路由 + 占位符注入 | ~10 行 |
| 9 | `static/ops/ops.js` + `ops.css`：Alpine 组件、DOM 自建、工具栏按钮注入、事件订阅、历史、结果渲染（**不含 xterm**） | ~380 行 |
| 10 | `index.html`：1 个空挂载点 + 1 行脚本 | 2 行 |
| 11 | `app.js`：1 行事件广播 | 1 行 |
| 12 | 测试：`policy_test.go`、`session_test.go`、`ws_test.go`（含 CSWSH 用例） | ~300 行 |
| 13 | 文档：`README.md`、`config.sample.yaml`、`DESIGN.md`（含事件契约）、`CHANGES-20260918.md` | ~280 行 |

**阶段一验收**：能在浏览器改完 `/data/nginx/nginx.conf` → 切到面板（cwd 已自动跟随）→ 点「重载 Nginx」→ 看到 `exit 0`。**且**：删除 `internal/ops/` 与 `static/ops/` 后主站仍能正常构建运行（可卸载性验证）。

### 阶段二：交互式终端

| 步骤 | 内容 | 预估量级 |
| :--- | :--- | :--- |
| 1 | `make vendor-xterm`：esbuild 打包为 IIFE，产物提交到 `static/ops/vendor/xterm/` | 脚本 ~40 行 |
| 2 | `internal/ops/pty_unix.go`：`creack/pty` 集成、读写泵、`Setsize`、信号转发 | ~220 行 |
| 3 | `internal/ops/pty_windows.go`：stub + `errPtyUnsupported` | ~25 行 |
| 4 | `ws.go` 扩展：`openpty`/`input`/`resize`/`signal` + `terminalToken` 校验 | ~120 行 |
| 5 | `ops.js` 扩展：xterm 挂载、fit addon、尺寸同步、口令流程 | ~200 行 |
| 6 | `/ops` 独立页（弹出窗口形态） | ~100 行 |
| 7 | 补充测试：PTY 生命周期、resize、会话回收 | ~150 行 |

**阶段二验收**：浏览器终端运行 `journalctl -fu myapp`，断网 10 秒恢复后仍连续输出；`vim` 可正常编辑保存。

---

## 九、 测试与验证规划

### 9.1 策略单元测试（`policy_test.go`）

| 类别 | 用例 |
| :--- | :--- |
| 内置只读白名单 | `ls -la /data`、`cat /data/x.conf`、`journalctl -u nginx --no-pager` 命中 |
| 拼接符拒绝 | `ls; rm -rf /`、`ls && rm x`、`ls \| rm x`、`ls $(rm x)`、`` ls `rm x` ``、`ls > /etc/passwd` 全部拒绝 |
| 绕过尝试 | 多余空白、大小写变体、前导 `\`、路径形式 `/bin/systemctl`、`sh -c "..."` |
| deny 优先级 | deny 命中时即使 allow 也命中仍拒绝；默认 deny 生效；`deny: []` 取消 |
| free 模式 | 内置白名单失效，仅 deny 约束 |
| cwd 沙箱 | 合法通过；`../` 越界拒绝；软链接逃逸拒绝；cwd 不存在报错 |

### 9.2 会话与执行测试（`session_test.go`）

| 类别 | 用例 |
| :--- | :--- |
| 超时与回收 | 超时触发 SIGTERM→SIGKILL 且**子进程组全部回收**（`sleep 100 & wait` 验证无孤儿） |
| 输出限流 | 超限截断并置 `truncated`（`yes` 洪泛） |
| 并发上限 | 超限拒绝并返回 `concurrency_limit` |
| 环境变量 | 验证服务进程 env 中的敏感变量（如 `FILELIST_TEST_SECRET`）**未**被子进程继承 |
| 回滚缓冲 | 环形覆盖正确；重连重放内容一致 |
| 空闲回收 | `idleTimeout` 后会话被销毁，PTY 进程组回收 |

### 9.3 WebSocket 安全测试（`ws_test.go`，**必测项**）

因评审第 6 项决定不加 `SameSite=Strict`，**本组测试是本方案安全性的唯一保障**：

| 用例 | 期望 |
| :--- | :--- |
| 无 token 升级 | 拒绝（401） |
| **非法 Origin 升级** | **拒绝（403）——模拟 CSWSH，最高优先级** |
| **Origin 缺失** | 按配置策略处理（默认拒绝） |
| 合法 Origin + 合法 token | 通过 |
| 合法 Origin 但仅靠 Cookie（无显式 token） | **拒绝**（不依赖 Cookie 隐式通过） |
| `openpty` 未提供 `terminalToken` | 拒绝 |
| `openpty` 提供错误 `terminalToken` | 拒绝（恒定时间比对） |
| `ops.enabled: true` 但 `server.token` 为空 | 面板不渲染，路由 404 |
| `ops.enabled: false` | 路由 404（未注册），非 403 |

### 9.4 模块化验证（C4 专项）

| 检查 | 命令 / 方法 | 期望 |
| :--- | :--- | :--- |
| 子包无反向依赖 | `go list -deps ./internal/ops` | 输出中**不含** `filelist` 主包，也不含 indexer 相关符号 |
| 子包可独立测试 | `go test ./internal/ops/` | 全部通过，且测试中不启动 HTTP 服务、不访问真实磁盘根 |
| 可卸载性 | 删除 `internal/ops/` + `static/ops/`，移除 5 处集成点 | `go build ./...` 成功，主站功能正常 |
| 前端无符号耦合 | 搜索 `ops.js` 中的 `filelistApp` | 无匹配（仅使用 `data-ops-base` 与事件） |
| 主站无 ops 逻辑 | 搜索 `app.js` 中的 `ops` | 仅 1 行事件广播 |

### 9.5 端到端与人工验收

1. 编辑 `/data/nginx/nginx.conf` → 切面板 → cwd 自动为 `/data/nginx` → 执行 `nginx -t` → `exit 0`；
2. `tail -f` 长任务：抽屉内运行 → 关闭抽屉再打开 → 输出连续（会话保持）；
3. 移动端视口（400×840）：抽屉全屏化，无横向溢出；
4. 反代场景（Nginx 子路径）：WS 升级成功、输出实时、无 60s 断连；
5. 暗色模式：面板与终端配色协调，无白底闪烁。

### 9.6 回归测试

改动涉及 `config.go`、`server.go`、`index.html`、`app.js`，必须完整运行既有测试：

```bash
cd src && go test ./... && go vet ./...
```

并确认既有 E2E（`temp/test_reader_e2e.js` 等）不受影响。

---

## 十、 风险与已知边界

| 风险 | 等级 | 缓解措施 |
| :--- | :--- | :--- |
| **CSWSH：恶意网页静默连接内网面板** | **高** | **Origin 白名单 + 显式 token（唯一防线，§5.2 L3）+ 专门测试用例** |
| **root PTY = 完整 root shell** | 高 | 双前置 + 独立 `terminalToken`；文档显著标注；默认关闭终端模式 |
| **环境变量泄漏凭据** | 高 | 严格白名单透传，绝不继承完整 env |
| **子进程泄漏 / 孤儿进程** | 中 | 进程组 kill（Unix `Setpgid`），超时 SIGTERM→SIGKILL 兜底 |
| 输出洪泛打爆内存 | 中 | `maxOutput` 截断 + 回滚缓冲上限 + 并发上限 |
| 白名单被绕过 | 中 | 拼接符扫描（先于白名单匹配）+ 规范化 + 充分绕过用例 |
| 反代配置不当导致实时性丧失 | 中 | 提供 Nginx/Caddy 片段；反代场景要求显式配 `originPatterns` |
| 内置只读白名单过宽 | 中 | 逐条显式枚举子命令，禁用分页器与交互式工具；`deny` 可收紧 |
| xterm 前端资产升级困难 | 低 | 固化打包脚本与升级步骤于 `vendor/xterm/README.md` |
| Windows 下 PTY 不可用 | 低 | 构建标签隔离 + 前端隐藏入口 + 启动日志提示 |

### 明确的能力边界（写入 README）

- **cwd 沙箱不是安全边界**：只约束工作目录，不阻止读取沙箱外文件。真正边界是 L4 白名单。
- **前端二次确认不是安全边界**：仅防手滑，安全由服务端强制。
- **不替代 SSH**：`free` 模式虽可执行任意命令，但缺少 SSH 的会话复用、端口转发、SFTP。它是"改完即生效"的快捷通道。
- **本项目定位仍为只读优先**：`ops` 是可选能力，默认关闭，与 `upload`、`manage` 保持一致的"默认安全"哲学。

---

## 十一、 与 v1 方案的差异说明

| 决策点 | v1 结论 | **v2 结论** | 修正依据 |
| :--- | :--- | :--- | :--- |
| 后端包结构 | `package main` 内平铺 `ops_*.go` | **独立子包 `internal/ops` + 接口注入** | C4：需要编译期边界 |
| 后端耦合面 | 未量化 | **5 文件、约 6 行**（含适配器 40 行） | C4：可核查 |
| 前端 DOM | 写在 `index.html` 内 | **空挂载点，DOM 由 `ops.js` 运行时自建** | C4：主站模板零侵入 |
| 前端 JS | 未定义与主站的关系 | **独立 Alpine 组件 + 独立注册，零符号依赖** | C4 |
| cwd 跟随机制 | 未定义 | **`window` 自定义事件总线 `filelist:navigate`** | C4：避免读主站内部状态 |
| 工具栏按钮 | 写在 `index.html` | **由 `ops.js` 动态注入** | C4：进一步降低侵入 |
| 白名单 | 仅使用者配置的几条 | **内置只读命令白名单 + 使用者追加** | 评审第 2 项 |
| `snippets` | 设计包含 | **移除** | 评审第 5 项 |
| Cookie 加固 | 建议加 `SameSite=Strict` | **不加；Origin 校验升级为唯一防线并强制测试** | 评审第 6 项 |
| 安全测试 | 常规覆盖 | **新增 CSWSH 专项必测组 + 模块化验证专项** | 评审第 6 项 + C4 |
| 交付节奏 | 两阶段 | 两阶段（不变，已确认） | 评审第 1 项 |

---

## 十二、 实施前确认

评审 6 项已全部确认，无待决事项。以下为实施时需遵循的关键约束（供实施者核对）：

1. **耦合面不得超过 §2.1 的清单**（5 文件、约 6 行）。若实施中发现需要更多侵入点，应先评审；
2. **`internal/ops` 不得导入 `indexer`/`Server`/`webFS`**，用 `go list -deps` 核查；
3. **Origin 校验为强制项**，且必须有 CSWSH 测试用例；
4. **环境变量白名单为强制项**，不得继承完整 env；
5. **拼接符扫描必须先于白名单匹配执行**；
6. **阶段一结束时执行可卸载性验证**（删除 ops 相关文件后主站仍能构建运行）。
