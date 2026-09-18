# 方案评审：OPS-PANEL（运维命令面板）低耦合设计

> 评审时间：2026-09-18 (GMT+8)
> 被评审对象：`docs/PLAN-OPS-PANEL-20260918.md`（912 行）
> 评审目的：评估其「前后端子模块化 + 低耦合」设计，并与 `PLAN-STATUS-PANEL-20260918.md` 对照
> 评审方式：逐节阅读原文 + 与本仓库实际代码交叉核对 + 关键外部技术论点联网查证

---

## 一、总体结论

> **结论：该方案的模块化设计质量很高，是全项目里「低耦合」做得最扎实的一份。其核心方法论（编译器强制边界 + 依赖注入 + 运行时 DOM 自建 + 事件总线）应当作为后续所有可选模块的范本。**
>
> **但它有 3 处代码级事实错误、1 处外部技术论点失实，以及若干遗漏项，实施前必须修正。**

| 维度 | 评分 | 说明 |
| :--- | :--- | :--- |
| 低耦合设计 | **A（优秀）** | 编译期边界 + 三层前端隔离，方法论完整且可机械验证 |
| 可验证性 | **A** | 提供了 `go list -deps`、可卸载性、符号搜索等具体核查手段 |
| 安全意识 | **A** | CSWSH / env 泄漏 / 进程组孤儿 / 输出洪泛，覆盖到位且分级清晰 |
| 代码事实准确性 | **C（需修正）** | 3 处引用了仓库中不存在的标识符/签名 |
| 外部技术论点准确性 | **C（需修正）** | gorilla/websocket「已归档」的说法与当前事实不符 |
| 完整性 | **B** | 遗漏了 `Getwd` 依赖与 `/ops` 路由命名冲突问题 |

---

## 二、值得肯定并应被沿用的设计

### 2.1 编译器强制边界（§2.2）

原文指出：`package main` 内部没有任何访问控制，`ops.go` 可随手引用 `indexer` 私有字段，**「没有编译期边界，耦合只是暂时看起来干净，半年后必然腐化」**。

这个判断完全正确，且是本方案最有价值的部分。用 `internal/` 子包把「约定」升级为「编译器红线」，是防腐化的唯一可靠手段。

**我的状态面板方案已采用同一思路**（`internal/status`），两处设计在此完全一致。

### 2.2 接口定义在消费方（§2.3）

`Logger` / `PathResolver` 两个接口声明在 `internal/ops` 包内、由主包实现注入，符合 Go 惯例（接口属于使用方）。收益说得准确：**子包单测可用 `fakeResolver` / `noopLogger` 完全脱离 filelist 运行**。

这是模块化最实际的回报——测试快、无副作用、无需真实环境。**我的方案已据此补上对应的验证项（V8）**。

### 2.3 前端三层隔离（§2.5）

| 层 | 做法 | 评价 |
| :--- | :--- | :--- |
| DOM | `index.html` 只留一个空 `<div>`，**DOM 由 `ops.js` 运行时自建** | 优秀。主站改布局不会波及 ops，反之亦然 |
| JS | 独立 Alpine 组件 + 自行注册，`index.html` 不出现 `x-data` | 优秀。与 `app.js` 零符号依赖 |
| 通信 | `window` 自定义事件 `filelist:navigate`，契约仅 2 个符号 | 优秀。单向依赖，未启用时零开销 |

**「唯一契约是两个符号（事件名 + `detail.path`）」这个表述很好**——把耦合面压缩到可审查的粒度，并建议固化进 `DESIGN.md`。

> **对我的方案的启发**：我的状态面板是独立页面，不需要 cwd 跟随，因此没有引入事件总线。但**如果将来要在状态页里加「从进程 cwd 跳到文件浏览器」这类联动，就应当复用这个 `filelist:navigate` 事件契约，而不是另造一套**。这一点我记在方案 §14 的后续增强里。

### 2.4 路由条件注册（§2.4）

未启用时**根本不注册路由**，返回 404 而非 403——「不暴露这里有个被关闭的功能」。这个细节体现了对信息泄露的敏感度，与我的 `status.enabled: false` 行为一致。

### 2.5 安全分级与「能力边界」自述

- L1 双前置 / L2 第二因子 / L3 Origin / L4 白名单 / L5 沙箱与资源 / L6 审计，六层结构清晰；
- **主动声明「cwd 沙箱不是安全边界」「前端二次确认不是安全边界」**——这种自我限定比夸大防护更可信；
- 「环境变量白名单透传」抓住了真实风险：服务进程 env 含 `server.token`、`ops.terminalToken`，直接继承的话跑一个 `env` 就能读走全部凭据。

---

## 三、必须修正的问题

### 3.1 【代码事实错误】`MapVirtualToReal` 的返回值签名写错

**原文 §2.3**：

```go
func (p opsPaths) Resolve(v string) (string, error) { return p.idx.MapVirtualToReal(v) }
```

**仓库实际**（`src/indexer.go:116`）：

```go
func (idx *Indexer) MapVirtualToReal(vpath string) (string, bool)   // 第二返回值是 bool，不是 error
```

**后果**：适配器**编译不过**。必须包一层转换，把 `bool` 翻译成 `error`：

```go
func (p opsPaths) Resolve(v string) (string, error) {
    real, ok := p.idx.MapVirtualToReal(v)
    if !ok {
        return "", fmt.Errorf("path not mapped: %s", v)
    }
    return real, nil
}
```

这是本次评审发现的**最实质的问题**——它是承重的集成代码，写错会导致阶段一无法编译。

### 3.2 【代码事实错误】适配器引用了不存在的日志函数

**原文 §2.3**：

```go
func (opsLogger) Info(msg string, kv ...any)  { logInfo(msg, kv...) }
func (opsLogger) Warn(msg string, kv ...any)  { logWarn(msg, kv...) }
func (opsLogger) Error(msg string, kv ...any) { logError(msg, kv...) }
```

**仓库实际**（`src/log.go`）：不存在 `logInfo` / `logWarn` / `logError` 这三个包级函数。日志是 `*Logger` 的**方法**：

```go
var logger = NewLogger(LevelInfo, os.Stdout)   // 包级变量，非函数
func (l *Logger) Info(format string, args ...any)
```

**后果**：同样编译不过。正确写法是：

```go
func (opsLogger) Info(msg string, kv ...any)  { logger.Info(msg, kv...) }
func (opsLogger) Warn(msg string, kv ...any)  { logger.Warn(msg, kv...) }
func (opsLogger) Error(msg string, kv ...any) { logger.Error(msg, kv...) }
```

**顺带指出一个语义问题**：既有 `Logger.Info(format string, args ...any)` 是 **printf 风格**（`log.Printf("[INFO] "+format, args...)`），而 ops 的 `Logger` 接口签名是 **key-value 风格**（`msg string, kv ...any`）。两者语义不同，直接透传会导致格式串被当作 kv 参数、占位符不被替换。**适配器必须做转换**，例如：

```go
func (opsLogger) Info(msg string, kv ...any) { logger.Info("%s %v", msg, kv) }
```

这个隐患文档里没有提到，属于设计遗漏。

### 3.3 【代码事实错误】`s.opsHandler` 字段不存在

**原文 §2.4**：

```go
if s.cfg.OpsEnabled() {
    mux.Handle("/api/ops/", s.opsHandler)  // ops.New(...).Handler()
}
```

**仓库实际**：`Server` 结构体只有两个字段（`src/server.go:48-51`）：

```go
type Server struct {
    cfg     *Config
    indexer *Indexer
}
```

没有 `opsHandler` 字段。**需要在 `NewServer` 中新增该字段并在启动时装配**，或者改为主包 `main.go` 持有 handler 并以闭包传入。文档标注「1 行」是不准确的——**实际需要改动 `Server` 结构体与 `NewServer`，属于结构性改动**，应如实计入耦合面。

### 3.4 【外部论点失实】gorilla/websocket「已归档」的说法与当前事实不符

**原文 §3.5**：

> `gorilla/websocket` —— 原仓库 2022 年末已归档，安全补丁依赖社区分支，新项目不应构建在已归档依赖上。

**查证结果**（2026-09-18 实测 `github.com/gorilla/websocket`）：

| 观测项 | 实际值 |
| :--- | :--- |
| 仓库归档状态 | **未归档**（页面正常显示 Issues 47、PR 35、Discussions、Actions） |
| Stars / Forks | 24.9k / 3.6k |
| 最新提交 | 370 commits，持续有活动 |
| README「Status」段 | **"The package API is stable."**（无归档/弃用声明） |
| 最新版本 | v1.5.3（2024-06-14）；v1.5.2（2024-05-01） |

**结论**：该仓库在 2022 年确实一度宣布停止维护，但**其后已恢复活动并发布了 v1.5.2 / v1.5.3**。「2022 年末已归档」是**过时信息**。

**不过——选型结论仍然成立**，只是理由需要更正：

| 论点 | 是否成立 |
| :--- | :--- |
| 「已归档，不应选用」 | ❌ 事实不成立 |
| 「并发写不安全，会 panic」 | ✅ 成立且是关键理由。gorilla 要求调用方自行保证单写者；`coder/websocket` 内部处理并发写 |
| 「coder/websocket 原生支持 `context.Context`」 | ✅ 成立 |
| 维护活跃度 | ✅ coder/websocket 仍更活跃 |

**建议表述改为**：两者都在维护中，但 `coder/websocket` 的**并发写安全**与**原生 context 支持**更适合本项目（WebSocket 会同时承载 PTY 输出泵与心跳，多写者场景几乎必然出现）；gorilla 的并发写 panic 是 Go WebSocket 生产事故的最常见来源。

> 这个更正很重要：**基于错误事实做出的正确决定，在下次评审时会被推翻**。文档里的事实性论据必须准确。

### 3.5 【遗漏】cwd 跟随依赖 `Getwd`，但 `PathResolver` 接口未包含它

§7.1 的抽屉布局与 §3.2 的形态设计都把「cwd 自动跟随当前浏览目录」作为核心卖点，§7.2 也写了「订阅 `filelist:navigate` 事件」。

但前端拿到的 `detail.path` 是**虚拟路径**（如 `/data/nginx`），而后端执行命令需要**真实磁盘路径**。这中间的翻译由 `PathResolver.Resolve()` 完成——**这部分设计是完整的**。

真正的问题是另一个方向：**如果要在抽屉里显示「当前 cwd 的真实路径」或让用户手动输入路径**，前端就需要一个「虚拟路径 → 真实路径」的查询接口。文档没有提供这类只读端点，也没有说明是否展示真实路径。

**影响**：中低。不影响核心链路（执行命令），但会让「cwd 显示」这一 UI 元素缺乏数据来源。建议明确：**抽屉里只显示虚拟路径**，真实路径由服务端在执行时解析、仅在审计日志中记录。

### 3.6 【遗漏】`/ops` 路由命名与 `basePath` 的叠加行为未定义

主站支持 `basePath: /files` 子目录部署。ops 提供了「⤢ 弹出到新窗口」跳转 `/ops?cwd=<虚拟路径>`。

但文档没有说明：**在 `basePath` 非空时，`/ops` 的实际 URL 是什么？** 是 `/files/ops` 还是 `/ops`？

- `stripBase` 中间件会剥掉前缀，所以 mux 内部看到的仍是 `/ops`，路由注册没问题；
- 但**前端拼跳转链接时必须带上 `basePath`**，否则弹出窗口会 404。

§2.5 的挂载点用了 `data-ops-base="__FILELIST_BASE__"`，说明作者意识到了 basePath 的存在，但**独立页 `/ops` 的链接拼接规则没有写清楚**。建议在 §7.1 补充：弹出链接 = `{basePath}/ops?cwd=...`。

### 3.7 【不一致】耦合面清单内部矛盾

§1.4 结论摘要说「**4 个文件、约 6 行**」，§2.1 表格标题也说「目标 4 文件 + 6 行」，但表格实际列了 **6 行**（其中第 5 项是 `app.js` 的 1 行事件广播），且 §2.1 正文结论写的是「**总计：5 个文件、约 6 行**」。

三处数字不一致（4 文件 / 5 文件 / 6 行 vs 表格 6 项）。另外 §11 差异说明表又写「**5 文件、约 6 行**（含适配器 40 行）」。

**建议**：统一为一张权威表格并只引用它。同时**把「适配器 40 行」计入**——它是新增文件，不应被排除在耦合面统计之外（否则「6 行」这个数字有美化嫌疑）。考虑到 §3.3 指出的 `Server` 结构体改动，实际耦合面应重新核算。

---

## 四、与状态面板方案的对照

两份方案都采用「独立子包 + 独立前端资产」，方法论同源。差异如下：

| 维度 | OPS-PANEL | STATUS-PANEL | 说明 |
| :--- | :--- | :--- | :--- |
| 后端边界 | `internal/ops` | `internal/status` | **完全一致** |
| 日志注入 | `Logger` 接口（3 方法） | `Logf` 函数回调（1 字段） | 各有取舍；ops 的表达力更强 |
| 路径依赖 | 需注入 `PathResolver`（cwd 沙箱） | 只需 `MountPoint` 纯数据（磁盘占用统计） | **状态面板依赖更轻** |
| 路由注册 | 主包持有 `s.opsHandler` 字段 | 子包 `Mount(mux, opts)` 自行注册 | 状态面板对主包侵入更小 |
| 前端形态 | 主站内抽屉（**必须耦合**） | 独立页 `/status`（可完全解耦） | ops 的耦合是功能需求决定的，非设计缺陷 |
| 前端技术 | Alpine（与主站一致） | 原生 ES 模块 | 状态面板选 ES 模块是为满足「前端模块化」 |
| 通信 | WebSocket（双向必需） | HTTP 轮询（只读，无需长连接） | 状态面板无 CSWSH 风险面 |
| 安全等级 | 极高（root shell） | 低（只读，无鉴权门禁） | 由功能性质决定 |

**一个重要观察**：ops 因为「cwd 跟随」这个功能需求，**必须**侵入主站前端（空挂载点 + 事件广播）；而状态面板没有这个需求，因此能做到**主站前端零改动**（仅工具栏一个按钮）。

这印证了一个判断：**耦合度不是纯粹的设计水平问题，也受功能需求约束**。ops 已经把它压到了需求允许的下限。

---

## 五、给 OPS-PANEL 的修改建议（按优先级）

| # | 优先级 | 问题 | 建议 |
| :--- | :--- | :--- | :--- |
| 1 | **高** | `MapVirtualToReal` 返回值写成 `(string, error)` | 改为 `(string, bool)` 并包一层错误转换（§3.1） |
| 2 | **高** | 适配器引用不存在的 `logInfo/logWarn/logError` | 改用包级 `logger.Info/Warn/Error`，**并处理 printf 风格 vs kv 风格的语义转换**（§3.2） |
| 3 | **高** | `s.opsHandler` 字段不存在，改动量被低估 | 明确 `Server` 结构体 + `NewServer` 的改动，重新核算耦合面（§3.3） |
| 4 | **中** | gorilla/websocket「已归档」失实 | 更正论据，改用「并发写安全 + context 支持」作为选型理由（§3.4） |
| 5 | **中** | 耦合面数字三处不一致，且未计入适配器 | 统一为一张权威表，计入 40 行适配器（§3.7） |
| 6 | **中** | `/ops` 弹出链接未定义 basePath 拼接规则 | 补充「链接 = `{basePath}/ops?cwd=...`」（§3.6） |
| 7 | **低** | 未说明 cwd 是否展示真实路径 | 明确只显示虚拟路径，真实路径仅入审计日志（§3.5） |
| 8 | **低** | 缺少「子包可脱离主包独立测试」的显式验证项 | 补一条：用假 `Resolver` 跑子包单测，不启动 HTTP、不访问真实磁盘根 |

---

## 六、可直接复用到状态面板的改进（已执行）

评审过程中确认有价值并已合入 `PLAN-STATUS-PANEL-20260918.md` 的三项：

| # | 改进 | 来源 | 落点 |
| :--- | :--- | :--- | :--- |
| 1 | **新增 V8 验证项**：子包单测用假 `Collector` + 假 `Logf` 运行，不启动 HTTP、不读真实 `/proc`、不依赖真实主机 | ops §9.4「子包可独立测试」 | 状态面板 §3.5 验证清单 |
| 2 | **显式记录 `Logf` vs `Logger` 接口的取舍** | ops §2.3 接口注入 | 状态面板 §3.1 |
| 3 | **记录 `filelist:navigate` 事件契约的复用条件**（未来若做进程 cwd 跳转联动，复用而非另造） | ops §2.5 第 3 层 | 状态面板 §14 后续增强 |

---

## 七、评审结论

**OPS-PANEL 的低耦合设计是合格且优秀的，方法论可以直接作为项目内「可选模块」的标准范式**：

1. **后端**：`internal/<module>` 子包 + 接口定义在消费方 + 条件注册路由；
2. **前端**：空挂载点 + 运行时 DOM 自建 + 独立组件注册 + 事件总线通信；
3. **验证**：`go list -deps` 查反向依赖 + 可卸载性验证 + 符号搜索；
4. **契约**：把跨模块契约压缩到最少符号并固化进 `DESIGN.md`。

**但实施前必须先修正 §3.1–3.3 的三处代码级错误**——它们都在承重的集成路径上，写错会直接导致阶段一编译失败。§3.4 的外部论点失实也需更正，否则「基于错误事实的正确决定」会在下次评审时被质疑。

两份方案（OPS-PANEL 与 STATUS-PANEL）**互不冲突、可并行实施**，且共享同一套模块化范式。建议在 `docs/DESIGN.md` 中新增一节「可选模块接入规范」，把上述 4 条范式固化为项目约定，后续任何可选模块都照此办理。
