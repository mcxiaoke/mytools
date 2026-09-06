package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestMapVirtualToReal_ExactMatch(t *testing.T) {
	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: "/mnt/data"},
			{URL: "/downloads", Path: "/home/user/Downloads"},
		},
	}
	idx := &Indexer{cfg: cfg}

	tests := []struct {
		vpath  string
		want   string
		wantOK bool
	}{
		{"/data", "/mnt/data", true},
		{"/downloads", "/home/user/Downloads", true},
		{"/nonexistent", "", false},
		{"/", "", false},
		{"", "", false},
	}

	for _, tt := range tests {
		got, ok := idx.MapVirtualToReal(tt.vpath)
		if ok != tt.wantOK {
			t.Errorf("MapVirtualToReal(%q): ok=%v, want %v", tt.vpath, ok, tt.wantOK)
		}
		if tt.wantOK && filepath.ToSlash(got) != tt.want {
			t.Errorf("MapVirtualToReal(%q): got %q, want %q", tt.vpath, got, tt.want)
		}
	}
}

func TestMapVirtualToReal_Subpath(t *testing.T) {
	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: "/mnt/data"},
		},
	}
	idx := &Indexer{cfg: cfg}

	tests := []struct {
		vpath string
		want  string
	}{
		{"/data/sub", "/mnt/data/sub"},
		{"/data/deep/nested/dir", "/mnt/data/deep/nested/dir"},
		{"/data/file.txt", "/mnt/data/file.txt"},
	}

	for _, tt := range tests {
		got, ok := idx.MapVirtualToReal(tt.vpath)
		if !ok {
			t.Errorf("MapVirtualToReal(%q): unexpectedly not found", tt.vpath)
		}
		if filepath.ToSlash(got) != tt.want {
			t.Errorf("MapVirtualToReal(%q): got %q, want %q", tt.vpath, got, tt.want)
		}
	}
}

func TestMapVirtualToReal_PathCleaning(t *testing.T) {
	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: "/mnt/data"},
		},
	}
	idx := &Indexer{cfg: cfg}

	// traversal attempt should be cleaned
	got, ok := idx.MapVirtualToReal("/data/../etc/passwd")
	if ok {
		t.Errorf("expected /data/../etc/passwd to not match (cleaned to /etc/passwd)")
	}
	if got != "" {
		t.Errorf("got %q, want empty", got)
	}

	// double slashes are cleaned
	got, ok = idx.MapVirtualToReal("/data//sub")
	if !ok {
		t.Errorf("expected /data//sub to match /data")
	}
	if filepath.ToSlash(got) != "/mnt/data/sub" {
		t.Errorf("got %q, want /mnt/data/sub", got)
	}
}

func TestMapVirtualToReal_LongestPrefixWins(t *testing.T) {
	// roots are sorted by URL length (longest first) during LoadConfig,
	// but here we manually set them out of order to test the sort.
	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: "/mnt/data"},
			{URL: "/data/archive", Path: "/archive"},
		},
	}
	// simulate the sort that LoadConfig does
	sortRootsByLength(cfg.Roots)
	idx := &Indexer{cfg: cfg}

	// /data/archive should match /data/archive (longer prefix), not /data
	got, ok := idx.MapVirtualToReal("/data/archive")
	if !ok {
		t.Fatal("expected match")
	}
	if filepath.ToSlash(got) != "/archive" {
		t.Errorf("got %q, want /archive", got)
	}

	got, ok = idx.MapVirtualToReal("/data/archive/old")
	if !ok {
		t.Fatal("expected match")
	}
	if filepath.ToSlash(got) != "/archive/old" {
		t.Errorf("got %q, want /archive/old", got)
	}

	// /data/other should still match /data
	got, ok = idx.MapVirtualToReal("/data/other")
	if !ok {
		t.Fatal("expected /data/other to match /data")
	}
	if filepath.ToSlash(got) != "/mnt/data/other" {
		t.Errorf("got %q, want /mnt/data/other", got)
	}
}

func TestMapVirtualToReal_NoRootSlash(t *testing.T) {
	// even if someone manually injects root "/", MapVirtualToReal should
	// NOT act as a catch-all — it should only match exact prefix.
	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/", Path: "/root"},
			{URL: "/data", Path: "/mnt/data"},
		},
	}
	sortRootsByLength(cfg.Roots)
	idx := &Indexer{cfg: cfg}

	// /data should match /data, not / (root)
	got, ok := idx.MapVirtualToReal("/data")
	if !ok {
		t.Fatal("expected /data to match")
	}
	if filepath.ToSlash(got) != "/mnt/data" {
		t.Errorf("got %q, want /mnt/data", got)
	}
}

// sortRootsByLength sorts roots by URL length (longest first).
// This mirrors the sort done in LoadConfig.
func sortRootsByLength(roots []RootMapping) {
	for i := 1; i < len(roots); i++ {
		for j := i; j > 0 && len(roots[j].URL) > len(roots[j-1].URL); j-- {
			roots[j], roots[j-1] = roots[j-1], roots[j]
		}
	}
}

// --- helpers for indexer behaviour tests ---

func testIndexer(t *testing.T, rootPath string, mutate func(cfg *Config)) *Indexer {
	t.Helper()
	cfg := &Config{Roots: []RootMapping{{URL: "/v", Path: rootPath}}}
	cfg.Index.ExcludeDirs = defaultExcludeDirs
	cfg.Index.ExcludeFiles = defaultExcludeFiles
	if mutate != nil {
		mutate(cfg)
	}
	return &Indexer{cfg: cfg, entries: map[string]Entry{}, dirStamp: map[string]stamp{}}
}

func writeFile(t *testing.T, p, content string) {
	t.Helper()
	if err := os.WriteFile(p, []byte(content), 0644); err != nil {
		t.Fatal(err)
	}
}

// settle lets directory metadata (mtime) catch up after filesystem writes.
// Windows buffers directory timestamp updates, so builds issued back-to-back
// in tests can observe a still-converging directory stamp.
func settle() { time.Sleep(30 * time.Millisecond) }

func TestBuildIndex_IncrementalNoChangeIsCheap(t *testing.T) {
	dir := t.TempDir()
	writeFile(t, filepath.Join(dir, "a.txt"), "a")
	if err := os.MkdirAll(filepath.Join(dir, "sub"), 0755); err != nil {
		t.Fatal(err)
	}
	writeFile(t, filepath.Join(dir, "sub", "b.txt"), "b")

	idx := testIndexer(t, dir, nil)

	settle()
	first := idx.BuildIndex() // full build (empty index)
	if first.Added != 3 {     // a.txt, sub, sub/b.txt
		t.Fatalf("first pass: Added=%d, want 3", first.Added)
	}
	if got := len(idx.entries); got != 3 {
		t.Fatalf("first pass: entries=%d, want 3", got)
	}

	settle()
	second := idx.BuildIndex() // nothing changed -> subtree skipped
	if second.Skipped == 0 {
		t.Error("second pass: expected the untouched root subtree to be skipped")
	}
	if second.Added != 0 || second.Changed != 0 || second.Removed != 0 {
		t.Errorf("second pass: unexpected diff: %+v", second)
	}
	if got := len(idx.entries); got != 3 {
		t.Fatalf("second pass: entries=%d, want 3", got)
	}
}

func TestBuildIndex_IncrementalAddRemove(t *testing.T) {
	dir := t.TempDir()
	writeFile(t, filepath.Join(dir, "a.txt"), "a")
	if err := os.MkdirAll(filepath.Join(dir, "sub"), 0755); err != nil {
		t.Fatal(err)
	}
	writeFile(t, filepath.Join(dir, "sub", "b.txt"), "b")

	idx := testIndexer(t, dir, nil)
	settle()
	idx.BuildIndex()

	// add a new file at the root: root dir mtime changes -> rescan.
	// The unchanged "sub" subtree should normally be skipped (that path is
	// covered by TestBuildIndex_IncrementalNoChangeIsCheap); here we only
	// assert correctness of the diff, since Windows may buffer a directory
	// stamp update across the two builds and rescan sub once.
	writeFile(t, filepath.Join(dir, "c.txt"), "c")
	settle()
	pass := idx.BuildIndex()
	if pass.Added != 1 {
		t.Errorf("add pass: Added=%d, want 1", pass.Added)
	}
	if got := len(idx.entries); got != 4 {
		t.Errorf("add pass: entries=%d, want 4", got)
	}
	if _, ok := idx.entries["/v/sub/b.txt"]; !ok {
		t.Error("add pass: existing entry /v/sub/b.txt was lost")
	}

	// remove a file
	if err := os.Remove(filepath.Join(dir, "a.txt")); err != nil {
		t.Fatal(err)
	}
	settle()
	pass = idx.BuildIndex()
	if pass.Removed != 1 {
		t.Errorf("remove pass: Removed=%d, want 1", pass.Removed)
	}
	if got := len(idx.entries); got != 3 { // sub, sub/b.txt, c.txt
		t.Errorf("after remove: entries=%d, want 3", got)
	}
}

func TestBuildIndex_MaxDepth(t *testing.T) {
	dir := t.TempDir()
	writeFile(t, filepath.Join(dir, "top.txt"), "t")
	if err := os.MkdirAll(filepath.Join(dir, "d1", "d2"), 0755); err != nil {
		t.Fatal(err)
	}
	writeFile(t, filepath.Join(dir, "d1", "l1.txt"), "1")
	writeFile(t, filepath.Join(dir, "d1", "d2", "l2.txt"), "2")

	// maxDepth=1 -> only depth-1 entries are indexed: the root's direct
	// children (top.txt, d1). Entries under d1 (depth 2+) must not appear.
	idx := testIndexer(t, dir, func(c *Config) { c.Index.MaxDepth = 1 })
	idx.BuildIndex()
	if got := len(idx.entries); got != 2 { // top.txt, d1
		t.Fatalf("maxDepth=1: entries=%d, want 2 (top.txt, d1)", got)
	}
	for p := range idx.entries {
		if strings.Contains(p, "l1.txt") || strings.Contains(p, "l2.txt") || p == "/v/d1/d2" {
			t.Errorf("maxDepth=1: depth-2+ entry %q must not be indexed", p)
		}
	}
}

func TestBuildIndex_ExcludeFiles(t *testing.T) {
	dir := t.TempDir()
	writeFile(t, filepath.Join(dir, "ok.txt"), "ok")
	writeFile(t, filepath.Join(dir, ".env"), "SECRET=1")
	writeFile(t, filepath.Join(dir, "backup.key"), "k")
	writeFile(t, filepath.Join(dir, ".DS_Store"), "x")

	idx := testIndexer(t, dir, nil)
	idx.BuildIndex()
	for p := range idx.entries {
		if p != "/v/ok.txt" {
			t.Errorf("excluded file %q made it into the index", p)
		}
	}

	// the same rules apply to live directory listing
	list, err := idx.ListDir("/v")
	if err != nil {
		t.Fatal(err)
	}
	if len(list) != 1 || list[0].Name != "ok.txt" {
		t.Errorf("ListDir should apply exclusions, got %+v", list)
	}
}

func TestBuildIndex_ExcludeDirsConsistentWithList(t *testing.T) {
	dir := t.TempDir()
	if err := os.MkdirAll(filepath.Join(dir, "node_modules"), 0755); err != nil {
		t.Fatal(err)
	}
	writeFile(t, filepath.Join(dir, "node_modules", "x.js"), "x")

	idx := testIndexer(t, dir, nil)
	idx.BuildIndex()
	if len(idx.entries) != 0 {
		t.Errorf("excluded dir leaked into index: %+v", idx.entries)
	}
	list, err := idx.ListDir("/v")
	if err != nil {
		t.Fatal(err)
	}
	if len(list) != 0 {
		t.Errorf("ListDir should hide excluded dirs, got %+v", list)
	}
}

func TestBuildIndex_DoesNotFollowDirSymlink(t *testing.T) {
	dir := t.TempDir()
	outside := t.TempDir()
	writeFile(t, filepath.Join(outside, "secret.txt"), "s")
	if err := os.MkdirAll(filepath.Join(dir, "real"), 0755); err != nil {
		t.Fatal(err)
	}
	writeFile(t, filepath.Join(dir, "real", "ok.txt"), "o")
	if err := os.Symlink(outside, filepath.Join(dir, "link")); err != nil {
		t.Skipf("cannot create symlink on this platform: %v", err)
	}

	idx := testIndexer(t, dir, nil)
	idx.BuildIndex()
	for p := range idx.entries {
		if p == "/v/link/secret.txt" || p == "/v/outside/secret.txt" {
			t.Errorf("index walked through a directory symlink: %q", p)
		}
	}
}

func TestPathWithin(t *testing.T) {
	cases := []struct {
		child, parent string
		want          bool
	}{
		{"/mnt/data/a.txt", "/mnt/data", true},
		{"/mnt/data/sub/a.txt", "/mnt/data", true},
		{"/mnt/data", "/mnt/data", true},
		{"/mnt/data2/a.txt", "/mnt/data", false},
		{"/etc/passwd", "/mnt/data", false},
		{"/mnt", "/mnt/data", false},
	}
	for _, c := range cases {
		if got := pathWithin(c.child, c.parent); got != c.want {
			t.Errorf("pathWithin(%q, %q)=%v, want %v", c.child, c.parent, got, c.want)
		}
	}
}

func TestPersistSaveLoadRoundTrip(t *testing.T) {
	dir := t.TempDir()
	writeFile(t, filepath.Join(dir, "a.txt"), "a")
	if err := os.MkdirAll(filepath.Join(dir, "sub"), 0755); err != nil {
		t.Fatal(err)
	}
	writeFile(t, filepath.Join(dir, "sub", "b.txt"), "b")

	idx := testIndexer(t, dir, nil)
	idx.persistPath = filepath.Join(t.TempDir(), "filelist.idx")
	settle()
	idx.BuildIndex()
	if err := idx.save(); err != nil {
		t.Fatalf("save: %v", err)
	}

	// a fresh indexer should restore entries + directory stamps from disk
	idx2 := testIndexer(t, dir, nil)
	idx2.persistPath = idx.persistPath
	if err := idx2.load(); err != nil {
		t.Fatalf("load: %v", err)
	}
	if got := len(idx2.entries); got != 3 {
		t.Fatalf("after load: entries=%d, want 3", got)
	}
	if _, ok := idx2.entries["/v/sub/b.txt"]; !ok {
		t.Error("after load: missing /v/sub/b.txt")
	}
	if _, ok := idx2.dirStamp["/v/sub"]; !ok {
		t.Error("after load: missing dir stamp for /v/sub")
	}

	// a no-change incremental pass on the restored index must skip everything
	idx2.mu.Lock()
	idx2.gen = 0
	idx2.mu.Unlock()
	r := idx2.BuildIndex()
	if r.Skipped == 0 {
		t.Error("incremental pass after restore should skip the untouched root")
	}
	if r.Added != 0 || r.Changed != 0 || r.Removed != 0 {
		t.Errorf("unexpected diff after restore: %+v", r)
	}
}
