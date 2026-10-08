package main

import (
	"encoding/gob"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"testing"
	"time"
)

func TestCompactIndex_BuildSearchAndPaths(t *testing.T) {
	idx := NewCompactIndex()

	// Add root folder: /chome
	rootOff, rootLen := idx.Arena.Append("/chome")
	idx.Folders = append(idx.Folders, CompactFolder{
		ParentID: 0xFFFFFFFF,
		NameOff:  rootOff,
		NameLen:  rootLen,
		ModTime:  time.Now().UnixNano(),
	})
	idx.folderMap["/chome"] = 0

	// Add child folder: /chome/Docs
	docsOff, docsLen := idx.Arena.Append("Docs")
	idx.Folders = append(idx.Folders, CompactFolder{
		ParentID:  0,
		NameOff:   docsOff,
		NameLen:   docsLen,
		FileStart: 0,
		FileCount: 2,
		ModTime:   time.Now().UnixNano(),
	})
	idx.folderMap["/chome/Docs"] = 1

	// Add files under /chome/Docs
	f1Off, f1Len := idx.Arena.Append("report.pdf")
	idx.Files = append(idx.Files, CompactFile{
		FolderID: 1,
		NameOff:  f1Off,
		NameLen:  f1Len,
		Size:     1024,
		ModTime:  time.Now().UnixNano(),
	})

	f2Off, f2Len := idx.Arena.Append("summary.txt")
	idx.Files = append(idx.Files, CompactFile{
		FolderID: 1,
		NameOff:  f2Off,
		NameLen:  f2Len,
		Size:     2048,
		ModTime:  time.Now().UnixNano(),
	})

	// Verify path reconstruction
	if path := idx.BuildFolderPath(0); path != "/chome" {
		t.Fatalf("expected /chome, got %s", path)
	}
	if path := idx.BuildFolderPath(1); path != "/chome/Docs" {
		t.Fatalf("expected /chome/Docs, got %s", path)
	}
	if path := idx.BuildFilePath(0); path != "/chome/Docs/report.pdf" {
		t.Fatalf("expected /chome/Docs/report.pdf, got %s", path)
	}
	if path := idx.BuildFilePath(1); path != "/chome/Docs/summary.txt" {
		t.Fatalf("expected /chome/Docs/summary.txt, got %s", path)
	}

	// Verify FindFile (binary search)
	f, found := idx.FindFile(1, "summary.txt")
	if !found || f == nil {
		t.Fatalf("expected to find summary.txt")
	}
	if f.Size != 2048 {
		t.Fatalf("expected size 2048, got %d", f.Size)
	}

	_, notFound := idx.FindFile(1, "missing.doc")
	if notFound {
		t.Fatalf("expected missing.doc to not be found")
	}

	// Verify Search
	results := idx.Search("report", 10)
	if len(results) != 1 {
		t.Fatalf("expected 1 search result, got %d", len(results))
	}
	if results[0].Name != "report.pdf" || results[0].Path != "/chome/Docs/report.pdf" {
		t.Fatalf("unexpected search result: %+v", results[0])
	}
	if results[0].MatchType != "name" {
		t.Fatalf("expected matchType name, got %s", results[0].MatchType)
	}

	// Verify case-insensitivity
	caseResults := idx.Search("REPORT", 10)
	if len(caseResults) != 1 || caseResults[0].Name != "report.pdf" {
		t.Fatalf("case-insensitive search failed, got %+v", caseResults)
	}
}

func TestCompactIndex_PersistenceRoundtrip(t *testing.T) {
	dir := t.TempDir()
	savePath := filepath.Join(dir, "test.flix")

	roots := []RootMapping{{URL: "/v", Path: "C:/virtual"}}

	// Build original index
	entries := map[string]Entry{
		"/v/file1.txt": {Name: "file1.txt", Path: "/v/file1.txt", Size: 100, ModTime: time.Now(), IsDir: false},
		"/v/sub":       {Name: "sub", Path: "/v/sub", Size: 0, ModTime: time.Now(), IsDir: true},
		"/v/sub/b.txt": {Name: "b.txt", Path: "/v/sub/b.txt", Size: 200, ModTime: time.Now(), IsDir: false},
	}
	original := BuildCompactIndexFromEntries(entries, roots)

	// Save uncompressed
	if err := original.Save(savePath, roots, false); err != nil {
		t.Fatalf("save failed: %v", err)
	}

	// Load
	loaded, err := LoadCompactIndex(savePath, roots)
	if err != nil {
		t.Fatalf("load failed: %v", err)
	}

	if len(loaded.Folders) != len(original.Folders) {
		t.Fatalf("folder count mismatch: %d vs %d", len(loaded.Folders), len(original.Folders))
	}
	if len(loaded.Files) != len(original.Files) {
		t.Fatalf("file count mismatch: %d vs %d", len(loaded.Files), len(original.Files))
	}

	// Verify search works identically on loaded index
	res := loaded.Search("b.txt", 10)
	if len(res) != 1 || res[0].Path != "/v/sub/b.txt" {
		t.Fatalf("search on loaded index failed: %+v", res)
	}

	// Save compressed
	saveGzPath := filepath.Join(dir, "test.flix.gz")
	if err := original.Save(saveGzPath, roots, true); err != nil {
		t.Fatalf("save compressed failed: %v", err)
	}

	loadedGz, err := LoadCompactIndex(saveGzPath, roots)
	if err != nil {
		t.Fatalf("load compressed failed: %v", err)
	}
	resGz := loadedGz.Search("file1", 10)
	if len(resGz) != 1 || resGz[0].Path != "/v/file1.txt" {
		t.Fatalf("search on compressed loaded index failed: %+v", resGz)
	}
}

func generateSyntheticEntries(n int) (map[string]Entry, []RootMapping) {
	roots := []RootMapping{{URL: "/data", Path: "C:/data"}}
	entries := make(map[string]Entry, n)

	entries["/data"] = Entry{Name: "data", Path: "/data", IsDir: true}

	exts := []string{".go", ".txt", ".md", ".json", ".yaml", ".png", ".pdf"}
	for i := 0; i < n; i++ {
		dirIdx := i / 50
		dirPath := fmt.Sprintf("/data/dir_%03d", dirIdx)
		if _, ok := entries[dirPath]; !ok {
			entries[dirPath] = Entry{
				Name:    fmt.Sprintf("dir_%03d", dirIdx),
				Path:    dirPath,
				IsDir:   true,
				ModTime: time.Now(),
				lname:   strings.ToLower(fmt.Sprintf("dir_%03d", dirIdx)),
				lpath:   strings.ToLower(dirPath),
			}
		}

		ext := exts[i%len(exts)]
		name := fmt.Sprintf("sample_file_%05d%s", i, ext)
		fpath := fmt.Sprintf("%s/%s", dirPath, name)
		entries[fpath] = Entry{
			Name:    name,
			Path:    fpath,
			Size:    int64(i * 100),
			ModTime: time.Now(),
			IsDir:   false,
			lname:   strings.ToLower(name),
			lpath:   strings.ToLower(fpath),
		}
	}
	return entries, roots
}

func legacyMapSearch(entries map[string]Entry, query string, limit int) []Entry {
	query = strings.ToLower(strings.TrimSpace(query))
	if query == "" {
		return nil
	}
	type scored struct {
		entry Entry
		score int
	}
	var results []scored
	for _, e := range entries {
		var matchType string
		var score int
		switch {
		case e.lname == query:
			matchType, score = "name", 100
		case strings.HasPrefix(e.lname, query):
			matchType, score = "name", 80
		case strings.Contains(e.lname, query):
			matchType, score = "name", 60
		case e.IsDir && strings.Contains(e.lpath, query):
			matchType, score = "path", 40
		default:
			continue
		}
		e.MatchType = matchType
		results = append(results, scored{e, score})
	}
	sort.Slice(results, func(i, j int) bool {
		if results[i].score != results[j].score {
			return results[i].score > results[j].score
		}
		return results[i].entry.Path < results[j].entry.Path
	})
	if limit > 0 && len(results) > limit {
		results = results[:limit]
	}
	out := make([]Entry, len(results))
	for i, r := range results {
		out[i] = r.entry
	}
	return out
}

func TestCompactIndex_SearchEquivalenceWithOldIndexer(t *testing.T) {
	entries, roots := generateSyntheticEntries(2000)

	compactIdx := BuildCompactIndexFromEntries(entries, roots)

	queries := []string{"sample", "SAMPLE_FILE_001", "file_00050.go", "dir_010", "json", "none_existing"}

	for _, q := range queries {
		oldRes := legacyMapSearch(entries, q, 20)
		compactRes := compactIdx.Search(q, 20)

		if len(oldRes) != len(compactRes) {
			t.Fatalf("query %q: result count mismatch: old=%d, compact=%d", q, len(oldRes), len(compactRes))
		}

		for i := range oldRes {
			if oldRes[i].Path != compactRes[i].Path {
				t.Errorf("query %q item %d: path mismatch: old=%s, compact=%s", q, i, oldRes[i].Path, compactRes[i].Path)
			}
			if oldRes[i].MatchType != compactRes[i].MatchType {
				t.Errorf("query %q item %d: matchType mismatch: old=%s, compact=%s", q, i, oldRes[i].MatchType, compactRes[i].MatchType)
			}
		}
	}
}

func BenchmarkSearch_OldIndexer(b *testing.B) {
	entries, _ := generateSyntheticEntries(10000)

	b.ResetTimer()
	b.ReportAllocs()
	for i := 0; i < b.N; i++ {
		_ = legacyMapSearch(entries, "sample_file_005", 20)
	}
}

func BenchmarkSearch_CompactIndex(b *testing.B) {
	entries, roots := generateSyntheticEntries(10000)
	compactIdx := BuildCompactIndexFromEntries(entries, roots)

	b.ResetTimer()
	b.ReportAllocs()
	for i := 0; i < b.N; i++ {
		_ = compactIdx.Search("sample_file_005", 20)
	}
}

func getMemMB() (alloc, inuse, sys uint64) {
	var m runtime.MemStats
	runtime.ReadMemStats(&m)
	return m.Alloc / 1024 / 1024, m.HeapInuse / 1024 / 1024, m.Sys / 1024 / 1024
}

func TestBenchmarkRealComparison(t *testing.T) {
	idxPath := `C:\Home\Tools\filelist\data\filelist.idx`
	fi, err := os.Stat(idxPath)
	if err != nil {
		t.Skip("真实索引文件不存在，跳过全量真实测试")
	}

	fmt.Println("\n================================================================")
	fmt.Println("FileList 索引架构对比基准测试 (Old GOB Indexer vs New FLIX CompactIndex)")
	fmt.Println("================================================================")

	oldFileSizeMB := float64(fi.Size()) / 1024 / 1024
	fmt.Printf("[1/5] 读取实际索引文件: %s (%.2f MB)\n", idxPath, oldFileSizeMB)

	start := time.Now()
	f, err := os.Open(idxPath)
	if err != nil {
		t.Fatalf("打开失败: %v", err)
	}
	type stamp struct {
		Mod  time.Time
		Size int64
		Gen  uint64
	}
	type persistData struct {
		Version int
		Entries map[string]Entry
		Dirs    map[string]stamp
	}
	var data persistData
	if err := gob.NewDecoder(f).Decode(&data); err != nil {
		f.Close()
		t.Fatalf("解码失败: %v", err)
	}
	f.Close()
	oldLoadTime := time.Since(start)

	totalEntries := len(data.Entries)
	fmt.Printf("      旧版 GOB 加载完成: %d 条目，耗时: %v\n", totalEntries, oldLoadTime)

	// GC to steady state
	runtime.GC()
	runtime.GC()
	oldAlloc, oldInuse, oldSys := getMemMB()
	fmt.Printf("      旧版常驻内存: HeapAlloc=%d MB, HeapInuse=%d MB, Sys=%d MB\n\n", oldAlloc, oldInuse, oldSys)

	// 2. Convert to Compact Index
	fmt.Println("[2/5] 转换为紧凑索引 (CompactIndex) 并测量内存...")
	roots := []RootMapping{
		{URL: "/chome", Path: "C:/Home"},
		{URL: "/ftemp", Path: "F:/Temp"},
	}

	start = time.Now()
	compactIdx := BuildCompactIndexFromEntries(data.Entries, roots)
	convertTime := time.Since(start)
	fmt.Printf("      转换完成: Folders=%d, Files=%d, Arena=%.2f MB, 耗时: %v\n",
		len(compactIdx.Folders), len(compactIdx.Files), float64(len(compactIdx.Arena.buf))/1024/1024, convertTime)

	// Release old entries
	data = persistData{}
	runtime.GC()
	runtime.GC()
	newAlloc, newInuse, newSys := getMemMB()
	fmt.Printf("      新版常驻内存: HeapAlloc=%d MB, HeapInuse=%d MB, Sys=%d MB\n", newAlloc, newInuse, newSys)
	fmt.Printf("      ★ 内存节省: 从 %d MB 降至 %d MB (降低 %.1f%%)\n\n",
		oldAlloc, newAlloc, float64(oldAlloc-newAlloc)/float64(oldAlloc)*100)

	// 3. Test Persistence (Save and Load)
	fmt.Println("[3/5] 测试新版磁盘持久化 (FLIX 格式)...")
	tempDir := t.TempDir()

	// Raw FLIX
	rawFlixPath := filepath.Join(tempDir, "compact.flix")
	start = time.Now()
	if err := compactIdx.Save(rawFlixPath, roots, false); err != nil {
		t.Fatalf("Save raw failed: %v", err)
	}
	rawSaveTime := time.Since(start)
	rawFi, _ := os.Stat(rawFlixPath)
	rawSizeMB := float64(rawFi.Size()) / 1024 / 1024

	start = time.Now()
	if _, err := LoadCompactIndex(rawFlixPath, roots); err != nil {
		t.Fatalf("Load raw failed: %v", err)
	}
	rawLoadTime := time.Since(start)
	fmt.Printf("      FLIX (未压缩): 大小=%.2f MB (旧版 %.2f MB, 缩减 %.1f%%) | 保存耗时=%v | 加载耗时=%v\n",
		rawSizeMB, oldFileSizeMB, (1.0-rawSizeMB/oldFileSizeMB)*100, rawSaveTime, rawLoadTime)

	// Gzip Compressed FLIX
	gzFlixPath := filepath.Join(tempDir, "compact.flix.gz")
	start = time.Now()
	if err := compactIdx.Save(gzFlixPath, roots, true); err != nil {
		t.Fatalf("Save gz failed: %v", err)
	}
	gzSaveTime := time.Since(start)
	gzFi, _ := os.Stat(gzFlixPath)
	gzSizeMB := float64(gzFi.Size()) / 1024 / 1024

	start = time.Now()
	if _, err := LoadCompactIndex(gzFlixPath, roots); err != nil {
		t.Fatalf("Load gz failed: %v", err)
	}
	gzLoadTime := time.Since(start)
	fmt.Printf("      FLIX (Gzip压缩): 大小=%.2f MB (旧版 %.2f MB, 缩减 %.1f%%) | 保存耗时=%v | 加载耗时=%v\n\n",
		gzSizeMB, oldFileSizeMB, (1.0-gzSizeMB/oldFileSizeMB)*100, gzSaveTime, gzLoadTime)

	// 4. Search Latency Benchmark on 2.45 Million Entries
	fmt.Printf("[4/5] 测试在 %d 万真实条目上的检索性能 (Search)...\n", totalEntries/10000)
	queries := []string{"main.go", "config.yaml", "README", "test", "flutter", "apk"}

	for _, q := range queries {
		start = time.Now()
		res := compactIdx.Search(q, 50)
		lat := time.Since(start)
		fmt.Printf("      查询 %-15q -> 命中 %3d 条结果, 耗时: %v\n", q, len(res), lat)
	}

	fmt.Println("\n================================================================")
	fmt.Println("测试完成！")
	fmt.Println("================================================================")
}
