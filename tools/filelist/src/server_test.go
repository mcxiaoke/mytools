package main

import (
	"bytes"
	"encoding/json"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

func createMultipartRequest(t *testing.T, targetURL string, files map[string]string) *http.Request {
	t.Helper()
	var body bytes.Buffer
	writer := multipart.NewWriter(&body)

	for filename, content := range files {
		part, err := writer.CreateFormFile("files", filename)
		if err != nil {
			t.Fatalf("CreateFormFile error: %v", err)
		}
		if _, err := part.Write([]byte(content)); err != nil {
			t.Fatalf("Write part error: %v", err)
		}
	}
	if err := writer.Close(); err != nil {
		t.Fatalf("writer Close error: %v", err)
	}

	req := httptest.NewRequest(http.MethodPost, targetURL, &body)
	req.Header.Set("Content-Type", writer.FormDataContentType())
	return req
}

func TestHandleUpload_Disabled(t *testing.T) {
	tmp := t.TempDir()
	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: tmp},
		},
	}
	cfg.Upload.Enabled = false
	idx := &Indexer{cfg: cfg}
	srv := NewServer(cfg, idx)

	req := createMultipartRequest(t, "/api/upload?path=/data", map[string]string{
		"test.txt": "hello",
	})
	rec := httptest.NewRecorder()

	srv.handleUpload(rec, req)
	if rec.Code != http.StatusForbidden {
		t.Errorf("expected 403 Forbidden when upload is disabled, got %d", rec.Code)
	}
}

func TestHandleUpload_RootRejected(t *testing.T) {
	tmp := t.TempDir()
	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: tmp},
		},
	}
	cfg.Upload.Enabled = true
	idx := &Indexer{cfg: cfg}
	srv := NewServer(cfg, idx)

	// Try uploading to root view
	req := createMultipartRequest(t, "/api/upload?path=/", map[string]string{
		"test.txt": "hello",
	})
	rec := httptest.NewRecorder()

	srv.handleUpload(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Errorf("expected 400 Bad Request when uploading to root, got %d", rec.Code)
	}
}

func TestHandleUpload_SuccessAndAutoSuffix(t *testing.T) {
	tmp := t.TempDir()
	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: tmp},
		},
	}
	cfg.Upload.Enabled = true
	idx := &Indexer{cfg: cfg}
	srv := NewServer(cfg, idx)

	// 1. Upload first file
	req1 := createMultipartRequest(t, "/api/upload?path=/data", map[string]string{
		"doc.txt": "first version",
	})
	rec1 := httptest.NewRecorder()
	srv.handleUpload(rec1, req1)

	if rec1.Code != http.StatusOK {
		t.Fatalf("first upload failed: code %d, body: %s", rec1.Code, rec1.Body.String())
	}

	content1, err := os.ReadFile(filepath.Join(tmp, "doc.txt"))
	if err != nil || string(content1) != "first version" {
		t.Fatalf("file doc.txt content mismatch: %v, %s", err, string(content1))
	}

	// 2. Upload same file again -> should auto suffix to doc (1).txt
	req2 := createMultipartRequest(t, "/api/upload?path=/data", map[string]string{
		"doc.txt": "second version",
	})
	rec2 := httptest.NewRecorder()
	srv.handleUpload(rec2, req2)

	if rec2.Code != http.StatusOK {
		t.Fatalf("second upload failed: code %d, body: %s", rec2.Code, rec2.Body.String())
	}

	var resp struct {
		Success  bool     `json:"success"`
		Uploaded []string `json:"uploaded"`
	}
	if err := json.NewDecoder(rec2.Body).Decode(&resp); err != nil {
		t.Fatalf("decode json response error: %v", err)
	}
	if len(resp.Uploaded) != 1 || resp.Uploaded[0] != "doc (1).txt" {
		t.Errorf("expected uploaded file 'doc (1).txt', got %v", resp.Uploaded)
	}

	content2, err := os.ReadFile(filepath.Join(tmp, "doc (1).txt"))
	if err != nil || string(content2) != "second version" {
		t.Fatalf("file doc (1).txt content mismatch: %v, %s", err, string(content2))
	}

	// Original file must be preserved intact
	contentOriginal, err := os.ReadFile(filepath.Join(tmp, "doc.txt"))
	if err != nil || string(contentOriginal) != "first version" {
		t.Fatalf("original doc.txt was modified: %v, %s", err, string(contentOriginal))
	}
}

func TestHandleUpload_ExcludeFiles(t *testing.T) {
	tmp := t.TempDir()
	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: tmp},
		},
	}
	cfg.Index.ExcludeFiles = []string{".env", "*.key"}
	cfg.Upload.Enabled = true
	idx := &Indexer{cfg: cfg}
	srv := NewServer(cfg, idx)

	req := createMultipartRequest(t, "/api/upload?path=/data", map[string]string{
		".env":       "SECRET=123",
		"normal.txt": "normal content",
	})
	rec := httptest.NewRecorder()
	srv.handleUpload(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("upload failed: code %d, body: %s", rec.Code, rec.Body.String())
	}

	// .env should be blocked and not created
	if _, err := os.Stat(filepath.Join(tmp, ".env")); !os.IsNotExist(err) {
		t.Errorf(".env was excluded and should not be created")
	}

	// normal.txt should be created
	if _, err := os.Stat(filepath.Join(tmp, "normal.txt")); err != nil {
		t.Errorf("normal.txt should be created: %v", err)
	}
}

func TestHandleUpload_Sanitize(t *testing.T) {
	tmp := t.TempDir()
	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: tmp},
		},
	}
	cfg.Upload.Enabled = true
	idx := &Indexer{cfg: cfg}
	srv := NewServer(cfg, idx)

	req := createMultipartRequest(t, "/api/upload?path=/data", map[string]string{
		`bad:name*?.txt`: "content1",
		`CON.txt`:        "content2",
	})
	rec := httptest.NewRecorder()
	srv.handleUpload(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("upload failed: code %d, body: %s", rec.Code, rec.Body.String())
	}

	// bad:name*?.txt should be sanitized to bad_name__.txt
	if _, err := os.Stat(filepath.Join(tmp, "bad_name__.txt")); err != nil {
		t.Errorf("bad_name__.txt not found: %v", err)
	}

	// CON.txt should be sanitized to _CON.txt
	if _, err := os.Stat(filepath.Join(tmp, "_CON.txt")); err != nil {
		t.Errorf("_CON.txt not found: %v", err)
	}
}
