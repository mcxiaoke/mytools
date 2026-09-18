# FileList 服务器状态面板（Status Panel）设计与实施方案 v2

> 创建时间：2026-09-18 (GMT+8) ｜ 修订：v2（按评审意见重写）
> 状态：方案设计完成，待评审
> 目标版本：v0.3.0
> 相关文档：`DESIGN.md`、`PLAN-NOVEL-READER-20260916.md`
> 说明：本文与 `PLAN-OPS-PANEL-20260918.md`（运维命令面板 / 可执行命令）**范围不重叠**，互不依赖。

---

## 〇、v2 修订说明（相对 v1 的改动）

| # | 评审意见 | v1 | v2 结论 |
| :--- | :--- | :--- | :--- |
| 1 | 两平台都引入 gopsutil，不要求零依赖 | 纯 `/proc` 解析 + 构建标签隔离 | **改用 `github.com/shirou/gopsutil/v4`**，Linux/Windows 统一实现，删除全部平台桩代码 |
| 2 | 只读状态不需要额外验证 | 建议 `enabled` + 非空 `token` 双前置 | **取消第二因子与双前置**；仅继承既有全局 token（配了就受保护），不新增任何门禁 |
| 3 | systemd 卡片 | 列为建议项 | **采纳**，与 Docker 卡片同级，均为可选卡片 |
| 4 | 不做历史曲线 | 不做 | **不做**，维持「实时一瞥」定位 |
| 5 | **尽量低耦合，前后端都模块化** | 仅强调「新增文件、改动少」 | **架构级重构**：后端改为独立 Go 子包（编译器强制边界），前端改为自包含 ES 模块岛。见 §三 |

> **重要**：v1 的「零依赖 / 纯 `/proc`」结论已被本次评审推翻，v1 中的相关章节（`/proc` 解析、平台桩、`testdata/proc` 夹具）**全部作废**，以本文为准。
> v1 备份于 `temp/backups/PLAN-STATUS-PANEL-20260918.md.<时间戳>.bak`。

### 0.1 本次为修订方案所做的实测（结论均来自真实运行，非推断）

在 `temp/` 下建隔离探针验证，全部结论可复现（原始数据见 §十二）：

| 验证项 | 结果 |
| :--- | :--- |
| gopsutil 版本 | **v4.26.8**（2026-08-29 发布，BSD-3-Clause） |
| `CGO_ENABLED=0` 交叉编译 | windows/amd64 与 linux/amd64 **均编译通过**，单二进制定位不受影响 |
| 二进制体积增量 | Linux 精简后 **1.52 MB → 2.45 MB（+0.93 MB）** |
| Linux 采集性能（328 进程） | 全字段扫描 **36 ms**（0.11 ms/进程） |
| Windows 采集性能（295 进程） | 除 `NumThreads()` 外均正常；**`NumThreads()` 单轮耗时 4.67 秒**（严重缺陷，见 §五） |
| Windows 线程数修复 | 改用单次 Toolhelp32 快照：**17 ms / 295 进程**（提速约 275 倍） |
| 进程 CPU% 陷阱 | `p.Percent(0)` 在每轮重新枚举的对象上**恒返回 0**，必须自维护差分表（见 §五） |

---

## 一、需求与边界

### 1.1 需求

| 编号 | 能力 | 优先级 |
| :--- | :--- | :--- |
| R1 | CPU 使用率（总体 + 每核心 + 负载） | 必须 |
| R2 | 内存使用（含 swap） | 必须 |
| R3 | 进程列表 + 多字段排序 + 搜索/过滤 | 必须 |
| R4 | Docker 容器列表 | 加分（已确认） |
| R5 | systemd 失败单元列表 | 加分（已确认） |
| R6 | **前后端均低耦合、模块化** | 必须（硬性） |

### 1.2 明确不做（Non-Goals）

- **不做任何写操作**：不 kill 进程、不重启容器/服务、不改配置。纯观察窗。
- 不做历史曲线、时序库、阈值告警（用户已确认）。
- 不做多主机聚合、不做分布式。
- 不做交互式终端 / 命令执行（属 `PLAN-OPS-PANEL` 范围）。
- 不引入 npm / 打包工具链，前端保持「无构建」。
- 不新增任何鉴权门禁（用户已确认）。

### 1.3 「低耦合」的可验证定义

低耦合不能只是一句形容词。本方案把它定义为**四条可机械验证的约束**：

| 约束 | 含义 | 验证方式 |
| :--- | :--- | :--- |
| **C1 编译器强制边界** | 后端状态逻辑放在独立 Go 子包 `internal/status`，**永不 import `main` 包**，不接触 `*Server`、`*Indexer`、`*Config` 等主包类型 | `go list -deps ./internal/status` 的输出中不含主包 |
| **C2 单向依赖** | 依赖方向恒为 `main → internal/status`，状态模块通过**自己的** `Config` / `Options` 结构体接收输入 | 主包类型不出现在子包任何签名中 |
| **C3 前端零全局** | 状态页的 JS/CSS 与 `app.js` / `app.css` **不共享任何全局变量、store、类名**，通过独立 `<script type="module">` 入口加载 | 删除 `web/status/` 目录后主站页面零变化 |
| **C4 整体可删除** | 删除状态模块全部新增文件 + 回退 4 处共约 12 行改动，项目即可完全恢复原状 | 见 §3.5 验证清单 |

---

## 二、架构总览：两个自包含模块岛

```
┌─────────────────────────────────────────────────────────────────────┐
│  main 包（既有，几乎不动）                                            │
│                                                                      │
│   config.go ── +Status 配置块                                        │
│   server.go ── Routes() 中加 1 行；pageHTML() 注入 1 个布尔值          │
│   server_status.go ── 【唯一适配层】把主包配置翻译成 status.Config     │
│                                                                      │
│            │ 依赖方向：main ──→ internal/status（单向，编译器强制）    │
│            ▼                                                         │
├─────────────────────────────────────────────────────────────────────┤
│  internal/status  ── 自包含 Go 子包（不 import main，零主包类型）      │
│                                                                      │
│   status.go        Service 生命周期、Options、对外唯一入口 Mount()     │
│   config.go        本包自己的 Config（与主包完全解耦）                 │
│   collect.go       采集编排 + 差分状态表 + 缓存                       │
│   collect_linux.go / collect_windows.go   平台采集（gopsutil）         │
│   docker.go        Docker 采集（CLI）                                 │
│   systemd.go       systemd 单元采集（CLI）                             │
│   sortfilter.go    排序 / 过滤 / 裁剪（纯函数，好测）                  │
│   http.go          路由注册 + 参数校验 + JSON 输出                    │
│   web/             【本包自带的嵌入式前端资产】                        │
│     ├── index.html                                                  │
│     ├── app.css                                                     │
│     └── js/  *.js  （ES 模块岛）                                     │
│                                                                      │
│   全部路由挂在既有 mux 上，路径前缀 /status 与 /api/status/           │
└─────────────────────────────────────────────────────────────────────┘
                              │ HTTP
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  浏览器：/status 页面 = 自包含 ES 模块岛                              │
│                                                                      │
│   index.html ── 唯一 <script type="module"> 入口                     │
│   app.js   ── 引导：装配 store + 视图 + 路由，无全局泄漏              │
│   core/    ── api.js（HTTP 客户端） store.js（状态+轮询） format.js   │
│   views/   ── cpu.js memory.js disk.js net.js processes.js           │
│               docker.js systemd.js  （每卡片一个模块，注册式挂载）     │
└─────────────────────────────────────────────────────────────────────┘
```

**核心设计意图**：状态功能是一个**可整体摘除的器官**，而不是长在主功能里的藤蔓。它自带前端资产、自带路由、自带配置结构、自带生命周期，与主包之间只隔着一层约 40 行的适配代码。

---

## 三、低耦合架构详解（本次修订重点）

### 3.1 后端：独立 Go 子包 + 编译器强制边界

**为什么用 Go 子包而不是「主包内新增几个文件」**：Go 的包边界是**编译期强制**的，不是靠约定和自觉。子包物理上无法访问主包的未导出标识符，也无法 import 主包（否则循环依赖，编译失败）。这把「低耦合」从文档里的承诺变成了**编译器会报错的红线**——后来者想破坏耦合，代码根本编译不过。

**包位置**：`src/internal/status/`

用 `internal/` 而非普通子包，是因为 `internal` 有额外的语言级约束：只能被 `src/` 下的代码 import，外部模块无法引用。这保证了它不会被误当成公共 API。

**子包对外只暴露三个东西**：

```go
package status

// Config 是状态模块自己的配置，与主包 Config 无任何关系。
type Config struct {
    Enabled       bool
    Interval      time.Duration
    ProcessLimit  int
    ShowDocker    bool
    ShowSystemd   bool
    ShowNetwork   bool
    ShowDisk      bool
    DockerTimeout time.Duration
    IdleStop      time.Duration
}

// MountPoint 是磁盘卡片需要的挂载点信息（由主包从 roots 翻译而来）。
type MountPoint struct {
    URL  string
    Path string
}

// Options 是装配参数。主包只需构造这个结构体。
type Options struct {
    Config    Config
    BasePath  string       // 反代子目录前缀，用于生成正确的资源 URL
    Mounts    []MountPoint // 来自主包 roots 配置
    Logf      func(string, ...any) // 日志回调，避免依赖主包 logger 类型
}

// Mount 在给定 mux 上注册本模块的全部路由（页面 / API / 静态资产）。
// 返回的 Service 由调用方决定是否 Close；不 Close 也会在空闲后自停。
func Mount(mux *http.ServeMux, opts Options) (*Service, error)

// Service 持有采样器与会话状态。
func (s *Service) Close() error
```

> **日志注入方式的取舍**：ops 方案用 `Logger` **接口**（3 个方法）注入，本方案用 `Logf` **函数回调**（1 个字段）。两者都能达到「子包不依赖主包日志类型」；`Logf` 更简洁、测试时直接传 `t.Logf`，但丧失按级别过滤的表达力。若实现时发现需要区分级别，改回接口即可，属局部调整。

**关键解耦细节**：

| 细节 | 做法 | 解决的问题 |
| :--- | :--- | :--- |
| 配置 | 子包定义**自己的** `Config`，不用主包 `*Config` | 主包配置结构变更不会波及子包 |
| 日志 | 用 `Logf func(string, ...any)` 回调，不用主包 `*Logger` | 子包不依赖主包日志类型；测试时可注入 `t.Logf` |
| 路由 | `Mount(mux, opts)` 自行注册，不由主包逐个 `HandleFunc` | 主包不需要知道子包有几个端点、路径叫什么 |
| 静态资产 | 子包**自己** `//go:embed web/*` 并自行服务 | 主包 `/static/` handler 与 `webFS` 完全不受影响 |
| 路径前缀 | 通过 `BasePath` 参数注入，子包内部自行拼接 | 子包不读取主包配置 |
| 生命周期 | 懒启动 + 空闲自停，`Service` 自管理 goroutine | 主包 `main.go` **零改动**，不需要接线启动/停止 |

### 3.2 前端：自包含 ES 模块岛

**核心原则：不共享任何东西。**

| 维度 | 主站（既有） | 状态面板（新增） | 冲突可能 |
| :--- | :--- | :--- | :--- |
| JS 加载方式 | `<script defer src="alpine.min.js">` + `app.js` | `<script type="module" src=".../app.js">` | 无（模块作用域隔离） |
| 全局变量 | Alpine 全局 store（`Alpine.store`） | **完全不使用**，模块内闭包状态 | 无 |
| CSS | `app.css` + `pico.min.css` | 自带 `app.css`，**所有选择器加 `.st-` 前缀** | 无 |
| 主题变量 | 读取 `--pico-*` | **只读**同样的变量，不覆写 | 无（继承主题但互不修改） |
| 静态资源路径 | `/static/*` | `/status/assets/*` | 无（路径空间完全分离） |

**为什么用原生 ES 模块而不是继续用 Alpine**：Alpine 是「HTML 里写声明式属性」的范式，适合主站这种以模板为主的页面。但状态面板是**高频更新的数据视图**（2 秒一轮、多个卡片、表格排序），用声明式属性会导致模板臃肿且难以模块化拆分。原生 ES 模块的 `import`/`export` 天然就是模块边界，配合 `type="module"` 的自动 defer 与作用域隔离，正好满足 C3「零全局」。

> 注意：这是**新增页面的独立技术选择**，不改动主站任何现有代码，也不影响主站技术栈。

**前端模块划分与契约**：

```
web/
├── index.html            唯一入口，仅 <script type="module" src="app.js">
├── app.css               全部样式，选择器统一 .st- 前缀
└── js/
    ├── app.js            引导：创建 store → 注册视图 → 启动轮询
    ├── core/
    │   ├── api.js        export 纯 HTTP 客户端（只负责取数，不碰 DOM）
    │   ├── store.js      export 状态容器 + 轮询调度 + 订阅通知
    │   └── format.js     export 纯格式化函数（字节/百分比/时长）
    └── views/
        ├── cpu.js        每模块 export 一个 mount(el, store) 
        ├── memory.js
        ├── disk.js
        ├── net.js
        ├── processes.js  含排序/搜索交互
        ├── docker.js
        └── systemd.js
```

**视图模块统一契约**（这是前端模块化的关键，保证卡片可插拔）：

```js
// 每个 views/*.js 都导出同样的形状，app.js 只认这个契约
export const id = 'cpu';                    // 唯一标识
export const title = 'CPU';                 // 卡片标题
export function mount(el, store) { ... }    // el: 容器元素, store: 状态容器
export function update(el, data) { ... }    // 数据到达时被调用
export function unmount(el) { ... }         // 清理定时器/监听器
```

**收益**：新增一个卡片（比如以后的「GPU 状态」）只需在 `views/` 加一个文件并在 `app.js` 的注册数组里加一行，**不需要改动任何其它模块**。删除某卡片同理。卡片之间零耦合，且每个模块可独立单测（纯函数部分）。

### 3.3 对既有代码的全部改动（量化清单）

| # | 文件 | 改动 | 行数 |
| :--- | :--- | :--- | :--- |
| 1 | `config.go` | `Config` 增加 `Status` 块 + `StatusEnabled()` 方法 | ~10 |
| 2 | `server.go` `Routes()` | 追加：`if s.cfg.StatusEnabled() { mountStatus(mux, s.cfg) }` | 1 |
| 3 | `server.go` `pageHTML()` | 注入 `statusConfig`（仅 `{enabled, url}`）供工具栏按钮显隐 | ~2 |
| 4 | `web/index.html` | 工具栏新增「📊 状态」按钮，`x-show` 控制 | ~4 |
| 5 | `server_status.go` | **新增适配层**（唯一翻译点） | ~40（新文件） |
| 6 | `config.sample.yaml` | `status:` 配置说明 | ~20（纯注释） |

**对既有代码的实际修改：4 个文件、约 17 行**（其中 1 个是新增适配文件，2 处是纯注释）。
**除此之外零改动**：`indexer.go`、`utils.go`、`log.go`、`app.js`、`app.css`、`main.go` 全部不动。

> `main.go` 不动是刻意的：状态模块懒启动 + 空闲自停，因此不需要在启动/关闭流程里接线。

### 3.4 依赖关系图（可机械验证）

```
internal/status 的 import 清单（全部为外部库或标准库）：
  github.com/shirou/gopsutil/v4/{cpu,mem,disk,net,host,process}
  golang.org/x/sys/windows          （仅 Windows 构建，用于 Toolhelp32 快照）
  context, encoding/json, fmt, net/http, os/exec, sort, strings,
  sync, time, embed, io/fs, strconv, errors, runtime

  明确不包含：
  ✗ filelist 主包（任何类型）
  ✗ *Server / *Indexer / *Config / *Logger
  ✗ 主包的 webFS / writeJSON / constEqual / withinRoot
```

**验证命令**（写进验收清单）：

```bash
cd src
# 应无输出（证明子包不依赖主包）
go list -deps ./internal/status | grep -x "filelist"
```

### 3.5 解耦验证清单（验收时逐条执行）

| # | 验证 | 期望结果 |
| :--- | :--- | :--- |
| V1 | `go list -deps ./internal/status \| grep -x filelist` | 无输出 |
| V2 | 删除 `src/internal/status/` + `src/server_status.go` + 回退 4 处改动 | `go build ./...` 与 `go test ./...` 全绿 |
| V3 | 删除 `src/internal/status/web/` 整个目录 | 仅状态页 404，主站一切正常 |
| V4 | 状态页打开时，浏览器控制台执行 `Object.keys(window)` | 无 `status`/`st*` 相关全局变量 |
| V5 | 主站 `app.js` 中全局搜索 `status` | 仅命中 `statusConfig` 注入点（预期） |
| V6 | `status.enabled: false` 时访问 `/status` 与 `/api/status/overview` | 均 404，主页无入口按钮 |
| V7 | 删除状态模块后，既有 27 项 E2E 测试 | 全部通过（零回归） |
| V8 | **注入失败可测性**：用假 `Collector` + 假 `Logf` 运行子包单测 | 全部通过，且**不启动 HTTP 服务、不读真实 `/proc`、不依赖真实主机**（子包可脱离 filelist 独立测试） |

### 3.6 两个必须注意的实现细节

**（1）`go:embed` 不递归子目录 —— 前端模块拆分后的第一个坑**

前端拆成 `web/js/core/` 与 `web/js/views/` 后，如果按主包既有写法只嵌顶层会**静默漏掉子目录文件**：

```go
// ✗ 错误：只嵌 web 顶层，js/core、js/views 下的文件不会被包含
//go:embed web/*
var assets embed.FS

// ✓ 正确：用 all: 前缀递归包含
//go:embed all:web
var assets embed.FS
```

`//go:embed all:web` 会连同以 `.` 或 `_` 开头的文件一并递归嵌入。这是拆分前端模块后**必须同步调整**的一处写法，否则表现是「页面能打开但 JS 404」，容易误判为路径问题。

**（2）子包内的 embed 与主包 embed 相互独立**

状态页资源由**子包自己**的 `embed.FS` 持有，与主包 `server.go` 的 `webFS` 完全无关。因此：

- 主包 `/static/` 路由与 `webFS` 的行为不受任何影响；
- 状态页资源走独立的 `/status/assets/*` 路径空间，不经过主包 `handleStatic`；
- 删除子包的 `web/` 目录只会让状态页 404（V3），主站零感知。

---

## 四、数据采集（gopsutil v4）

### 4.1 依赖引入

```bash
cd src
go get github.com/shirou/gopsutil/v4@v4.26.8
go mod tidy
```

| 项 | 值 |
| :--- | :--- |
| 版本 | **v4.26.8**（2026-08-29） |
| 许可 | BSD-3-Clause |
| 上游 | `github.com/shirou/gopsutil`（活跃维护，v4 为当前主线） |
| 直接依赖 | `purego`、`go-sysconf`、`plan9stats`、`perfstat`、`wmi`、`yusufpapurcu/wmi`、`x/sys` |
| 传递依赖 | `go-ole`、`numcpus`、`go-spew`、`go-cmp`、`go-difflib`（测试用） |
| 二进制增量 | **+0.93 MB**（Linux，`-s -w` 精简后实测） |
| CGO | **不需要**，`CGO_ENABLED=0` 双平台交叉编译已验证通过 |

> **关于二进制体积**：从 1.52 MB 增至 2.45 MB，相对增幅 61%，但绝对增量不到 1 MB。对「单文件分发、无运行时依赖」的定位无实质影响，属于可接受成本（评审已确认不强制零依赖）。

### 4.2 采集 API 映射

| 数据 | gopsutil 调用 | 备注 |
| :--- | :--- | :--- |
| 主机信息 | `host.Info()` | 主机名、OS、内核、启动时间 |
| CPU 型号/核心 | `cpu.Info()` / `cpu.Counts(true)` / `cpu.Counts(false)` | 型号、物理核、逻辑核 |
| CPU 总体/每核 | **`cpu.Times(false/true)` + 自算差分** | 不用阻塞版（见 §5.1） |
| 负载 | `load.Avg()` | Linux 有值；Windows 返回 0 |
| 内存 | `mem.VirtualMemory()` / `mem.SwapMemory()` | `UsedPercent` 已是「总量−可用」口径 |
| 磁盘 | `disk.Usage(mount.Path)` | 逐 roots 挂载点 |
| 网络 | `net.IOCounters(false)` + 自算差分 | 过滤回环/虚拟网卡 |
| 进程列表 | `process.Processes()` | 已预填 name/ppid/numThreads（Windows 快照） |
| 进程详情 | `p.Times()` / `p.MemoryInfo()` / `p.Cmdline()` / `p.Status()` / `p.Username()` / `p.CreateTime()` | 见 §5 的成本分级 |

### 4.3 平台差异处理

两平台统一用 gopsutil，**不需要任何平台桩**。但有两处必须按平台处理，用构建标签隔离：

| 文件 | 构建标签 | 内容 |
| :--- | :--- | :--- |
| `collect_linux.go` | `//go:build linux` | 标准路径；`load.Avg()` 有效；线程数直接取 `p.NumThreads()` |
| `collect_windows.go` | `//go:build windows` | **线程数改走单次 Toolhelp32 快照**（见 §5.2）；`load.Avg()` 恒为 0，UI 隐藏负载卡片 |

### 4.4 采样器（Sampler）设计

```
Service（懒启动）
 ├── 首个请求到达 → 启动后台 goroutine
 ├── tick（默认 1s）：采集 → 更新差分表 → 写入缓存快照
 ├── 所有 HTTP 请求读缓存，不各自触发采集
 ├── 空闲 idleStop（默认 60s）无请求 → 自动停止 goroutine
 └── Close() 显式停止；随进程退出自然回收
```

**差分状态表**（进程 CPU% 的核心，见 §5.1）：

```go
type procSample struct {
    cpuTicks  float64   // user + system（自上次采样）
    startedAt int64     // CreateTime，用于识别 pid 复用
    at        time.Time
}
// key = pid；仅保留本轮仍存在的 pid（自动清理已退出进程）
prev map[int32]procSample
```

**为什么必须自己维护差分**：gopsutil 的 `Process.Percent(0)` 依赖对象内部的 `lastCPUTimes` 字段。而 `process.Processes()` **每次调用都返回全新的 `*Process` 对象**，缓存必然是空的。实测结论：

```
Sweep 1: Percent(0) nonzero=0/46 sum=0.000
Sweep 2 (new objects): Percent(0) nonzero=0/41 sum=0.000   ← 恒为 0
Own-delta approach:  computed for 41 procs, sum=0.00% (sane)
```

**若照抄 gopsutil 文档示例，进程 CPU% 会永远显示 0**。这是本方案必须自行实现差分表的直接原因。

**正确性验证**（实测）：制造一个满载进程后，自维护差分表正确算出 `bash cpu=99.3%`。

---

## 五、关键性能陷阱与对策（实测驱动）

### 5.1 阻塞式 API 一律禁用

gopsutil 的 `Percent(interval)` 系列**内部会 `time.Sleep(interval)`**，实测：

| 调用 | 实测耗时 | 结论 |
| :--- | :--- | :--- |
| `cpu.Percent(1s, true)` | **1.0014 s** | 阻塞 1 秒 |
| `p.Percent(200ms)` × 5 进程 | **1.0027 s** | 阻塞 1 秒（5 进程共享一次 sleep） |
| `cpu.Percent(0, false)` | ~0 s | 非阻塞，但依赖上次调用状态 |

**结论**：全部改用 `Times()` + 自维护差分，配合后台采样器。这样采样间隔由我们掌控，且与请求频率完全解耦——**HTTP 请求永远不会阻塞**。

### 5.2 Windows `NumThreads()` 严重性能缺陷（必须绕开）

**实测数据**：

| 平台 | `NumThreads()` 扫 295~297 进程 | 说明 |
| :--- | :--- | :--- |
| Linux | 0.46 ms（24 进程）/ 1.31 ms（67 进程） | 走 `/proc/[pid]/status`，正常 |
| **Windows** | **4.67 秒** | 每次调用都 `CreateToolhelp32Snapshot` 全系统枚举 |

**根因**（读源码确认）：`process_windows.go:595` 的 `NumThreadsWithContext` **无条件**调用 `getFromSnapProcess(pid)`，该函数创建全系统进程快照并线性扫描，且**不检查已缓存值**：

```go
func (p *Process) NumThreadsWithContext(_ context.Context) (int32, error) {
    ppid, ret, _, err := getFromSnapProcess(p.Pid)  // ← 每次全系统快照
    ...
}
```

讽刺的是 `process.Processes()` 已经通过一次快照把 `numThreads` 预填好了，但 `NumThreads()` 不读这个缓存。295 进程 × 16 ms ≈ 4.7 秒，完全吻合。

**关于「是不是权限问题」的判定**（回应 psutil#2366 的同类现象）：

psutil 生态确实存在一个**不同**的性能问题：非管理员身份下 `process_iter()` 约慢 10 倍（[giampaolo/psutil#2366](https://github.com/giampaolo/psutil/issues/2366)，因访问被拒时回退到慢速路径）。它与本节的缺陷**不是同一个问题，但可叠加**：

| | 权限回退（psutil#2366） | 缺少缓存检查（本节） |
| :--- | :--- | :--- |
| 触发条件 | 非管理员身份，访问被拒的进程回退慢速路径 | 任何身份下，**每个**进程都做一次全系统枚举 |
| 受影响面 | `process_iter()` 整体（约 10×） | 仅 `NumThreads()`（约 1000×） |
| 能否缓存规避 | ❌ | ✅ 一次快照即可 |

**决定性的分离判据**（同一用户、同一批已预填对象）：

```
A) Ppid()       已预填（有缓存检查）  : 0.09 ms/进程
C) Ppid()       新建（缓存未命中）    : 15.1 ms/进程
B) NumThreads() 已预填（无缓存检查）  : 16.6 ms/进程   ≈ C
```

`PpidWithContext`（`:306`）**先查缓存**，命中即返回零快照；`NumThreadsWithContext`（`:595`）**无缓存检查**。两者共用同一个预填字段与同一个快照函数——**权限不可能只让其中一个 API 走缓存**，因此 B ≈ C 只能由「缺少缓存检查」解释。

> 即便把权限因素完全排除，一次快照（17 ms）就能覆盖全部进程，说明缓存化本身就是充分的修复。

**对策**：Windows 上改用**单次** Toolhelp32 快照自行取线程数。实测：

```
single-pass Toolhelp snapshot: 295 procs in 17.07 ms
10x snapshots: 166 ms (16.6 ms each)   ← 一次快照覆盖全部进程
```

**从 4.67 秒降到 17 毫秒（约 275 倍提升）**。实现放在 `collect_windows.go`，用 `golang.org/x/sys/windows`（已是 gopsutil 的间接依赖，不新增依赖）：

```go
//go:build windows

// snapshotThreads 一次性取回全部进程的线程数，规避 NumThreads() 的全系统快照缺陷。
func snapshotThreads() map[uint32]uint32 {
    snap, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPPROCESS, 0)
    if err != nil { return nil }
    defer windows.CloseHandle(snap)
    m := make(map[uint32]uint32, 512)
    var pe32 windows.ProcessEntry32
    pe32.Size = uint32(unsafe.Sizeof(pe32))
    if err := windows.Process32First(snap, &pe32); err != nil { return nil }
    for {
        m[pe32.ProcessID] = pe32.Threads
        if err := windows.Process32Next(snap, &pe32); err != nil { break }
    }
    return m
}
```

### 5.3 字段成本分级（Linux 实测，328 进程）

| 字段 | 成本 | 分级 |
| :--- | :--- | :--- |
| `Times()` | 4.1 ms | A（排序必需） |
| `Name()` | 1.4 ms | A |
| `MemoryInfo()` | 1.4 ms | A |
| `Status()` | 3.3 ms | A |
| `Ppid()` | 4.3 ms | A |
| `CreateTime()` | ~0 ms | A（pid 复用识别必需） |
| `Cmdline()` | 1.0 ms | B（仅 TopN） |
| `Username()` | 1.9 ms | B（仅 TopN） |
| `NumThreads()` | 1.3 ms（Linux）/ **4.67 s（Windows，需绕开）** | B（仅 TopN） |

**两阶段采集策略**：

| 阶段 | 动作 | 覆盖范围 |
| :--- | :--- | :--- |
| A 轻扫 | `Times` / `Name` / `MemoryInfo` / `Status` / `Ppid` / `CreateTime` | **全量**进程（排序、过滤、计数都需要） |
| B 增强 | `Cmdline` / `Username` / `NumThreads` | **仅 TopN**（默认 100） |

实测：Linux 328 进程，A 阶段 + B 阶段（全量）共 36 ms；按 TopN 裁剪后 B 阶段成本进一步下降。
**排序必须先于增强**：只有先按全量数据排序，才能确定哪些进程值得付出 B 阶段的成本。这也是 §6.3「搜索/过滤作用于全量」的实现基础。

### 5.4 异常与边界处理

| 场景 | 处理 |
| :--- | :--- |
| 单进程读取失败（权限/已退出） | 跳过该进程，不中断整轮采集；累计 `skipped` 计数 |
| 权限不足导致部分进程不可见 | 返回 `partial: true` + 缺失计数，前端提示「部分进程不可见」（不静默丢数据） |
| 进程数极多（> 3000，编译机） | A 阶段设硬上限 `maxProcs`（默认 3000），超出只统计数量并提示 |
| pid 复用 | 差分表以 `(pid, CreateTime)` 为身份；`CreateTime` 变化即视为新进程，重新开始累计 |
| 时钟回跳 / 计数回绕 | 差分前校验 `Δcpu >= 0` 且 `Δt > 0`，否则丢弃该样本返回 `null` |
| Windows 线程数快照失败 | 降级为 `null`，前端该列显示 `--`，不影响其它列 |

---

## 六、API 设计

所有接口 `GET`，响应 `application/json; charset=utf-8`，一律 `Cache-Control: no-store`。
路由由子包自行注册，全部落在 `/status` 与 `/api/status/` 前缀下。

### 6.1 `GET /api/status/overview`

```json
{
  "host": { "hostname": "nas", "os": "linux", "platform": "ubuntu",
            "kernel": "6.8.0-45-generic", "arch": "amd64",
            "uptimeSec": 812345, "bootTime": "2026-09-09T10:12:03+08:00" },
  "cpu": {
    "model": "Intel(R) N100", "physicalCores": 4, "logicalCores": 4,
    "usage": 12.4, "perCore": [ { "id": 0, "usage": 9.1 } ],
    "load": [0.42, 0.51, 0.63]
  },
  "mem": {
    "total": 16777216000, "used": 6214963200, "available": 10562252800,
    "usage": 37.0,
    "swapTotal": 2147483648, "swapUsed": 0, "swapUsage": 0.0
  },
  "disks": [ { "url": "/files", "path": "/mnt/data",
               "total": 1900000000000, "used": 840000000000,
               "free": 1100000000000, "usage": 43.1 } ],
  "net": [ { "iface": "eth0", "rxBps": 10240, "txBps": 20480 } ],
  "collectMs": 3.1,
  "partial": false
}
```

- `usage` 为百分比（0~100，一位小数）；`load` 仅 Linux 有值，Windows 省略该字段；
- 字节数返回**原始整数**，单位换算交给前端（避免精度损失与歧义）；
- 首次采样尚未产出差分时，`usage` 返回 `null`，前端显示 `--`。

### 6.2 `GET /api/status/procs`

| 参数 | 默认 | 说明 |
| :--- | :--- | :--- |
| `sort` | `cpu` | `cpu` \| `mem` \| `rss` \| `pid` \| `name` \| `state` |
| `order` | `desc` | `asc` \| `desc` |
| `limit` | `100` | 1~500，服务端裁剪 |
| `q` | `""` | 名称/命令行关键字过滤，**作用于全量进程后再排序** |
| `state` | `""` | 按状态过滤：`R`/`S`/`D`/`Z` |

```json
{
  "total": 187, "returned": 100, "partial": false, "skipped": 3,
  "items": [
    { "pid": 1234, "ppid": 1, "user": "root", "name": "nginx",
      "state": "S", "threads": 4, "cpu": 3.2, "mem": 1.4,
      "rss": 234881024, "started": "2026-09-15T08:30:11+08:00",
      "cmdline": "nginx: worker process" }
  ]
}
```

### 6.3 `GET /api/status/docker`

```json
{
  "available": true,
  "containers": [
    { "id": "a1b2c3d4e5f6", "name": "nextcloud", "image": "nextcloud:28",
      "state": "running", "status": "Up 3 days", "ports": "0.0.0.0:8081->80/tcp" }
  ]
}
```

`docker stats` 属慢操作（约 1s），**不纳入轮询**，单独提供 `GET /api/status/docker/stats`，由前端「📈 采样」按钮按需触发。

### 6.4 `GET /api/status/systemd`

```json
{
  "available": true,
  "failedCount": 1,
  "failed": [ { "unit": "nginx.service", "load": "loaded", "active": "failed",
                "sub": "failed", "description": "A high performance web server" } ]
}
```

- 实现：`systemctl --failed --no-legend --plain --no-pager`（只读，参数硬编码）；
- `systemctl` 不存在（非 systemd 系统 / Windows / 容器内）时返回 `{"available": false, "reason": "..."}`，前端隐藏卡片；
- 超时 3s，失败不影响其它卡片。

### 6.5 错误响应

| 场景 | HTTP | body |
| :--- | :--- | :--- |
| 参数非法 | 400 | `{"error":"invalid sort field"}` |
| docker / systemd 不可用 | 200 | `{"available":false,"reason":"..."}`（能力缺失，非错误） |
| 功能未开启 | 404 | 路由未注册，标准 404 |

> **鉴权**：本模块不新增任何鉴权代码。路由挂在既有 mux 上，自动受既有 `authMiddleware` 保护——配了 `server.token` 就受保护，没配就是内网开放。符合评审意见 2。

---

## 七、前端设计

### 7.1 布局

```
┌──────────────────────────────────────────────────────────────┐
│ 📊 服务器状态   nas · Linux 6.8.0 · 运行 9天9小时    [⏸ 暂停]  │
├───────────────────────────────┬──────────────────────────────┤
│ CPU  12.4%  负载 0.42/0.51/0.63 │ 内存  37.0%                │
│ ██████░░░░░░░░░░░░░░░░░░░░░░  │ ██████████░░░░░░░░░░░░░░░   │
│ 核1 9.1%  核2 22.3%  核3 8.0%  │ 6.2 GB / 15.6 GB            │
│ 核4 10.2%                      │ Swap 0% · 缓存 4.6 GB        │
├───────────────────────────────┴──────────────────────────────┤
│ 磁盘  /files  43.1% ████████░░░░░  872 GB / 1.7 TB            │
│ 网络  eth0    ↓ 10.2 KB/s   ↑ 20.5 KB/s                       │
├──────────────────────────────────────────────────────────────┤
│ 进程 (187)   [搜索]  [状态 ▾]  [刷新 2s ▾]                     │
│ PID ▲  USER   NAME        CPU%   MEM%   RSS    STATE  THREADS │
│ 1234   root   nginx        3.2    1.4   224M   S      4       │
├──────────────────────────────────────────────────────────────┤
│ 🐳 Docker (3 运行中)                        [📈 采样资源]      │
│ ⚙️ systemd  1 个单元失败  → nginx.service                      │
└──────────────────────────────────────────────────────────────┘
```

### 7.2 交互

| 交互 | 行为 |
| :--- | :--- |
| 列头点击 | 切换排序字段；同字段再点切换升/降序；箭头指示 |
| 搜索框 | 300ms 防抖，服务端过滤（作用于全量后排序） |
| 刷新频率 | 1s / 2s / 3s / 5s / 暂停，`localStorage` 记忆 |
| 页面隐藏 | `visibilitychange` 自动暂停轮询，回前台立即刷新一次 |
| 请求堆积防护 | 上一请求未返回则跳过本次 tick（in-flight 标志）；隐藏时 `AbortController` 取消在途请求 |
| 错误态 | 顶部可关闭提示条，**保留上一次快照**不清空 |
| 窄屏 | 卡片单列；进程表隐藏 THREADS/RSS 等次要列 |
| 数字稳定 | `font-variant-numeric: tabular-nums` + CSS transition，避免跳动 |

### 7.3 主题与样式隔离

- 所有选择器加 `.st-` 前缀，**不复用** `app.css` 的任何类名；
- 只**读取** `--pico-*` 变量以继承亮/暗主题，不覆写任何全局变量；
- 字号调节、暗色模式自动跟随系统与主站设置，但样式规则完全独立。

---

## 八、配置设计

```yaml
# 服务器状态面板（只读）。默认关闭。
# 说明：本模块不新增鉴权门禁，是否可访问完全由 server.token 决定。
status:
  enabled: false           # 总开关。false 时路由不注册，零暴露面
  interval: 1s             # 服务端采样间隔（1s/2s/5s）
  processLimit: 100        # 进程列表默认返回条数（上限 500）
  maxProcs: 3000           # 单轮采集的进程数硬上限（超出只计数并提示）
  showDocker: true         # Docker 卡片（需服务器有 docker CLI）
  showSystemd: true        # systemd 失败单元卡片（需 systemctl）
  showNetwork: true        # 网络吞吐（IOCounters 差分）
  showDisk: true           # 各挂载点磁盘占用（复用 roots）
  hideCmdline: false       # true: 进程列表不返回命令行（收敛信息暴露面）
  # dockerTimeout: 3s      # docker / systemctl 命令超时
  # idleStop: 60s          # 无请求自动停止采样（省 CPU）
```

### 8.1 安全模型

| 层 | 措施 | 说明 |
| :--- | :--- | :--- |
| L1 | **复用既有 `server.token`** | 挂在同一 mux，自动受 `authMiddleware` 保护；**不新增任何门禁**（评审意见 2） |
| L2 | **只读，无写接口** | 无 POST/PUT/DELETE；docker / systemctl 参数全部硬编码，无命令注入面 |
| L3 | **默认关闭** | 与 `manage.enabled` / `upload.enabled` 的 fail-safe 风格一致 |
| L4 | **响应头** | `Cache-Control: no-store`；`X-Content-Type-Options: nosniff` 由既有中间件覆盖 |
| L5 | **不泄露凭据** | 不读取/不返回环境变量；`Cmdline()` 返回的原始命令行会原样展示（见下） |

> **信息暴露提示（非门禁，仅告知）**：进程命令行、容器名、磁盘路径属于运维敏感信息，可能间接暴露内部服务拓扑。建议在需要对外暴露的场景下配置 `server.token`。本方案不做强制，符合评审意见。

### 8.2 资源开销预算（基于实测）

| 项 | 预算 | 依据 |
| :--- | :--- | :--- |
| 单轮全量采集（Linux，300 进程） | ~36 ms | 实测 328 进程 36.4 ms |
| 稳态 CPU 占用 | < 0.5% 单核 | 1s 采样 × 36 ms ≈ 3.6% 的单次开销占比，摊薄后极低 |
| 内存增量 | < 3 MB | 差分表 + 快照缓存 |
| 二进制体积 | **+0.93 MB** | 实测（1.52 → 2.45 MB） |
| 空闲开销 | **0** | 无请求 60s 后采样器自停 |

---

## 九、实施计划

### 阶段 1：子包骨架 + CPU/内存（约 1 天）

- [ ] 建 `src/internal/status/` 包骨架：`status.go`（Service/Options/Mount）、`config.go`
- [ ] `go get gopsutil/v4@v4.26.8`，确认双平台交叉编译通过
- [ ] `collect.go`：采样器（tick / 缓存 / 懒启动 / 空闲自停）、差分状态表
- [ ] `collect_linux.go` / `collect_windows.go`：CPU/内存/负载/主机信息
- [ ] `http.go`：`/api/status/overview`
- [ ] `server_status.go`（主包适配层）+ `config.go` 配置块
- [ ] 单测：差分计算（含时钟回跳、pid 复用、Δ=0 边界）

### 阶段 2：进程列表（约 1 天）

- [ ] 两阶段采集（A 全量轻扫 → 排序 → B 仅 TopN 增强）
- [ ] **Windows 线程数单次快照实现**（§5.2）
- [ ] 用户名缓存映射（避免重复 NSS 查询）
- [ ] `sortfilter.go`：排序、过滤、裁剪（纯函数）
- [ ] `/api/status/procs`
- [ ] 单测：排序稳定性、过滤作用于全量、limit 边界、pid 复用识别

### 阶段 3：前端模块岛（约 1.5 天）

- [ ] `web/index.html` + `web/app.css`（`.st-` 前缀隔离）
- [ ] `core/api.js`、`core/store.js`、`core/format.js`
- [ ] `views/cpu.js`、`memory.js`、`disk.js`、`net.js`、`processes.js`
- [ ] `app.js` 注册式装配
- [ ] 子包自行 `//go:embed web/*` 并服务 `/status/assets/*`
- [ ] 主页工具栏入口按钮
- [ ] E2E：`tests/e2e/specs/status.spec.js`

### 阶段 4：Docker + systemd + 收尾（约 1 天）

- [ ] `docker.go`：可用性探测（缓存 60s）、`docker ps` 解析、按需 stats
- [ ] `systemd.go`：`systemctl --failed` 解析
- [ ] `views/docker.js`、`views/systemd.js`
- [ ] **解耦验证清单 V1–V7 逐条执行**（§3.5）
- [ ] `config.sample.yaml` / `README.md` / `docs/DESIGN.md` 模块表更新
- [ ] `docs/CHANGES-20260918.md` 追加变更摘要

**总计约 4.5 天**（比 v1 多 1 天，因新增子包结构、前端模块拆分与 systemd 卡片）。

---

## 十、风险与对策

| # | 风险 | 影响 | 对策 |
| :--- | :--- | :--- | :--- |
| R1 | **照抄 gopsutil 文档用 `Percent(0)`** | 进程 CPU% 永远为 0 | 自维护差分表（§4.4）；实测已确认此陷阱 |
| R2 | **Windows `NumThreads()` 慢 275 倍** | 单轮采集 4.7 秒，面板不可用 | 改单次 Toolhelp32 快照（§5.2） |
| R3 | 阻塞式 `Percent(interval)` | HTTP 请求阻塞 1 秒 | 全部改用非阻塞 `Times()` + 后台采样（§5.1） |
| R4 | gopsutil 上游 API 变更 | 升级时编译失败 | 版本锁定 `v4.26.8`；升级需跑完整单测 |
| R5 | 二进制体积增长 | 从 1.52 MB 到 2.45 MB | 已实测确认可接受；如未来需要可改用 build tag 按平台裁剪 |
| R6 | 权限不足读不到部分进程 | 列表不全 | `partial: true` + 缺失计数，前端明确提示 |
| R7 | 进程数极多（编译机） | 采样变慢 | A 阶段硬上限 `maxProcs`（默认 3000） |
| R8 | 前端轮询堆积 | 请求雪崩 | in-flight 标志 + `AbortController` |
| R9 | 反代缓存 JSON | 数据不更新 | 全部接口 `Cache-Control: no-store` |
| R10 | 与 `PLAN-OPS-PANEL` 混淆 | 实现串味 | 两份文档独立；状态面板**只读**，不引入 WebSocket / PTY / 命令执行 |
| R11 | 子包意外依赖主包 | 耦合失效 | `go list -deps` 纳入验收（V1） |

---

## 十一、验收标准

| # | 标准 | 验证方式 |
| :--- | :--- | :--- |
| A1 | `status.enabled: false` 时 `/status` 与 `/api/status/*` 均 404，主页无入口 | 手工 + E2E |
| A2 | CPU/内存/负载与 `top`、`free -h` 目测一致（误差 < 2%） | 手工对照 |
| A3 | 进程 CPU% **非零且合理**（有负载进程时排序正确） | 手工 + 单测 |
| A4 | 进程列表支持按 CPU/MEM/PID/NAME/STATE 排序，升降序正确 | 单测 + E2E |
| A5 | 搜索与状态过滤作用于**全量**进程后再取 TopN | 单测（构造夹具） |
| A6 | **Windows 下单轮采集 < 200 ms**（验证线程数缺陷已绕开） | 手工计时 |
| A7 | Docker 不可用时卡片隐藏/提示，页面无报错 | 手工（无 docker 环境） |
| A8 | systemd 不可用时同上 | 手工（Windows） |
| A9 | 无请求 60s 后采样器停止，再次请求自动恢复 | 手工 + 日志 |
| A10 | 10 个标签页同时打开，服务端采样频率仍为 1 次/秒 | 日志计数 |
| A11 | Windows 与 Linux 下 `go build ./...`、`go test ./...` 全绿 | 本地 + 交叉编译 |
| A12 | **解耦验证清单 V1–V7 全部通过** | 见 §3.5 |
| A13 | 既有 27 项 E2E 测试全部通过（零回归） | `.\tests\run-e2e.ps1` |

---

## 十二、附录：实测原始数据

### 12.1 环境

| 项 | 值 |
| :--- | :--- |
| 开发机 | Windows 10 (10.0.19045) |
| Go | go1.26.5 windows/amd64（`C:\Home\Develop\go\bin`，不在默认 PATH） |
| Linux 测试环境 | WSL2 Ubuntu，内核 6.18.33.2-microsoft-standard-WSL2，12 核 |
| gopsutil | v4.26.8 |
| 探针位置 | `temp/gopsutil-probe2/`（验证后已清理） |

### 12.2 交叉编译与体积

```
CGO_ENABLED=0 GOOS=windows GOARCH=amd64 go build  → OK
CGO_ENABLED=0 GOOS=linux   GOARCH=amd64 go build  → OK

baseline(-s -w):  1.52 MB
with gopsutil  :  2.45 MB
delta          : +0.93 MB
```

### 12.3 Linux 采集性能（WSL2）

```
=== 24 进程（空闲系统）===
Times()        497.75 µs
Name()         323.73 µs
MemoryInfo()   193.62 µs
NumThreads()   464.55 µs
Status()       406.84 µs
Cmdline()      171.12 µs
Username()     1.08 ms
CreateTime()   1.8 µs

=== 328 进程（压力测试，spawn 300 个 sleep）===
full sweep (all procs): 36.43 ms  -> 0.11 ms/proc
differential Times() pass: 7.12 ms (n=328)

=== 67 进程（字段分级）===
Stage A (Times/Name/MemoryInfo/Status/Ppid/CreateTime): 14.53 ms
Stage B (Cmdline/Username/NumThreads):                   4.20 ms
A+B:                                                     18.73 ms
```

### 12.4 Windows 采集性能（关键缺陷）

```
total procs: 295
Times()                  0s
Name()                   0s
MemoryInfo()             1.21 ms
Status()                 0s
Ppid()                  30.55 ms
Cmdline()                2.13 ms
Username()              12.24 ms
NumThreads() [slow]   4667.17 ms   ← 严重缺陷
```

**根因源码位置**：`process/process_windows.go:595` `NumThreadsWithContext` 无条件调用 `getFromSnapProcess`（`:887`），后者 `CreateToolhelp32Snapshot` + 线性扫描，不复用 `ProcessesWithContext`（`:937`）已预填的 `p.numThreads`。

**修复验证**：

```
single-pass Toolhelp snapshot: 295 procs in 17.07 ms
10x snapshots: 166.40 ms (16.64 ms each)
```

### 12.5 进程 CPU% 差分陷阱

```
Sweep 1: Percent(0) nonzero=0/46 sum=0.000
Sweep 2 (new objects): Percent(0) nonzero=0/41 sum=0.000   ← 恒为 0
Own-delta approach:  computed for 41 procs, sum=0.00% (sane)
```

**自维护差分表在负载下的正确性**：

```
after load (own-delta):
  top1: pid=420 name=bash cpu=99.3%     ← 正确识别满载进程
  top2: pid=2   name=init-systemd 0.0%
  top3: pid=9   name=init         0.0%
```

### 12.6 阻塞式 API 实测

```
C) Percent(200ms) on 5 procs: 1.0027 s   ← 阻塞
D) cpu.Percent(1s,true):      1.0014 s   ← 阻塞
E) cpu.Percent(0,false):      ~0 s       ← 非阻塞
```

---

## 十三、已定稿的默认决策（可直接实施）

以下 5 项已按推荐值定稿，实施时无需再确认。若需调整，改动均为局部（不影响架构）。

| # | 决策点 | **定稿值** | 理由 / 回退成本 |
| :--- | :--- | :--- | :--- |
| 1 | 子包路径 | **`src/internal/status/`** | `internal` 有语言级约束（只能被 `src/` 下代码 import），避免被误当成公共 API。改路径只需批量替换 import，成本极低 |
| 2 | 前端范式 | **原生 ES 模块**（仅状态页） | 模块边界由语言机制保证（`import`/`export` + 模块作用域），正好满足「前端模块化」要求。**主站技术栈完全不动**。若日后要求全站统一 Alpine，只需重写 `views/*.js`，后端与 API 不受影响 |
| 3 | 采样间隔 `interval` | **1s** | 实测单轮采集 36 ms（328 进程），1s 间隔下开销占比 < 4%，完全可接受。改为 2s 仅需改配置默认值 |
| 4 | 进程 `cmdline` | **默认展示** | 运维价值高（定位进程用途）；已提供 `status.hideCmdline: true` 收敛开关（见 §8） |
| 5 | systemd 卡片范围 | **仅失败单元** | 保持轻量：`systemctl --failed` 一次调用即可；统计全部运行中单元在 systemd 下成本明显更高，且与「实时一瞥」定位不符 |

> **说明**：第 4 项需在配置中补上 `hideCmdline` 开关，已同步更新 §8 配置块。

---

## 十四、后续可选增强（不在本次范围）

- 进程详情浮层（打开的文件数、监听端口）；
- 按 PID 定位到 filelist 可浏览路径（cwd 落在 roots 内时提供跳转）；
- 前端 ring buffer 迷你 sparkline（评审已确认本次不做）；
- 阈值高亮（仅前端视觉，无通知渠道）；
- gopsutil 按平台裁剪以回收体积（如未来需要）。

> **与 ops 面板的联动**：若将来要做「从进程 cwd 跳转到文件浏览器」，应**复用 ops 方案已固化的 `filelist:navigate` 事件契约**（事件名 + `detail.path`，仅两个符号），而不是另造一套通信机制。该契约需登记进 `docs/DESIGN.md`。

