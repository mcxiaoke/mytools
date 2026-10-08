# FileList 索引与目录浏览排除规则解耦设计与实施方案

> 文档编号：DESIGN-EXCLUDE-DECOUPLING-20261008  
> 创建时间：2026-10-08 15:47:00 (GMT+8)  
> 状态：实施完毕并通过全部回归验证 (Completed)  
> 核心目标：彻底解耦“全文检索内存排除”与“实时目录浏览显示”的职责边界，确保所有开发环境目录与构建产物在 Web 目录浏览中完整可见、可点入、可下载，同时后台索引依然保持轻量低内存。

---

## 1. 问题背景与根因分析

### 1.1 现象与问题反馈
在引入 40+ 常见开发环境目录排除清单（内置 `msys64`、`Scoop`、`flutter`、`android-sdk`、`venv`、`target`、`bin`、`obj` 等）以解决 245 万条目占用 4~5GB 内存的问题后，用户反馈：
> “exclude实现有重大问题，现在exclude的文件直接目录浏览也看不到文件，这明显不对，exclude这些东西应该只对索引生效，或者说indexer和browse的exclude等规则应该分开 严重错误”

用户在 Web 前端浏览目录时，所有被排除的目录（如 `msys64`、`bin`、`target`、`node_modules` 等）全部消失，无法查看其子目录也无法下载其中的文件。

### 1.2 根因定位（强耦合设计缺陷）
历史代码中存在概念混淆：早期实现中误将“索引过滤”等同于“全局隐藏”，在 `src/indexer.go` 的 `ListDir` 函数中直接复用了 `idx.Excluded()`：
```go
// 历史旧代码缺陷：
for _, de := range dirEntries {
    if idx.Excluded(de.Name(), de.IsDir()) {
        continue // 导致网页目录浏览把排除项全部过滤掉了！
    }
}
```
早期 `defaultExcludeDirs` 仅有 5 个少见目录（`.git`, `.svn`, `.hg`, `node_modules`, `.idea`），未充分暴露该问题。但当排除清单扩展覆盖开发工具链与编译产物时，直接破坏了文件管理器的核心浏览与下载功能。

### 1.3 核心原则：三类操作的职责边界

| 操作类型 | 目标 | 应该排除什么 | 为什么 |
| :--- | :--- | :--- | :--- |
| **Indexer (后台索引)** | 控制常驻内存，加速全局搜索 | **重型开发与缓存目录**（`msys64`, `node_modules`, `target`, `venv` 等数十万碎片） | 避免海量碎片文件将常驻活堆撑大到数 GB，且用户极少全局搜索依赖代码 |
| **Browse (实时目录浏览)** | 目录导航、结构查看、文件下载 | **默认不隐藏任何开发目录**（仅过滤系统垃圾如 `.DS_Store`、`Thumbs.db` 与敏感凭据） | 用户主动点入文件夹，期望看到真实存在的文件；隐藏开发目录会导致用户误以为文件丢失 |
| **Upload / Edit (上传与编辑)** | 保证系统与凭据安全 | **敏感凭据与私钥**（`.env`, `id_rsa`, `*.pem`, `*.key` 等） | 纯粹属于安全拦截范畴，与开发目录无关 |

---

## 2. 架构设计与配置模型

### 2.1 整体拓扑模型

```
                      配置文件 config.yaml
                                │
          ┌─────────────────────┴─────────────────────┐
          ▼                                           ▼
┌─────────────────────────┐               ┌─────────────────────────┐
│     index: (索引配置)    │               │    browse: (浏览配置)   │
│ - excludeDirs: [...]    │               │ - excludeDirs: []       │
│ - excludeFiles: [...]   │               │ - excludeFiles: [...]   │
│ - builtinExcludes: true │               │   (.DS_Store, .env 等)  │
└─────────┬───────────────┘               └───────────┬─────────────┘
          │                                           │
          ▼ 仅用于后台扫描                             ▼ 仅用于实时前端目录读取
┌─────────────────────────┐               ┌─────────────────────────┐
│   CompactIndex 扫描构建  │               │   Server ListDir 浏览   │
│ (不索引 msys64/target 等)│               │ (全部开发目录正常可见/点击)│
└─────────────────────────┘               └─────────────────────────┘
```

### 2.2 配置定义（`config.yaml`）

配置项本身挂在 `index:` 节下，其语义严格限定为“仅对索引生效”；新增独立的 `browse:` 配置节控制前端实时目录呈现：

```yaml
# 索引与搜索配置：仅控制后台全文检索与常驻内存
index:
  interval: 5m
  fullInterval: 24h
  rescanDepth: 3
  builtinExcludes: true       # 仅对索引生效：自动跳过 .git, node_modules, msys64, venv, target 等，不占内存
  excludeDirs:                # 仅对索引生效：自定义不建立索引的目录（在浏览中依然可见）
    - .git
    - node_modules
    - __pycache__
    - $RECYCLE.BIN
    - System Volume Information
  excludeFiles:               # 仅对索引生效：自定义不建立索引的文件
    - .env
    - '*.key'

# 目录浏览配置：仅控制 Web 页面列表显示（独立解耦）
browse:
  excludeDirs: []             # 浏览时要隐藏的目录（默认为空 []，所有真实开发目录均可见可点）
  excludeFiles:               # 浏览时要隐藏的文件（默认过滤系统垃圾与敏感文件）
    - .env
    - .env.*
    - id_rsa
    - '*.key'
    - .DS_Store
    - Thumbs.db
    - desktop.ini
```

---

## 3. 代码实现与改动细节

### 3.1 配置层（`src/config.go`）
- 在 `Config` 中新增 `Browse` 结构体：
  ```go
  Browse struct {
      ExcludeDirs  []string `yaml:"excludeDirs"`
      ExcludeFiles []string `yaml:"excludeFiles"`
  } `yaml:"browse"`
  ```
- 定义独立的默认名单：
  - `DefaultSensitiveFiles`：敏感凭据文件黑名单（`.env`, `id_rsa`, `*.pem`, `*.key` 等）；
  - `DefaultBrowseExcludeFiles`：目录浏览默认隐藏规则（敏感凭据 + `.DS_Store`、`Thumbs.db`、`desktop.ini`）；
- 提供专用访问器方法：
  - `(c *Config) BrowseExcludeDirs() []string`（未配置时默认空切片 `[]string{}`）；
  - `(c *Config) BrowseExcludeFiles() []string`（未配置时默认 `DefaultBrowseExcludeFiles`）；
  - `(c *Config) SensitiveFiles() []string`。

### 3.2 索引与浏览层（`src/indexer.go`）
- **保留 `Excluded(name, isDir)`**：严格仅用于 `Index` 规则；
- **新增 `BrowseExcluded(name, isDir)`**：严格按 `browse.excludeDirs` 与 `browse.excludeFiles` 判定；
- **新增 `SensitiveExcluded(name)`**：严格按 `DefaultSensitiveFiles` 判定敏感文件；
- **重构 `ListDir(vpath)`**：
  将过滤判定替换为 `idx.BrowseExcluded(de.Name(), de.IsDir())`。开发目录（如 `node_modules`, `msys64`, `target`, `bin`, `venv`）默认不再被过滤，用户在 Web 端可以正常浏览、点进子目录并下载文件。

### 3.3 服务与安全防护层（`src/server.go`）
- **上传防护（`handleUpload`）**：调用 `s.indexer.SensitiveExcluded(cleanName)`，精准拦截敏感凭据上传，不误伤普通开发文件；
- **在线编辑防护（`handleContent`）**：调用 `s.indexer.SensitiveExcluded(baseName)`，禁止直接编辑凭据与密钥；
- **目录打包下载（`handleDownloadZip`）**：
  - 目录：遵循 `s.indexer.BrowseExcluded(name, true)`（允许打包开发目录）；
  - 文件：跳过 `BrowseExcluded` 或 `SensitiveExcluded` 的文件（防止系统垃圾或私钥被打入 ZIP 泄露）。

---

## 4. 验证与回归测试

1. **单元测试验证**：
   - `src/config_test.go`：新增 `TestLoadConfig_BrowseDefaultsAndCustom`，验证 `browse` 默认值与自定义规则生效；
   - `src/indexer_test.go`：
     - `TestBuildIndex_ExcludeFiles`：验证索引排除与目录浏览过滤独立生效；
     - `TestBuildIndex_ExcludeDirsDecoupledFromList`：验证 `node_modules` 被排除在索引之外（`TotalEntries == 0`，不耗内存），但在 `ListDir` 中完全可见（`len(list) == 1`），且显式配置 `browse.excludeDirs` 时能够按需隐藏；
   - `src/server_test.go`：
     - 新增 `TestHandleList_BrowseShowsDevDirs`：端到端验证 HTTP 接口 `/api/list` 可以正常返回 `node_modules`，而 `/api/search` 检索不到；
     - 验证 `TestHandleUpload_ExcludeFiles` 敏感凭据上传阻断行为正常；
   - 运行 `go test ./...`：全量测试 1.4s 通过。
2. **端到端浏览器测试 (E2E)**：
   - 运行 `.\tests\run-e2e.ps1`：全量 **46 个 Playwright 测试全部一次性通过**（包括上传阻断、目录导航、面包屑回溯、图片网格、管理操作、Zip 打包等）。
