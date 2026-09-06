# FileList 实现方案设计文档

> **版本**: v2.0 | **更新**: 2026-09-06 | **状态**: 设计评审中

## 1. 概述

### 1.1 目标

FileList 是一个轻量级文件目录索引与搜索 Web 服务，核心功能为 **dir index + search**：

- HTTP 服务，网页浏览配置的目录（类似 caddy filebrowser / nginx autoindex）
- 基于增量索引的文件名/目录名搜索（非实时遍历磁盘）
- 路径映射（URL 路径与磁盘实际路径解耦）
- 全部只读，无上传，无登录/权限系统
- 跨平台：Windows + Linux，Linux 支持 systemd 开机启动

### 1.2 非目标

- 不做多用户/权限/登录
- 不支持文件上传/编辑/删除
- 不支持文件内容全文搜索（仅文件名/目录名匹配）
- 不做分布式/集群部署

### 1.3 技术选型

| 维度 | 选择 | 理由 |
|------|------|------|
| 语言 | Go | 单二进制跨平台编译，标准库覆盖 HTTP/embed，无运行时依赖 |
| 配置 | YAML (gopkg.in/yaml.v3) | 可读性好，支持注释，唯一外部依赖 |
| 前端 | 原生 HTML+CSS+JS（嵌入式单文件） | go:embed 内嵌，零构建工具链，无外部 CDN 依赖 |
| 索引持久化 | gob 编码二进制文件 | Go 原生支持，序列化/反序列化快，无需额外依赖 |
| 路由 | 标准库 http.ServeMux | 简单够用，无需引入第三方路由库 |

## 2. 架构设计

### 2.1 整体架构

```
┌─────────────────────────────────────────────────┐
│                   main.go                        │
│  ┌──────────┐  ┌──────────┐  ┌───────────────┐  │
│  │ Config    │  │ Indexer  │  │ HTTP Server   │  │
│  │ (YAML)    │→ │ (内存索引)│→ │ (mux+handlers)│  │
│  └──────────┘  └────┬─────┘  └───────┬───────┘  │
│                     │                 │          │
│               ┌─────┴──────┐   ┌─────┴──────┐  │
│               │ Background │   │ Web UI     │  │
│               │ Re-indexer │   │ (embed)    │  │
│               │ (goroutine)│   │ index.html │  │
│               └────────────┘   └────────────┘  │
│                     │                           │
│               ┌─────┴──────┐                    │
│               │ Persist    │                    │
│               │ (gob file) │                    │
│               └────────────┘                    │
└─────────────────────────────────────────────────┘
```

### 2.2 模块职责

| 模块 | 文件 | 职责 |
|------|------|------|
| 入口 | main.go | 解析 flag，加载配置，启动 indexer 和 HTTP server，信号处理与优雅关闭 |
| 配置 | config.go | YAML 解析，路径映射规范化，默认值填充，校验 |
| 索引引擎 | indexer.go | 内存索引构建/加载/持久化，路径映射（virtual↔real），目录列表，搜索，后台增量更新 |
| HTTP 服务 | server.go | 路由注册，请求处理，JSON 响应，静态文件服务 |
| 前端 | web/index.html | 单页应用：目录浏览、搜索、面包屑、排序、下载 |

### 2.3 关键数据流

#### 2.3.1 启动流程

```
main()
  ├── LoadConfig(path)                    // YAML → Config
  │     ├── 校验 roots 非空、path 非空
  │     ├── 规范化 URL（确保 / 前缀，去尾部 /）
  │     ├── 规范化磁盘路径（展开 ~，统一为 OS 路径）
  │     └── 填充默认值（host=0.0.0.0, port=8080, interval=5m）
  ├── NewIndexer(cfg)
  │     ├── 尝试从 persist 文件加载已有索引（快速启动）
  │     └── 解析 re-index interval
  ├── idx.Start()                          // 后台 goroutine
  │     ├── BuildIndex()                   // 首次全量索引
  │     ├── ticker ← interval              // 定时重建
  │     └── persist → gob file             // 持久化到磁盘
  ├── NewServer(cfg, idx)
  │     └── Routes() → mux
  └── httpSrv.ListenAndServe()             // 阻塞，直到收到信号
        └── SIGINT/SIGTERM → idx.Stop() + httpSrv.Shutdown()
```

#### 2.3.2 目录浏览请求流程

```
浏览器 URL: http://host:8080/#/data
  │
  ├── hashchange 事件 → navigate('/data')
  │     ├── 更新 state.path
  │     ├── renderBreadcrumb('/data')        // 面包屑导航
  │     └── loadDir('/data')
  │           ├── fetch GET /api/list?path=/data
  │           │     └── server.handleList()
  │           │           ├── indexer.MapVirtualToReal('/data')  // URL→磁盘路径
  │           │           ├── os.ReadDir(realPath)               // 实时读磁盘
  │           │           ├── 构建 []Entry（name,size,modTime,isDir）
  │           │           └── 排序：目录优先 + 字母序
  │           └── renderTable(entries)
  │                 ├── 前端排序（name/size/time，可切换升降序）
  │                 └── 渲染表格：图标 + 名称链接 + 大小 + 时间
  │
  └── 点击目录名 → hash 变化 → navigate(subpath) → 循环
  └── 点击文件名 → /raw/path → 文件预览/下载
```

#### 2.3.3 搜索请求流程

```
用户输入搜索框 (250ms 防抖)
  │
  ├── doSearch(query)
  │     ├── state.searchMode = true
  │     └── fetch GET /api/search?q=keyword
  │           └── server.handleSearch()
  │                 └── indexer.Search(query, 500)
  │                       ├── 全量遍历内存索引 entries
  │                       ├── 大小写不敏感匹配（name + path）
  │                       ├── 评分：精确>前缀>包含(name)>包含(path)
  │                       ├── 按分数降序 + 路径字母序排序
  │                       └── 截取 top 500
  │
  └── renderSearchResults(results)
        ├── 名称 + 路径（可点击跳转） + 大小 + 时间
        └── 点击路径 → navigate(dirPath) 退出搜索模式
```

#### 2.3.4 增量索引流程

```
Indexer.Start() goroutine:
  │
  ├── 初始全量索引 BuildIndex()
  │     ├── 遍历所有 roots
  │     ├── 每个 root: filepath.WalkDir 递归遍历
  │     ├── 构建 []Entry（virtual path）
  │     └── 持久化 → gob file
  │
  ├── ticker ← interval (默认 5m)
  │     └── BuildIndex()  // 重建全量索引
  │
  └── <-stopCh → return  // 优雅退出

注意：当前实现为全量重建，非真正的增量 diff。
对于 18 万+ 文件的场景，5 分钟重建一次约需 10 秒。
后续优化方向见 §5.2。
```

## 3. 已发现问题诊断

### 3.1 P0 - 前端空白页（CRITICAL）

**现象**: 用户启动服务后浏览器打开页面，不显示任何内容，无报错。

**根因**: `navigate()` 函数的早期返回守卫在初始化时误触发。

```js
// web/index.html line 129-137
function navigate(path, skipHash){
  path = path || '';
  if(path === state.path && !state.searchMode) return;  // BUG!
  // state.path 初始值为 ''，initPath 也是 '' → 直接 return
```

初始化时 `state.path = ''`，页面无 hash 时 `initPath = ''`，调用 `navigate('', true)` 时 `'' === ''` 为 true → **直接返回，不加载任何数据**。

**修复方案**: 初始化时不走守卫检查，或增加 `force` 参数绕过。

### 3.2 P0 - /api/roots 与前端数据结构不匹配（CRITICAL）

**现象**: 即使修好 P0-3.1，根目录页面仍会崩溃。

**根因**: API 返回结构与前端期望的结构不一致。

```json
// /api/roots 返回：
[{"url":"/data","path":"数据库"}]

// 前端 renderTable() 期望：
[{"name":"data","path":"/data","isDir":true,"size":0,"modTime":"..."}]
```

`renderTable()` 中 `items.sort()` 调用 `a.name.toLowerCase()` → `undefined.toLowerCase()` → **TypeError**。

**修复方案**: `/api/roots` 改为返回与 `/api/list` 相同的 `Entry` 结构，把 root 当作目录条目返回。

### 3.3 P1 - 根 URL `/` 的子路径路由失败

**现象**: 配置 `url: /` 时，访问 `/subdir` 无法匹配到该 root。

**根因**: `MapVirtualToReal` 中子路径匹配逻辑：

```go
// indexer.go line 71
rel := strings.TrimPrefix(vpath, r.URL+"/")
// 当 r.URL = "/" 时, r.URL+"/" = "//"
// strings.TrimPrefix("/subdir", "//") = "/subdir" → 不匹配
```

**修复方案**: 特殊处理 `r.URL == "/"` 的情况，或改用 `strings.HasPrefix` + 长度前缀匹配。

### 3.4 P1 - 非 ASCII 文件名下载头编码缺失

**现象**: 下载 `数据库.zip` 等中文文件名时，`Content-Disposition` header 可能乱码。

**根因**:

```go
// server.go line 121-122
w.Header().Set("Content-Disposition",
    fmt.Sprintf(`attachment; filename="%s"`, filepath.Base(realPath)))
```

直接将 UTF-8 文件名放入 header，未使用 RFC 5987 编码。

**修复方案**: 使用 `filename*=UTF-8''...` 编码。

### 3.5 P2 - 索引重建为全量重建，非真正增量

**现象**: 18 万文件每 5 分钟全量重建，约 10 秒，期间索引数据被替换。

**根因**: `BuildIndex()` 每次从头 `filepath.WalkDir`，不检查文件是否有变化。

**影响**: 功能正常但效率低，后续可优化。

### 3.6 P2 - 索引构建期间前端无反馈

**现象**: 首次启动时索引需要 10 秒，期间搜索返回空结果，用户无感知。

**修复方案**: `/api/stats` 增加 `building` 状态字段；前端显示索引中提示。

### 3.7 P2 - favicon.ico 404 日志噪音

**现象**: 每次页面加载产生 `GET /favicon.ico → 404` 日志。

**修复方案**: 内嵌一个 favicon 或对 `/favicon.ico` 返回 204。

### 3.8 P3 - 配置文件首次运行无引导

**现象**: 首次运行无 `config.yaml` 时直接 `log.Fatal`，用户不知所措。

**修复方案**: 检测无配置文件时，生成默认配置并提示用户编辑。

## 4. 详细设计

### 4.1 配置设计

#### 4.1.1 配置文件格式 (config.yaml)

```yaml
server:
  host: 0.0.0.0        # 监听地址
  port: 8080           # 监听端口

index:
  interval: 5m         # 重新索引间隔 (30s / 5m / 1h)
  persist: ./filelist.idx  # 索引持久化文件路径 (空=不持久化)
  maxDepth: 0           # 最大遍历深度 (0=无限)
  excludeDirs: []       # 排除目录名 (如 [".git", "node_modules", "$RECYCLE.BIN"])

roots:
  - url: /data          # 虚拟 URL 路径
    path: /mnt/data     # 实际磁盘路径
  - url: /media
    path: /media
  # Windows:
  # - url: /c
  #   path: C:/
```

#### 4.1.2 配置校验规则

| 规则 | 处理 |
|------|------|
| roots 为空 | 报错退出 |
| root.url 为空 | 报错退出 |
| root.path 为空 | 报错退出 |
| root.path 不存在 | 警告但继续（可能后续挂载） |
| root.path 不是目录 | 警告但继续 |
| URL 前缀冲突（如 `/data` 和 `/data/sub`） | 警告，按最长前缀优先匹配 |
| URL `/` 与其他 root 共存 | 警告（`/` 会捕获所有未匹配路径） |

#### 4.1.3 路径规范化

- URL：确保以 `/` 开头，去除尾部 `/`（除根 `/` 外）
- 磁盘路径：展开 `~` 为用户主目录；保持原始格式，由 `filepath.Join` 处理分隔符
- 交叉平台路径：配置中统一用 `/`，代码中 `filepath.FromSlash` 转换

### 4.2 路径映射设计

#### 4.2.1 Virtual → Real 映射规则

```
输入: vpath (如 "/data/subdir/file.txt")

1. 清洗: vpath = path.Clean("/" + vpath)  // 防 /../ 遍历
2. 遍历 roots，找最长前缀匹配:
   - 精确匹配: vpath == r.URL → return r.Path
   - 前缀匹配: vpath 以 r.URL+"/" 开头 → return filepath.Join(r.Path, rel)
3. 特殊处理 r.URL == "/":
   - 任何 vpath 都匹配，rel = vpath 去掉前导 /
   - 但优先级最低（放在 roots 最后，且其他 root 先匹配）
4. 无匹配 → 返回 ("", false)
```

#### 4.2.2 Real → Virtual 映射规则

```
输入: realPath (磁盘路径), rootURL, rootPath

1. rel = filepath.Rel(rootPath, realPath)
2. if rel == "." → return rootURL
3. else → return rootURL + "/" + filepath.ToSlash(rel)
```

#### 4.2.3 路径遍历防护

- 所有 virtual path 输入经过 `path.Clean("/" + input)` 清洗
- `path.Clean` 会将 `/../` 解析掉：`/data/../etc` → `/etc`
- 映射后的 realPath 一定在某个 root.Path 之下
- `/raw/` 端点额外检查 `os.Stat`，拒绝目录（防止列目录）

### 4.3 索引引擎设计

#### 4.3.1 数据结构

```go
type Entry struct {
    Name    string    `json:"name"`     // 文件/目录名 (basename)
    Path    string    `json:"path"`     // 虚拟 URL 路径 (如 /data/sub/file.txt)
    Size    int64     `json:"size"`     // 字节数
    ModTime time.Time `json:"modTime"`  // 修改时间
    IsDir   bool      `json:"isDir"`    // 是否目录
}

type Indexer struct {
    mu          sync.RWMutex
    entries     []Entry         // 全量内存索引
    cfg         *Config
    persistPath string
    interval    time.Duration
    stopCh      chan struct{}
    building    bool            // 是否正在构建索引
}
```

#### 4.3.2 索引构建流程

```
BuildIndex():
  1. 设置 building = true
  2. 遍历所有 roots:
     - os.Stat 检查路径可访问性
     - filepath.WalkDir 递归遍历
     - 跳过 excludeDirs 中的目录名
     - 对每个条目调用 d.Info() 获取元信息
     - mapRealToVirtual 转换为虚拟路径
     - 追加到 entries
  3. 加锁替换 idx.entries
  4. building = false
  5. 如果配置了 persist，gob 编码写入文件
  6. 记录日志：条目数 + 耗时
```

#### 4.3.3 搜索算法

```
Search(query, limit):
  1. query = strings.ToLower(strings.TrimSpace(query))
  2. 加读锁遍历 idx.entries
  3. 对每个 entry 评分:
     - name 精确匹配: 100
     - name 前缀匹配: 80
     - name 包含匹配: 60
     - path 包含匹配: 40
     - 不匹配: 跳过
  4. 按分数降序 + 路径字母序排序
  5. 截取 top limit 条
  6. 返回结果
```

**性能**: 全量遍历 O(n)，18 万条目约 <50ms（内存操作），可接受。

#### 4.3.4 目录列表（实时读磁盘）

```
ListDir(vpath):
  1. vpath = path.Clean("/" + vpath)
  2. MapVirtualToReal → realPath
  3. os.ReadDir(realPath) → []os.DirEntry
  4. 对每个 DirEntry 调用 .Info() 获取 Size + ModTime
  5. 构建 []Entry（path = path.Join(vpath, name)）
  6. 排序：目录优先 + 字母序（大小写不敏感）
  7. 返回
```

**为什么目录列表不走索引而是实时读磁盘？**
- 目录列表只读单层，os.ReadDir 一次系统调用，很快
- 保证文件列表的实时性（索引有延迟）
- 搜索才需要索引（因为搜索需要遍历全树）

#### 4.3.5 索引持久化

```
save():
  1. 读锁获取 entries 快照
  2. os.Create(persistPath) → 临时文件
  3. gob.NewEncoder(f).Encode(entries)
  4. 原子替换（写入完成后再 rename，或直接覆盖）

load():
  1. os.Open(persistPath)
  2. gob.NewDecoder(f).Decode(&entries)
  3. 写锁替换 idx.entries
```

**为何选 gob 而非 JSON？**
- gob 二进制编码比 JSON 体积小 40-60%，读写快 2-3 倍
- Go 原生支持，无外部依赖
- 缺点：不可读、跨语言不兼容，但索引文件仅供本程序使用

### 4.4 HTTP API 设计

#### 4.4.1 端点总览

| 方法 | 路径 | 说明 | 响应类型 |
|------|------|------|----------|
| GET | `/` | 返回嵌入式 Web UI (index.html) | text/html |
| GET | `/favicon.ico` | 返回内嵌 favicon | image/svg+xml 或 204 |
| GET | `/api/roots` | 返回配置的根目录列表 | JSON: Entry[] |
| GET | `/api/list?path=/{url}` | 返回目录内容 | JSON: Entry[] |
| GET | `/api/search?q={query}` | 搜索文件/目录名 | JSON: Entry[] |
| GET | `/api/stats` | 返回索引状态 | JSON: Stats |
| GET | `/raw/{url}/{path}` | 文件预览/下载 | 原始文件 |
| GET | `/raw/{url}/{path}?download=1` | 强制下载 | 原始文件 + Content-Disposition |

#### 4.4.2 统一响应结构

所有 `/api/*` 端点返回 JSON。成功返回数据数组/对象，失败返回 HTTP 错误码 + 纯文本错误信息。

**Entry 结构（统一）**:
```json
{
  "name": "file.txt",
  "path": "/data/subdir/file.txt",
  "size": 1024,
  "modTime": "2026-09-06T18:00:00+08:00",
  "isDir": false
}
```

**Stats 结构**:
```json
{
  "indexed": 188267,
  "building": false
}
```

#### 4.4.3 错误响应

| 场景 | HTTP 状态码 | 响应体 |
|------|-------------|--------|
| 路径未找到/不在任何 root 下 | 404 | `not found` |
| 目录不可访问（权限/不存在） | 404 | `not found` |
| 搜索查询为空 | 200 | `[]` |
| 索引正在构建中搜索 | 200 | `[]`（空结果，非错误） |
| 非 API/raw 路径 | 200 | 返回 index.html（SPA fallback） |

#### 4.4.4 /api/roots 修复设计

**问题**: 当前返回 `{url, path}`，前端期望 `Entry` 结构。

**修复**: 返回与 `/api/list` 相同的 `Entry` 结构，把每个 root 当作目录条目：

```go
func (s *Server) handleRoots(w http.ResponseWriter, r *http.Request) {
    entries := make([]Entry, len(s.cfg.Roots))
    for i, root := range s.cfg.Roots {
        info, err := os.Stat(root.Path)
        entries[i] = Entry{
            Name:  filepath.Base(root.Path), // 显示磁盘目录名
            Path:  root.URL,                 // 虚拟路径
            IsDir: true,
        }
        if err == nil {
            entries[i].Size = info.Size()
            entries[i].ModTime = info.ModTime()
        }
    }
    writeJSON(w, entries)
}
```

#### 4.4.5 /raw/ 端点设计

```
请求: GET /raw/data/subdir/file.txt?download=1
  1. 提取 vpath = "/data/subdir/file.txt"
  2. MapVirtualToReal → 磁盘路径
  3. os.Stat 检查: 存在且不是目录
  4. 如果 ?download=1:
     - Content-Disposition: attachment; filename*=UTF-8''<urlencoded-name>; filename="<ascii-fallback>"
  5. http.ServeFile(w, r, realPath)
     - 自动设置 Content-Type（基于扩展名）
     - 支持 Range 请求（断点续传）
     - 支持 Last-Modified / If-Modified-Since（304 缓存）
```

### 4.5 前端设计

#### 4.5.1 页面结构

```
┌──────────────────────────────────────────────────────┐
│ [📁 FileList]  [____ 搜索框 ____]      [188267项已索引] │  ← sticky header
├──────────────────────────────────────────────────────┤
│  根目录 / data / subdir                              │  ← 面包屑
├──────────────────────────────────────────────────────┤
│  名称          │ 大小     │ 修改时间     │            │  ← 表头(可排序)
├──────────────────────────────────────────────────────┤
│  📁 dir1       │ -        │ 2026-09-06  │            │
│  📁 dir2       │ -        │ 2026-09-06  │            │
│  📄 file.txt   │ 1.2 KB   │ 10:30       │  ⬇下载    │
│  📄 file2.log  │ 4.5 MB   │ 2026-09-05 │  ⬇下载    │
├──────────────────────────────────────────────────────┤
│              索引构建中... / 已索引 188267 项           │  ← 底部状态栏(可选)
└──────────────────────────────────────────────────────┘
```

#### 4.5.2 状态管理

```js
var state = {
  path: '',           // 当前浏览路径
  sortKey: 'name',    // 排序字段: name | size | time
  sortDir: 'asc',     // 排序方向: asc | desc
  searchMode: false,  // 是否在搜索模式
  searchQuery: '',    // 当前搜索词
  initialized: false  // 是否已完成首次加载 ← 新增，解决初始化守卫问题
};
```

#### 4.5.3 关键交互流程

**初始化加载**:
```
1. DOMContentLoaded
2. loadStats() → /api/stats → 显示索引状态
3. 读取 location.hash → initPath
4. navigate(initPath, force=true) ← 强制首次加载
5. 后续 hashchange → navigate(path) ← 正常守卫
```

**搜索**:
```
1. input 事件 → 250ms 防抖
2. 非空 → doSearch(query)
3. 进入搜索模式: searchMode=true
4. /api/search?q=... → renderSearchResults
5. 点击结果路径 → exitSearchMode + navigate(dirPath)
6. Esc / 清除按钮 → exitSearchMode + 恢复原目录
```

**排序**:
```
1. 点击表头 → setSort(key)
2. 同列: 切换 asc/desc
3. 不同列: 新列 asc
4. 重新渲染（不重新请求 API，前端排序）
```

#### 4.5.4 前端错误处理

| 场景 | 处理 |
|------|------|
| API 返回非 200 | 显示错误占位符 `⚠ 加载失败: {message}` |
| API 返回空数组 | 显示 `📁 空目录` |
| 搜索无结果 | 显示 `🔍 未找到匹配 "{q}" 的文件` |
| 索引构建中 | 搜索结果为空时显示 `索引构建中，请稍候...` |
| 网络错误 | 显示 `⚠ 网络错误` |

#### 4.5.5 样式原则

- 简洁现代：浅色背景 + 白色卡片 + 圆角 + 微阴影
- 系统字体栈：`system-ui, -apple-system, "Segoe UI", Roboto, sans-serif`
- CSS 变量定义主题色，方便后续切换暗色模式
- 响应式：移动端隐藏时间列，缩小路径列
- 图标用 Unicode emoji（📁📄⬇⚠🔍），无外部图标库依赖

### 4.6 安全设计

| 威胁 | 防护措施 |
|------|----------|
| 路径遍历 (`/../../../etc/passwd`) | `path.Clean` 清洗 + root 边界检查 |
| 目录列览 via /raw/ | `os.Stat` 拒绝目录，仅服务文件 |
| 敏感文件泄露 | 配置只读，无写入能力；不解析/执行脚本 |
| 大文件 DoS | `http.ServeFile` 支持 Range，流式传输 |
| 配置注入 | YAML 解析有类型安全，路径校验 |
| 并发竞争 | `sync.RWMutex` 保护索引读写 |

### 4.7 跨平台设计

| 差异点 | Windows | Linux |
|--------|---------|-------|
| 路径分隔符 | `\` (`filepath.Join` 自动处理) | `/` |
| 配置中路径写法 | `C:/Users/...`（用正斜杠） | `/home/user/...` |
| `~` 展开 | `%USERPROFILE%` via `os.UserHomeDir()` | `$HOME` via `os.UserHomeDir()` |
| 开机启动 | 手动或计划任务 | systemd service |
| 信号处理 | SIGINT (Ctrl+C) | SIGINT + SIGTERM |
| 权限模型 | 运行用户权限 | 建议专用 `filelist` 用户 |
| 排除目录 | `$RECYCLE.BIN`, `System Volume Information` | `.git`, `node_modules` |

### 4.8 部署设计

#### 4.8.1 编译

```bash
# 当前平台
go build -ldflags '-s -w' -o filelist .

# Linux amd64 (交叉编译)
GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build -ldflags '-s -w' -o filelist .

# Windows amd64
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -ldflags '-s -w' -o filelist.exe .
```

`-s -w` 去除调试信息，减小二进制体积。

#### 4.8.2 Linux systemd 部署

```ini
# /etc/systemd/system/filelist.service
[Unit]
Description=FileList - File Directory Index & Search
After=network.target

[Service]
Type=simple
ExecStart=/usr/local/bin/filelist -config /etc/filelist/config.yaml
User=filelist
Group=filelist
WorkingDirectory=/var/lib/filelist
Restart=on-failure
RestartSec=5

# 安全加固
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/filelist
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```

安装步骤:
```bash
sudo useradd -r -s /usr/sbin/nologin -d /var/lib/filelist filelist
sudo mkdir -p /etc/filelist /var/lib/filelist
sudo cp filelist /usr/local/bin/
sudo cp config.yaml /etc/filelist/
sudo cp deploy/filelist.service /etc/systemd/system/
sudo chown -R filelist:filelist /var/lib/filelist
sudo systemctl daemon-reload
sudo systemctl enable --now filelist
```

## 5. 优化方向（后续迭代）

### 5.1 搜索性能优化

当前全量遍历 O(n) 对 18 万条目约 50ms，可接受。如果未来索引量到百万级：

- 构建 name → entries 的 map 倒排索引（前缀树/哈希表）
- 搜索时先查倒排索引，再打分排序

### 5.2 真正的增量索引

当前为定时全量重建。可优化为：

- 基于文件 ModTime 的增量检测：遍历时比对上次索引的 ModTime，仅更新变化的条目
- 基于 fsnotify 的实时监听：监听文件系统事件，实时更新索引（Linux inotify / Windows ReadDirectoryChangesW）
- 但 fsnotify 对大目录树的 watch 资源消耗大，需要权衡

### 5.3 其他

- 暗色模式（CSS 变量已预留）
- 文件类型图标（按扩展名区分图标）
- 搜索结果分页
- 目录列表分页
- WebSocket 推送索引状态
- 配置热重载

## 6. 修复计划

基于以上诊断和设计，需要修改的文件和具体改动：

| 文件 | 改动 | 优先级 |
|------|------|--------|
| web/index.html | 修复 navigate 初始化守卫；统一 roots/list 数据处理；增加索引构建中提示；favicon 处理；样式微调 | P0 |
| server.go | /api/roots 返回 Entry 结构；增加 /favicon.ico 处理；Content-Disposition RFC 5987 编码 | P0/P1 |
| indexer.go | MapVirtualToReal 修复 `/` root 子路径匹配；building 状态字段；excludeDirs 支持 | P1 |
| config.go | 增加 excludeDirs/maxDepth 配置项；URL 冲突检测 | P2 |
| main.go | 首次运行生成默认配置；日志格式优化 | P3 |
