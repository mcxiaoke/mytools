package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// writeConfig writes a YAML config to a temp file and returns its path.
func writeConfig(t *testing.T, content string) string {
	t.Helper()
	dir := t.TempDir()
	p := filepath.Join(dir, "config.yaml")
	if err := os.WriteFile(p, []byte(content), 0644); err != nil {
		t.Fatalf("write config: %v", err)
	}
	return p
}

func TestLoadConfig_RejectsRootSlash(t *testing.T) {
	cfg := `
roots:
  - url: /
    path: /tmp
`
	_, err := LoadConfig(writeConfig(t, cfg))
	if err == nil {
		t.Fatal("expected error for root url \"/\", got nil")
	}
	// the error message should mention the root index and hint at subpaths
	if !strings.Contains(err.Error(), "not allowed") {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestLoadConfig_RejectsTrailingSlashBecomingRoot(t *testing.T) {
	// url: //  or  url: /  should both be rejected after trimming
	cfg := `
roots:
  - url: //
    path: /tmp
`
	_, err := LoadConfig(writeConfig(t, cfg))
	if err == nil {
		t.Fatal("expected error for root url \"//\", got nil")
	}
}

func TestLoadConfig_AcceptsSubpaths(t *testing.T) {
	cfg := `
roots:
  - url: /data
    path: /tmp
  - url: /downloads
    path: /home/user/Downloads
`
	c, err := LoadConfig(writeConfig(t, cfg))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(c.Roots) != 2 {
		t.Fatalf("expected 2 roots, got %d", len(c.Roots))
	}
	// roots are sorted by URL length (longest first) for longest-prefix matching
	if c.Roots[0].URL != "/downloads" {
		t.Errorf("root[0] url: got %q, want /downloads", c.Roots[0].URL)
	}
	if c.Roots[1].URL != "/data" {
		t.Errorf("root[1] url: got %q, want /data", c.Roots[1].URL)
	}
}

func TestLoadConfig_TrimsTrailingSlash(t *testing.T) {
	cfg := `
roots:
  - url: /data/
    path: /tmp
`
	c, err := LoadConfig(writeConfig(t, cfg))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if c.Roots[0].URL != "/data" {
		t.Errorf("got %q, want /data", c.Roots[0].URL)
	}
}

func TestLoadConfig_PrependSlash(t *testing.T) {
	cfg := `
roots:
  - url: data
    path: /tmp
`
	c, err := LoadConfig(writeConfig(t, cfg))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if c.Roots[0].URL != "/data" {
		t.Errorf("got %q, want /data", c.Roots[0].URL)
	}
}

func TestLoadConfig_RejectsEmptyURL(t *testing.T) {
	cfg := `
roots:
  - url: ""
    path: /tmp
`
	_, err := LoadConfig(writeConfig(t, cfg))
	if err == nil {
		t.Fatal("expected error for empty url, got nil")
	}
}

func TestLoadConfig_RejectsEmptyPath(t *testing.T) {
	cfg := `
roots:
  - url: /data
    path: ""
`
	_, err := LoadConfig(writeConfig(t, cfg))
	if err == nil {
		t.Fatal("expected error for empty path, got nil")
	}
}

func TestLoadConfig_RejectsNoRoots(t *testing.T) {
	cfg := `
server:
  port: 8080
`
	_, err := LoadConfig(writeConfig(t, cfg))
	if err == nil {
		t.Fatal("expected error for no roots, got nil")
	}
}
