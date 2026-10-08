# FileList 紧凑索引架构设计方案（Compact Indexer）

> 文档编号：DESIGN-COMPACT-INDEX-20261008  
> 创建时间：2026-10-08 13:45:00 (GMT+8)  
> 状态：待评审 (Proposed)  
> 目标：将百万级文件索引的常驻内存开销从 GB 级降至 100~200MB（达到 Everything 内存利用率水准），彻底消除启动与保存时的内存尖峰。

---

## 1. 背景与问题定位

### 1.1 现状与实测瓶颈
在用户实际机器上，FileList 挂载了 `C:\Home` 与 `F:\Temp` 两个目录。排查记录显示：
- **索引条目总数**：**2,454,659**（约 245.5 万条目，包含 35 万个目录和 210 万个文件）。
- **运行内存**：
  - **峰值工作集（PeakWorkingSet）**：**5.09 GB**；
  - **常驻工作集（WorkingSet）**：**1.78 GB**；
- **磁盘持久化文件**：`data/filelist.idx` 大小达到 **637 MB**。

### 1.2 索引条目来源分布
通过解析实际持久化索引，条目来源分布如下：
1. `chome/Develop`：**1,087,735** 条目（占 **44.3%**）
   - 其中 `Scoop`（22.1万）、`msys64`（19.8万）、`flutter`（18.4万）、`android-sdk`（14.7万）、`venv`（4.7万）等开发工具链和 SDK 占据了绝对大头；
2. `ftemp/apk`：**362,960** 条目（占 **14.8%**，APK 批量解压/缓存）；
3. `chome/Temp`：**328,406** 条目（占 **13.4%**，临时文件）；
4. `chome/Projects`：**213,632** 条目（占 **8.7%**）；
5. `chome/Games`：**190,849** 条目（占 **7.8%**）。

### 1.3 根因剖析（现有架构的缺陷）
当前 `src/indexer.go` 采用“纯内存 Map + 全路径字符串 + GOB 反射序列化”架构，主要存在以下致命缺陷：
1. **全路径冗余存储（Path Prefix Bloat）**：
   每个文件都以独立的完整虚拟路径作为 key 和字段存储（如 `/chome/Develop/msys64/usr/include/sys/types.h`）。210 万个文件导致目录前缀被重复拷贝了数百万次，浪费数百兆内存。
2. **Go Map 桶开销与碎片化（Hash Map Overhead）**：
   `map[string]Entry` 在 Go runtime 中为每 8 个元素分配一个 bucket，245 万个条目仅哈希桶本身就需要超过 400MB 内存。
3. **海量独立堆对象导致 GC 压力剧增（12M+ Heap Objects）**：
   现有每个 `Entry` 拥有 `Name`、`Path`、`lname`、`lpath` 4 个独立字符串对象，加 map key 相当于每个条目分配 5 个堆对象。总计产生了超过 **1200 万个独立的 Go 堆对象**！Go GC 在每轮标记时必须递归追踪这 1200 万个指针，GC 停顿和 CPU 占用居高不下。
4. **冗余小写字段（Duplicate Lowercase Cache）**：
   为了加快检索，现有模型为每个 Entry 预计算并持久化保存了 `lname` 和 `lpath`，平白多出 490 万个堆对象（~500MB）。
5. **GOB 反射编解码的双倍重叠尖峰（Memory Spike during Save/Load）**：
   - 启动加载时：GOB 反射解码先生成一份 245 万条目的 Map，随后代码再遍历生成第二份 Map 并计算小写，堆内存瞬间冲上 2.8GB。
   - 扫描保存时：`idx.save()` 使用 GOB 编码 245 万个结构体，流式缓冲区和内部反射产生海量临时对象，直接将进程虚拟内存推高至 **5.03 GB**。

---

## 2. 行业标杆调研：Everything 底层内存原理

voidtools 的 **Everything** 在 Windows 下索引 100 万个文件仅占用约 75MB 内存，索引 400 万个文件仅占用 200~250MB。平均每个文件开销仅 **50~65 字节**。其核心架构思想如下：

```
Everything 内存结构示意图：

+-----------------------------------------------------------+
| Folders Table (紧凑连续切片)                                |
| [Folder 0] -> Parent: 0, NameOff: 0, NameLen: 5, Mtime... |
| [Folder 1] -> Parent: 0, NameOff: 5, NameLen: 7, Mtime... |
+-----------------------------------------------------------+
| Files Table (紧凑连续切片, 无指针, noscan)                 |
| [File 0]   -> FolderID: 1, NameOff: 12, NameLen: 8...     |
| [File 1]   -> FolderID: 1, NameOff: 20, NameLen: 6...     |
+-----------------------------------------------------------+
| String Arena (连续文件名大缓冲池)                          |
| "chome" + "Develop" + "main.go" + "app.js" ...            |
+-----------------------------------------------------------+
```

1. **父目录索引拓扑（Parent Index Hierarchy）**：
   文件从不存储完整路径，仅存储所属目录的整数序号（`FolderID: uint32`）。完整路径在检索命中并呈现时，由 `FolderID` 向上链式回溯到根目录动态还原。
2. **连续扁平数组（Flat Contiguous Slices / SoA）**：
   所有目录和文件分别保存在扁平数组中，完全消除哈希表的桶开销与链表跳转。
3. **紧凑字符串池（String Arena）**：
   所有文件名统一紧密存放在一个连续的 `[]byte` 缓冲池中，条目仅记录 `NameOff: uint32` 和 `NameLen: uint16`。
4. **纯值类型与无指针设计（Zero-Pointer / `noscan`）**：
   结构体内不含任何内存指针（全为标量数值）。在 Go 运行时中，无指针切片会被标记为 `noscan`，**Go GC 标记阶段彻底跳过对这片内存的递归遍历**，GC 停顿降至 0ms。
5. **流式大小写折叠检索（On-the-Fly Case Folding）**：
   不预先缓存全小写字符串，检索时使用高效的内联 ASCII 大小写折叠比对。在现代 CPU 上，线性扫描 100MB 连续内存仅需 5~15ms。

---

## 3. 新一代紧凑索引（Compact Indexer）技术规范

### 3.1 核心数据结构

```go
package indexer

// 1. 紧凑目录条目（数量通常仅占总条目的 10%~15%）
type CompactFolder struct {
	ParentID uint32 // 父目录在 Folders 切片中的索引（根目录为 0xFFFFFFFF）
	NameOff  uint32 // 目录名在 StringArena 中的字节起始偏移
	NameLen  uint16 // 目录名长度（最大 65535 字节）
	ModTime  int64  // Unix 秒级时间戳（8 字节，替代 time.Time 的 24 字节）
	Size     int64  // 目录大小 / 统计（8 字节）
} // 结构体大小: 26 字节 -> 内存对齐后 32 字节（完全无指针，noscan）

// 2. 紧凑文件条目（占总条目的 85%~90%）
type CompactFile struct {
	FolderID uint32 // 所属父目录在 Folders 切片中的索引（4 字节）
	NameOff  uint32 // 文件名在 StringArena 中的字节起始偏移（4 字节）
	NameLen  uint16 // 文件名长度（2 字节）
	Size     int64  // 文件大小（8 字节）
	ModTime  int64  // Unix 秒级时间戳（8 字节）
} // 结构体大小: 26 字节 -> 内存对齐后 32 字节（完全无指针，noscan）

// 3. 连续字符串池
type StringArena struct {
	buf []byte // 连续紧凑字节缓冲池
}

func (a *StringArena) Append(s string) (uint32, uint16) {
	off := uint32(len(a.buf))
	a.buf = append(a.buf, s...)
	return off, uint16(len(s))
}

func (a *StringArena) Get(off uint32, len uint16) string {
	return string(a.buf[off : off+uint32(len)])
}

// 4. 紧凑索引容器
type CompactIndex struct {
	Folders []CompactFolder // 所有目录
	Files   []CompactFile   // 所有文件
	Arena   StringArena     // 字符串池

	// 辅助查找（用于增量扫描与快速路径定位）：
	// 仅建立 FolderPath -> FolderID 的轻量哈希映射（目录通常只有数十万，开销小）
	folderMap map[string]uint32
}
```

### 3.2 内存预算精密推算（按 250 万条目实测数据）

假设系统包含 **35 万个目录** 和 **215 万个文件**，平均文件名长度 15 字节：

| 模块 | 计算公式 | 内存占用 |
| :--- | :--- | :--- |
| `[]CompactFolder` | 350,000 × 32 B | **11.2 MB** |
| `[]CompactFile` | 2,150,000 × 32 B | **68.8 MB** |
| `StringArena` 字节流 | 2,500,000 × 15 B | **37.5 MB** |
| `folderMap`（目录哈希表） | 350,000 × (16B + 4B + Bucket) | **~18 MB** |
| **总计常驻内存** | — | **约 135 MB** |

对比结论：**从 1.78 GB 骤降至 135 MB，节省 92% 的常驻内存！GC 堆对象数从 1200 万个直接骤降至个位数（切片本身）！**

### 3.3 搜索算法与路径还原

#### 步骤 1：流式无分配搜索
检索时不创建任何小写字符串，使用专用内联折叠匹配函数：
```go
func asciiContainsFold(s, substr string) bool {
    // 快速 ASCII 大小写折叠包含比对，零内存分配
}
```
遍历 `Files` 切片时：
```go
for i := range idx.Files {
    f := &idx.Files[i]
    name := idx.Arena.Get(f.NameOff, f.NameLen)
    if asciiContainsFold(name, query) {
        // 记录匹配项索引与打分
    }
}
```
在现代 CPU 架构下，连续内存布局（Cache-friendly）使得遍历 200 万项的耗时仅在 **8~15 毫秒** 之间。

#### 步骤 2：路径按需回溯还原（仅对前 N 个结果）
前端 API 只返回分页后的前 50~100 条记录。仅对这 50~100 个结果通过 `FolderID` 向上回溯：
```go
func (idx *CompactIndex) BuildFullPath(f *CompactFile) string {
    var parts []string
    parts = append(parts, idx.Arena.Get(f.NameOff, f.NameLen))

    curID := f.FolderID
    for curID != 0xFFFFFFFF {
        folder := &idx.Folders[curID]
        parts = append(parts, idx.Arena.Get(folder.NameOff, folder.NameLen))
        curID = folder.ParentID
    }
    // 逆向拼接出完整 virtual path
    return joinReverse(parts)
}
```
这样完全避免了为几百万个未命中的文件维护完整路径字符串。

### 3.4 紧凑二进制持久化格式（替代 GOB）

放弃 GOB，设计专用的二进制紧凑格式 `FLIX`（FileList Index v3）：

```
+-------------------------------------------------------+
| Magic (4B) | Version (2B) | FolderCount (4B) | FileCount (4B) | ArenaLen (4B) |
+-------------------------------------------------------+
| Folders Block: [CompactFolder 字节流 (连续紧凑存储)]    |
+-------------------------------------------------------+
| Files Block:   [CompactFile 字节流 (连续紧凑存储)]      |
+-------------------------------------------------------+
| String Arena:  [原始字节流]                           |
+-------------------------------------------------------+
```

- **序列化速度**：直接将底层字节切片写入文件（可串接 Snappy / ZSTD 快速压缩），耗时由现有的 3.5 秒缩短至 **< 0.1 秒**。
- **内存尖峰消除**：由于是直接写入或分块流式写入，没有任何反射与临时对象分配，彻底消除 5GB 内存尖峰。
- **文件体积**：磁盘占用由 **637 MB 缩减至 15~25 MB**。

---

## 4. 内置排除策略（Built-in Excludes）规范

在 Compact Indexer 上线前/并行期，必须立刻防止海量无关开发环境目录拖垮索引。

### 4.1 内置排除清单定义
将以下 10 大类、40+ 常见开发环境小文件重灾区定义为系统内置排除：

```go
var BuiltinExcludeDirs = []string{
    // 1. 版本控制
    ".git", ".svn", ".hg", ".bzr",
    // 2. 前端与 Node
    "node_modules", ".pnpm-store", ".yarn", ".npm", ".next", ".nuxt",
    // 3. Python 虚拟环境与缓存
    "venv", ".venv", "env", "__pycache__", ".pytest_cache", ".mypy_cache", ".tox", ".conda",
    // 4. Java / Kotlin
    ".gradle", ".m2",
    // 5. Rust / Go
    "target", "vendor",
    // 6. 移动端与大型 SDK
    "flutter", "android-sdk", ".dart_tool", "Pods", ".carthage", "DerivedData",
    // 7. Windows 工具链与包管理（单目录动辄数十万小文件）
    "msys64", "msys32", "cygwin", "cygwin64", "w64devkit", "Scoop", "ScoopApps",
    // 8. 编译二进制产物
    "bin", "obj", ".vs", "cmake-build-*",
    // 9. IDE 与 编辑器缓存
    ".idea", ".vscode", ".fleet", ".cache",
    // 10. 系统垃圾与卷信息
    "$RECYCLE.BIN", "System Volume Information",
}
```

### 4.2 配置合并逻辑与退出机制
1. **自动合并（Union Merge）**：
   用户在 `config.yaml` 中配置的 `index.excludeDirs` 会与 `BuiltinExcludeDirs` 自动取并集（去重），避免因为用户配了自定义项而把内置防护全部覆盖。
2. **退出机制（Opt-out Knob）**：
   增加配置项 `index.builtinExcludes: false`（默认 `true`）。若用户确实有教学或调试需求需要索引 `node_modules`，可显式设为 `false` 禁用内置清单。

---

## 5. 收益对比与分阶段实施计划

### 5.1 收益对比矩阵

| 指标 | 当前架构 (v0.2.0) | 仅做内置排除 (Phase 1) | Compact Indexer (Phase 2) |
| :--- | :--- | :--- | :--- |
| **当前用户环境条目数** | 245.5 万 | **约 10~15 万** | 约 10~15 万（或全盘数百万） |
| **常驻内存 (RSS)** | 1.78 GB | **~30 MB** | **~8 MB** |
| **启动/保存峰值内存** | 5.09 GB | **~80 MB** | **~15 MB** |
| **索引文件大小** | 637 MB | ~20 MB | ~15 MB |
| **极限量产能力** | >200万文件容易 OOM | 规避了重灾区 | **轻松支撑 500 万文件 (~200MB)** |

### 5.2 分阶段实施路线

- **阶段一（立即实施）**：
  - 落地 `BuiltinExcludeDirs` 与配置合并逻辑；
  - 增加单元测试确保合并规则与退出开关生效；
  - 更新配置示例文档。
- **阶段二（架构升级）**：
  - 在 `src/` 中实现 `CompactIndex` 核心数据结构与单元测试；
  - 实现基于内存切片的 `Search()` 与回溯路径还原；
  - 编写 Benchmark 对比内存占用与检索耗时。
- **阶段三（持久化与全量替换）**：
  - 实现紧凑二进制序列化器（`FLIX` 格式）；
  - 将 `src/indexer.go` 无缝切换至 `CompactIndex` 引擎；
  - 跑通现有全量 E2E 测试与单元测试。
