# FileList 紧凑索引方案 Review

> 文档编号：REVIEW-COMPACT-INDEX-20261008
> 评审时间：2026-10-08
> 评审对象：`docs/DESIGN-COMPACT-INDEX-20261008.md`
> 结论：方向正确，值得做；但与当前代码有脱节，且在增量索引用例定位、内存预算、GC 零分配、持久化格式等关键点缺少落地细节，不建议直接按文档开工。

## 1. 文档与代码现状不符，需要先更新状态

`src/config.go` 里阶段一已经实现，当前工作区未提交：

- `BuiltinExcludeDirs` 已存在于 `src/config.go:97-124`
- `builtinExcludes` 开关、`BuiltinExcludesEnabled()`、`mergeUniqueDirs()` 已存在于 `src/config.go:138-140、301-329、403-407`
- `config.sample.yaml:60-71` 已有说明
- `docs/CHANGES-20261008.md` 也已记录为完成

而方案文档还把阶段一写成“必须立刻落地”。状态应改为 Phase 1 Done，并重新实测 245 万这个数字（它应是排除前的数据）。

## 2. 关键缺口：增量索引缺少“文件级定位”方案

现状增量依赖 `map[string]Entry]` 按虚拟路径定位旧条目：`src/indexer.go:412、448`。

方案只给了目录的 `folderMap`，没有文件路径到文件的定位。`CompactFile` 本身不含路径，无法直接按虚拟路径查旧文件。若不补这个映射，`old, existed := idx.entries[vpath]` 这一步失去等价物，增量 diff 会把所有文件都当 Added，索引语义会失效。

可选方案：

- `fileMap map[string]uint32`：实现最简单，但 key 仍是完整路径，内存又会回到数百 MB，违背目标；
- 推荐：`Files` 按 `FolderID` 分组/排序，配合 `folderMap`（路径→FolderID）后在父目录内二分查找 basename。目录数少，文件不再重复存路径；
- 同时 `folderMap` 的 key 是目录虚拟路径，350k 个 key 需要重新计入预算，不能只算 `16B+4B+Bucket`。

## 3. 搜索实现零分配这一点不成立

方案写法：

```go
name := idx.Arena.Get(f.NameOff, f.NameLen)
if asciiContainsFold(name, query) {
```

而 `StringArena.Get` 是 `string(a.buf[off:off+len])`，会每次拷贝分配。对 245 万文件扫描，会重新生成海量 string 对象，GC 不会降到个位数。

热路径必须对 `Arena.buf` 直接比较，只对命中项（最多 limit/500）做 `string()` 转换。另方案只说 `asciiContainsFold`，建议明确：ASCII 折叠只作用于 `<0x80` 字节，非 ASCII 原样比较；不能直接 `c|0x20`，否则 UTF-8 continuation byte 可能误匹配。

## 4. 路径还原的虚拟路径与真实路径混用未定义

现状 `Entry.Path` 是虚拟路径，如 `/files/sub/a.txt`；`Entry.Name` 是真实名。根映射可能把真实目录名变成不同虚拟段，例如 `/files → ./`。

方案的 `BuildFullPath` 用目录名回溯，却没有说明 arena 里存的是 virtual segment 还是 real name。若存 real name，根路径和映射根会错；若存 virtual segment，则文件真实名/虚拟名要区分。建议：目录段存 virtual name，文件段存真实 name；根段用 root URL 覆盖。

## 5. 内存与磁盘数字不一致

方案目标是把内存降到 100~200MB，但表格里：

- `Files` 68.8MB + `Folders` 11.2MB + `Arena` 37.5MB ≈ 117.5MB；
- `folderMap` 若 key 是虚拟路径，还要加 key 字符串与桶，实际大概 150~170MB，不是 135MB；
- 持久化直接写连续切片，未压缩应是 ~118MB，不可能是 `3.4` 说的 `15~25MB`。要到 15~25MB，需要 Snappy/ZSTD 实测支撑，并在表中分“未压缩/压缩后”两档。

另外 Go 结构体内存布局有 padding：`CompactFile` 是 32B，不是 26B。持久化若直接写底层字节，会把 padding 一起写进去，且跨平台布局不可控。建议文件格式用 `encoding/binary` 显式 little-endian 编码，或用 FlatBuffers/Cap'n Proto；内存布局仍是 32B，但磁盘格式稳定。

## 6. GC 结论要修正

`CompactIndex` 的 `Files/Folders/Arena` 是 noscan 的，这点没错。但 `folderMap map[string]uint32` 仍有约 35 万个 key 字符串和 entry，会回到 GC 扫描范围。所以“堆对象降至个位数”不成立；应改为“百万级条目的字符串/struct 对象降至 O(目录数)”。

## 7. 增量合并的生命周期没说清

现有 `files` 若直接删除会移动切片元素，开销大；若用 tombstone，Arena 名字字节不会回收，`Files` 容量膨胀。建议明确二选一：

- 实用方案：`CompactIndex` snapshot 不可变，merge 生成新切片，原子替换指针，旧 snapshot 在无读者后由 GC 回收。后台每 5 分钟全量/增量重建一次，内存瞬时约 2×，仍远优于 1.78GB；
- Arena 字节不逐条回收，等 `fullInterval` 全量重建时整体重置。

方案现在只定义了结构体，没有定义替换、tombstone、arena 压缩点。

## 8. 其他需要补的细节

- `ModTime` 建议存 `UnixNano` 或 `UnixMilli`，不要用秒级。Windows 批量修改常在同一秒内发生，秒级会漏检；
- `NameLen uint16` 对单段名够用，但 `NameOff uint32` 要声明总 arena 上限 4GB；
- `folderMap` key 用虚拟路径，启动 load 后要重建；
- `persist` 头应记录 roots 签名（url+path）和 `persistVersion`，配置变化则丢弃重建；
- 文件格式加 magic/version/count/checksum，避免截断文件被误读；
- `Search` 的评分/MatchType（name/path/100/80/60/40）需要保持与现状一致，并补 golden test。

## 9. 是否还有更优方案

对这个项目，不建议换 SQLite/mmap/Bleve。它们能降低内存，但查询语义、分页命中路径还原、启动加载和 E2E 成本都会上升。

更优的折中是：Keep Everything-style CompactIndex，但把它做成不可变 snapshot + 目录分组索引 + 显式 LE 磁盘格式 + zstd + fullInterval 重置 arena。这能达到方案目标，同时保留现有 incremental diff、搜索评分和 ListDir 实时读取语义。

方案需要修改后再评审；阶段一应标记完成，`folderMap` 预算、文件级增量定位、零分配搜索、虚拟/真实路径边界、持久化格式与压缩数字这五项最优先补齐。
