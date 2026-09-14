package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"unicode/utf8"
)

func TestSplitNameExt(t *testing.T) {
	tests := []struct {
		filename string
		wantStem string
		wantExt  string
	}{
		{"test.txt", "test", ".txt"},
		{"test", "test", ""},
		{".gitignore", ".gitignore", ""},
		{"archive.tar.gz", "archive.tar", ".gz"},
		{"file.name.with.dots.pdf", "file.name.with.dots", ".pdf"},
	}

	for _, tt := range tests {
		stem, ext := splitNameExt(tt.filename)
		if stem != tt.wantStem || ext != tt.wantExt {
			t.Errorf("splitNameExt(%q) = (%q, %q); want (%q, %q)",
				tt.filename, stem, ext, tt.wantStem, tt.wantExt)
		}
	}
}

func TestAvailableFilename(t *testing.T) {
	tmp := t.TempDir()

	// Initial available name
	if got := availableFilename(tmp, "file.txt"); got != "file.txt" {
		t.Errorf("expected file.txt, got %s", got)
	}

	// Create file.txt
	if err := os.WriteFile(filepath.Join(tmp, "file.txt"), []byte("1"), 0644); err != nil {
		t.Fatal(err)
	}
	if got := availableFilename(tmp, "file.txt"); got != "file (1).txt" {
		t.Errorf("expected file (1).txt, got %s", got)
	}

	// Create file (1).txt
	if err := os.WriteFile(filepath.Join(tmp, "file (1).txt"), []byte("2"), 0644); err != nil {
		t.Fatal(err)
	}
	if got := availableFilename(tmp, "file.txt"); got != "file (2).txt" {
		t.Errorf("expected file (2).txt, got %s", got)
	}

	// Dotfile
	if got := availableFilename(tmp, ".env"); got != ".env" {
		t.Errorf("expected .env, got %s", got)
	}
	if err := os.WriteFile(filepath.Join(tmp, ".env"), []byte("secret"), 0644); err != nil {
		t.Fatal(err)
	}
	if got := availableFilename(tmp, ".env"); got != ".env (1)" {
		t.Errorf("expected .env (1), got %s", got)
	}
}

func TestTruncateUTF8(t *testing.T) {
	tests := []struct {
		input    string
		maxBytes int
		want     string
	}{
		{"hello", 10, "hello"},
		{"hello", 5, "hello"},
		{"hello", 4, "hell"},
		{"hello", 0, ""},
		{"你好世界", 12, "你好世界"},
		{"你好世界", 6, "你好"},
		{"你好世界", 5, "你"},
		{"你好世界", 4, "你"},
		{"你好世界", 2, ""},
	}

	for _, tt := range tests {
		got := truncateUTF8(tt.input, tt.maxBytes)
		if got != tt.want {
			t.Errorf("truncateUTF8(%q, %d) = %q, want %q", tt.input, tt.maxBytes, got, tt.want)
		}
	}
}

func TestSanitizeFilename(t *testing.T) {
	tests := []struct {
		input string
		want  string
	}{
		{"normal.txt", "normal.txt"},
		{"path/to/file.png", "file.png"},
		{`C:\Windows\System32\cmd.exe`, "cmd.exe"},
		{`a/b\c:d*e?f"g<h>i|j.txt`, "c_d_e_f_g_h_i_j.txt"},
		{"with spaces  .txt", "with spaces.txt"},
		{"trailing.dots...", "trailing.dots"},
		{"trailing space and dot.txt. . ", "trailing space and dot.txt"},
		{"   ", "upload"},
		{"...", "upload"},
		{"con", "_con"},
		{"CON.txt", "_CON.txt"},
		{"aux.log", "_aux.log"},
		{"aux.tar.gz", "_aux.tar.gz"},
		{"nul", "_nul"},
		{"com1.pdf", "_com1.pdf"},
		{"\x00\x1f\x7ftest\t.txt", "___test_.txt"},
	}

	for _, tt := range tests {
		got := sanitizeFilename(tt.input)
		if got != tt.want {
			t.Errorf("sanitizeFilename(%q) = %q, want %q", tt.input, got, tt.want)
		}
	}
}

func TestSanitizeFilename_Truncation(t *testing.T) {
	longASCII := strings.Repeat("a", 300) + ".txt"
	gotASCII := sanitizeFilename(longASCII)
	if len(gotASCII) > 240 {
		t.Errorf("expected len <= 240, got %d", len(gotASCII))
	}
	if !strings.HasSuffix(gotASCII, ".txt") {
		t.Errorf("expected extension .txt preserved, got %s", gotASCII)
	}

	longCN := strings.Repeat("中", 100) + ".dat"
	gotCN := sanitizeFilename(longCN)
	if len(gotCN) > 240 {
		t.Errorf("expected len <= 240, got %d", len(gotCN))
	}
	if !strings.HasSuffix(gotCN, ".dat") {
		t.Errorf("expected extension .dat preserved, got %s", gotCN)
	}
	if !utf8.ValidString(gotCN) {
		t.Errorf("expected valid UTF-8, got %q", gotCN)
	}
}
