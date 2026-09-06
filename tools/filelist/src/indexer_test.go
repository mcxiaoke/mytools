package main

import (
	"path/filepath"
	"testing"
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
