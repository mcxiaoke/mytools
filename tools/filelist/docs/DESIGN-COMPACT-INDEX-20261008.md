# FileList 紧凑索引架构设计方案（Compact Indexer v2）

> 文档编号：DESIGN-COMPACT-INDEX-20261008  
> 修订版本：v3 (全量替换主引擎并上线)  
> 最新更新：2026-10-08 14:50:00 (GMT+8)  
> 状态：阶段一与阶段二已全部实施完毕 (Phase 1 & Phase 2 Completed & Replaced)  
> 核心目标：将百万级文件索引的常驻内存从 GB 级降至 150MB 左右（对标 Everything 内存利用率），彻底消除启动与保存时的 GOB 内存尖峰。

---

## 1. 背景与现状

### 1.1 现状与实测瓶颈
在用户实际机器上，FileList 挂载了 `C:\Home` 与 `F:\Temp` 两个大目录。在启用内置排除名单前：
- **索引条目总数**：**2,454,659**（包含约 35 万个目录和 210 万个文件）。
- **运行内存**：
  - **峰值工作集（PeakWorkingSet）**：**5.09 GB**；
  - **常驻工作集（WorkingSet）**：**1.78 GB**；
- **旧版磁盘持久化文件**：`data/filelist.idx` 高达 **637 MB**（GOB 格式）。

### 1.2 阶段一现状（已合入，Commit 2c47470）
- `src/config.go` 中已内置 40+ 常见开发环境目录排除清单（`BuiltinExcludeDirs`，涵盖 `msys64`、`Scoop`、`flutter`、`android-sdk`、`venv`、`target`、`.gradle` 等）；
- `builtinExcludes` 开关默认开启，并与用户自定义 `excludeDirs` 采用大小写不敏感并集合并；
- 排除生效后，日常环境索引条目数从 245 万骤降至 10~20 万，常驻内存回落至几十兆。

### 1.3 阶段二实施落地（主引擎全面替换）
- 彻底移除旧版 `map[string]Entry` 引擎与 GOB 序列化，全量启用 `CompactIndex` 架构；
- 索引文件扩展名统一变更为 `.db`（默认存放于 `dataDir/filelist.db`），避免原 `.idx` 在 Windows / 播放器环境中被误识别为字幕文件；
- 移除配置文件中冗余的 `index.persist` 项，统一由 `dataDir` 派生路径；
- 默认采用 Raw 模式持久化（保存仅需 140ms，加载 106ms，单文件约 122MB），实测搜索提速 5.4 倍，常驻内存下降 90.7%。

---

## 2. 核心架构设计

### 2.1 整体拓扑与数据模型

借鉴 Everything 的“文件与目录分离存储 + 父目录索引 + 连续字符串池”：

```
CompactIndex 内存拓扑示意图：

+---------------------------------------------------------------------------------+
| Folders Table (紧凑切片, 350k entries)                                           |
| [Folder 0] -> ParentID: 0xFFFFFFFF, NameOff, NameLen, FileStart: 0, FileCount: 3|
| [Folder 1] -> ParentID: 0,          NameOff, NameLen, FileStart: 3, FileCount: 2|
+---------------------------------------------------------------------------------+
| Files Table (连续紧凑切片, 2.1M entries, 无指针, noscan, 按 FolderID+Name 局部有序) |
| [File 0]   -> FolderID: 0, NameOff, NameLen, ModTimeNano, Size                  |
| [File 1]   -> FolderID: 0, NameOff, NameLen, ModTimeNano, Size                  |
| [File 2]   -> FolderID: 0, NameOff, NameLen, ModTimeNano, Size                  |
| [File 3]   -> FolderID: 1, NameOff, NameLen, ModTimeNano, Size                  |
+---------------------------------------------------------------------------------+
| String Arena (连续字节流池)                                                      |
| "chome" + "Develop" + "main.go" + "config.yaml" ...                             |
+---------------------------------------------------------------------------------+
| folderMap (辅助哈希, 仅映射目录)                                                 |
| "/chome" -> 0, "/chome/Develop" -> 1 ...                                        |
+---------------------------------------------------------------------------------+
```

#### 数据结构定义

```go
package indexer

// CompactFolder 目录条目（数量通常仅占总条目的 10%~15%）
type CompactFolder struct {
	ParentID  uint32 // 父目录在 Folders 切片中的索引（根目录为 0xFFFFFFFF）
	NameOff   uint32 // 目录名在 StringArena 中的起始偏移（上限 4GB）
	NameLen   uint16 // 目录名长度
	FileStart uint32 // 该目录拥有的直接子文件在 Files 切片中的起始偏移
	FileCount uint32 // 该目录拥有的直接子文件数量（用于区间切片二分查找）
	ModTime   int64  // UnixNano 纳秒时间戳（8 字节，避免同一秒内批量变更漏检）
	Size      int64  // 目录汇总大小
} // 结构体大小: 36 字节 -> 内存对齐后 40 字节（无指针，noscan）

// CompactFile 文件条目（占总条目的 85%~90%）
type CompactFile struct {
	FolderID uint32 // 所属父目录在 Folders 切片中的索引（4 字节）
	NameOff  uint32 // 文件名在 StringArena 中的起始偏移（4 字节）
	NameLen  uint16 // 文件名长度（2 字节）
	_        uint16 // 显式对齐填充（2 字节）
	Size     int64  // 文件大小（8 字节）
	ModTime  int64  // UnixNano 纳秒时间戳（8 字节）
} // 结构体大小: 28 字节 -> 显式对齐 32 字节（无指针，noscan）

// StringArena 连续字符串池
type StringArena struct {
	buf []byte
}

func (a *StringArena) Append(b []byte) (uint32, uint16) {
	off := uint32(len(a.buf))
	a.buf = append(a.buf, b...)
	return off, uint16(len(b))
}

func (a *StringArena) Bytes(off uint32, len uint16) []byte {
	return a.buf[off : off+uint32(len)]
}

// CompactIndex 紧凑索引容器
type CompactIndex struct {
	Folders   []CompactFolder
	Files     []CompactFile
	Arena     StringArena
	folderMap map[string]uint32 // 仅存储目录: virtual path -> FolderID
}
```

---

## 3. 关键算法与落地细节突破

### 3.1 树遍历构建器与增量扫描加速（实际落地实现）
- **痛点**：若维持现有的 `map[string]Entry` 来按文件路径定位旧条目，200 万个文件全路径 string 又会导致内存暴涨回几百 MB；同时增量扫描需要支持子树跳过。
- **解法（DFS 树遍历 + 子树极速复制 + 局部二分比对）**：
  1. `compactBuilder` 采用深度优先遍历（DFS），按根目录顺序与虚拟路径逐级递归构建；
  2. **增量子树跳过（`copySubtree`）**：
     - 若目录的 `(mtime, size)` 未变且层级 `depth >= rescanDepth`，通过 `copySubtree` 递归复制旧快照中的该目录、所有直接子文件及所有后代子目录与子文件；
     - 极速跳过底层 `os.ReadDir` 物理磁盘 I/O，同时准确维持全量拓扑；
  3. **文件级精准 Diff**：
     - 当目录发生变动需重新扫描时，利用旧快照对应目录中的子文件二分列表，精准检测直接子文件的新增（`Added`）、变更（`Changed`）与删除（`Removed`）；
     - 同一目录下的直接子文件填充完毕后执行 `sort.Slice` 局部排序，确保增量二分查找的高效与严格有序；
  4. **根目录 Anchor 拓扑**：
     - 虚拟根节点（`ParentID == 0xFFFFFFFF`）作为虚拟路径挂载点，在 `TotalEntries()` 与 `AllEntries()` 中被明确标记，保持统计与对外行为的严谨一致。

### 3.2 搜索热路径的“真·零堆分配”
- **解法**：
  1. 搜索热路径直接在 `Arena.buf` 字节流上执行无分配匹配：
     ```go
     func asciiContainsFoldBytes(target []byte, queryLower []byte) bool
     ```
  2. **严格遵守 UTF-8 编码安全**：
     - 大小写折叠仅对 ASCII `< 0x80` 范围的 `'A'-'Z'` 映射到 `'a'-'z'`；
     - 对 `>= 0x80` 的多字节 UTF-8 字符（中文、日文、特殊符号等）严格按原字节序列逐字节比对，防止 continuation bytes 发生位运算误匹配。
  3. **堆分配延迟到截断后**：
     整个 200 万文件的比对过程中 **0 次堆分配**。仅对最终进入 top-limit（如前 50~100 个结果）的命中项，才调用 `BuildFullPath` 还原字符串并返回给 API。

### 3.3 路径还原的虚拟/真实路径边界
- **规范定义**：
  1. `Folders[0..N]` 中的顶层根目录（`ParentID == 0xFFFFFFFF`）对应 `cfg.Roots` 中的各个根条目，其名称存储为该根的 Virtual Root URL（如 `/chome`）；
  2. 所有下级子目录在 Arena 中存储其虚拟 URL 段名（Virtual Segment Name）；
  3. 所有文件在 Arena 中存储其真实文件名（Base Name）；
  4. `BuildFullPath(f)` 向上回溯到根目录时停止，直接拼接出合法标准的虚拟路径，格式如 `/chome/Music/热门/song.mp3`。与现有前端 API 完美兼容。

### 3.4 零锁并发读与生命周期模型
- **并发与更新模型**：
  1. 服务对外检索（`Search`）持有不可变快照指针 `atomic.Pointer[CompactIndex]`，实现**绝对零锁读并发**；
  2. 后台全量/增量构建在后台独立 Goroutine 中完成全新的 `CompactIndex` 生成；
  3. 扫描结束并生成最新索引后，执行原子指针切换（`idx.compact.Store(newCompact)`）；
  4. 读请求与写构建完全解耦，内存无任何碎块或需要原地整理的结构；旧实例在活跃读者读完后由 Go GC 极速回收（纯连续 noscan 内存，GC 耗时 < 1ms）。

---

## 4. 磁盘持久化格式规范（FLIX v3 二进制）

彻底放弃 GOB 反射编码，采用显式 Little-Endian 二进制紧凑格式：

```
+---------------------------------------------------------------------------------+
| Magic: "FLIX" (4B) | Version: 3 (2B) | RootsHash: uint64 (8B) | Checksum: (4B)    |
+---------------------------------------------------------------------------------+
| FolderCount: uint32 (4B) | FileCount: uint32 (4B) | ArenaLen: uint32 (4B)       |
+---------------------------------------------------------------------------------+
| Folders Block (FolderCount * 40 字节紧凑编码)                                     |
+---------------------------------------------------------------------------------+
| Files Block (FileCount * 32 字节紧凑编码)                                         |
+---------------------------------------------------------------------------------+
| Arena Block (原始字节流)                                                         |
+---------------------------------------------------------------------------------+
```

- **文件扩展名与路径**：
  - 扩展名变更为 `.db`（统一存放在 `filepath.Join(dataDir, "filelist.db")`）；
  - 避免原 `.idx` 在 Windows/媒体播放器中被误识别为字幕文件；
  - 移除配置文件中冗余的 `index.persist` 字段，统一由 `dataDir` 派生。
- **持久化模式（默认 Raw 模式）**：
  - 默认采用 Raw 模式，省去 gzip/zstd 压缩与解压的 CPU 消耗；
  - 借助 `bufio.Reader` / `bufio.Writer` 缓冲 I/O，245 万条目序列化写入仅 **140ms**，启动反序列化仅 **106ms**；
  - 文件大小约 **122 MB**（比原 GOB 637 MB 减少了 80.8%）。
- **RootsHash 校验**：将配置中的 `Roots[i].URL + Roots[i].Path` 计算 FNV-1a 哈希写入文件头。启动时若检测到 Roots 配置变更，自动使旧缓存失效并触发全量重建。

---

## 5. 内存与性能实测收益对比（真实 2,454,659 条目）

在包含 2,106,757 文件 + 347,902 目录的真实生产环境全量索引下实测对比：

| 指标 | 旧引擎 (Legacy Map + GOB) | 新引擎 (CompactIndex + Raw FLIX) | 优化幅度 |
| :--- | :--- | :--- | :--- |
| **磁盘存储体积** | **637 MB** (`filelist.idx`) | **122 MB** (`filelist.db`) | **-80.8%** |
| **保存序列化耗时** | 3,622 ms | **140 ms** | **25.8 倍加速** |
| **加载反序列化耗时** | 2,538 ms | **106 ms** | **23.9 倍加速** |
| **常驻活堆内存 (HeapAlloc)** | 1,349 MB | **125 MB** | **-90.7% (仅 1/10)** |
| **GC 扫描对象数** | ~12,000,000+ 个 | **~350,000 个** | **-97.1%** |
| **前缀搜索耗时 (`chome`)** | 41.5 ms | **12.8 ms** | **3.2 倍加速** |
| **后缀搜索耗时 (`.png`)** | 68.0 ms | **13.6 ms** | **5.0 倍加速** |
| **路径组合搜索 (`temp/test`)** | 72.0 ms | **13.3 ms** | **5.4 倍加速** |
| **搜索热路径堆分配** | 每次查询数十万次分配 | **0 堆分配** (仅 TopN 延迟分配) | **无 GC 抖动** |

---

## 6. 实施与上线结论

1. **原型开发与验证阶段**：
   - 编写 `src/indexer_compact.go` 与 `src/indexer_compact_test.go`，完成了数据模型、树遍历构建器、子树复制、FLIX 编解码、搜索过滤等完整功能；
   - 通过 245 万真实数据严格比对测试，验证了功能正确性与极致性能。
2. **全量替换与上线**：
   - 彻底移除了 `src/indexer.go` 中的旧版 `map[string]Entry`、`dirStamp`、`gen` 以及 GOB 序列化逻辑；
   - 将 `Indexer` 底层全面切换为 `CompactIndex`，对外提供 `atomic.Pointer` 零锁并发读；
   - 统一索引持久化文件为 `dataDir/filelist.db`，清理了配置冗余项；
   - 所有单元测试、回归测试均顺利通过。

