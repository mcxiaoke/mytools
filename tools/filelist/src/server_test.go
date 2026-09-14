package main

import (
	"archive/zip"
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

func TestPageHTML_BuildInfo(t *testing.T) {
	gitCommit = "abc1234"
	buildTime = "2026-09-14 11:30:00"
	defer func() {
		gitCommit = ""
		buildTime = ""
	}()

	cfg := &Config{}
	srv := NewServer(cfg, &Indexer{cfg: cfg})

	html := srv.pageHTML()
	if !bytes.Contains([]byte(html), []byte("abc1234")) {
		t.Errorf("expected pageHTML to contain gitCommit abc1234")
	}
	if !bytes.Contains([]byte(html), []byte("2026-09-14 11:30:00")) {
		t.Errorf("expected pageHTML to contain buildTime 2026-09-14 11:30:00")
	}
	if bytes.Contains([]byte(html), []byte("__FILELIST_GIT_COMMIT__")) {
		t.Errorf("placeholder __FILELIST_GIT_COMMIT__ was not replaced")
	}
	if bytes.Contains([]byte(html), []byte("__FILELIST_BUILD_TIME__")) {
		t.Errorf("placeholder __FILELIST_BUILD_TIME__ was not replaced")
	}
}

func TestHandleStats_BuildInfo(t *testing.T) {
	gitCommit = "testcommit789"
	buildTime = "2026-09-14 12:00:00"
	defer func() {
		gitCommit = ""
		buildTime = ""
	}()

	cfg := &Config{}
	srv := NewServer(cfg, &Indexer{cfg: cfg})

	req := httptest.NewRequest(http.MethodGet, "/api/stats", nil)
	rec := httptest.NewRecorder()
	srv.handleStats(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200 OK, got %d", rec.Code)
	}

	var data map[string]interface{}
	if err := json.Unmarshal(rec.Body.Bytes(), &data); err != nil {
		t.Fatalf("unmarshal stats error: %v", err)
	}

	if data["gitCommit"] != "testcommit789" {
		t.Errorf("expected gitCommit 'testcommit789', got %v", data["gitCommit"])
	}
	if data["buildTime"] != "2026-09-14 12:00:00" {
		t.Errorf("expected buildTime '2026-09-14 12:00:00', got %v", data["buildTime"])
	}
}

func TestHandleContent_GetAndPut(t *testing.T) {
	tmp := t.TempDir()
	filePath := filepath.Join(tmp, "note.txt")
	if err := os.WriteFile(filePath, []byte("hello world"), 0644); err != nil {
		t.Fatal(err)
	}

	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: tmp},
		},
	}
	cfg.Manage.Enabled = true
	cfg.Index.ExcludeFiles = []string{".env"}
	idx := NewIndexer(cfg)
	idx.BuildIndex()
	srv := NewServer(cfg, idx)

	// 1. GET content
	req := httptest.NewRequest(http.MethodGet, "/api/content?path=/data/note.txt", nil)
	rec := httptest.NewRecorder()
	srv.handleContent(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200 OK for GET content, got %d: %s", rec.Code, rec.Body.String())
	}
	var res map[string]any
	if err := json.Unmarshal(rec.Body.Bytes(), &res); err != nil {
		t.Fatal(err)
	}
	if res["content"] != "hello world" {
		t.Errorf("expected 'hello world', got %v", res["content"])
	}

	// 2. PUT content
	putReq := httptest.NewRequest(http.MethodPut, "/api/content?path=/data/note.txt", bytes.NewBufferString("updated text"))
	putRec := httptest.NewRecorder()
	srv.handleContent(putRec, putReq)
	if putRec.Code != http.StatusOK {
		t.Fatalf("expected 200 OK for PUT content, got %d: %s", putRec.Code, putRec.Body.String())
	}
	updatedOnDisk, err := os.ReadFile(filePath)
	if err != nil {
		t.Fatal(err)
	}
	if string(updatedOnDisk) != "updated text" {
		t.Errorf("expected updated text on disk, got %s", string(updatedOnDisk))
	}

	// 3. PUT content on excluded file blocked
	envReq := httptest.NewRequest(http.MethodPut, "/api/content?path=/data/.env", bytes.NewBufferString("SECRET=123"))
	envRec := httptest.NewRecorder()
	srv.handleContent(envRec, envReq)
	if envRec.Code != http.StatusForbidden {
		t.Errorf("expected 403 Forbidden for editing .env, got %d", envRec.Code)
	}
}

func TestHandleMkdir(t *testing.T) {
	tmp := t.TempDir()
	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: tmp},
		},
	}
	cfg.Manage.Enabled = true
	idx := NewIndexer(cfg)
	idx.BuildIndex()
	srv := NewServer(cfg, idx)

	// 1. Create dir
	req := httptest.NewRequest(http.MethodPost, "/api/mkdir?path=/data&name=my_folder", nil)
	rec := httptest.NewRecorder()
	srv.handleMkdir(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200 OK, got %d: %s", rec.Code, rec.Body.String())
	}
	if info, err := os.Stat(filepath.Join(tmp, "my_folder")); err != nil || !info.IsDir() {
		t.Fatalf("my_folder not created on disk: %v", err)
	}

	// 2. Conflict on re-create
	req2 := httptest.NewRequest(http.MethodPost, "/api/mkdir?path=/data&name=my_folder", nil)
	rec2 := httptest.NewRecorder()
	srv.handleMkdir(rec2, req2)
	if rec2.Code != http.StatusConflict {
		t.Errorf("expected 409 Conflict for existing dir, got %d", rec2.Code)
	}
}

func TestHandleRename(t *testing.T) {
	tmp := t.TempDir()
	filePath := filepath.Join(tmp, "orig.txt")
	os.WriteFile(filePath, []byte("data"), 0644)

	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: tmp},
		},
	}
	cfg.Manage.Enabled = true
	idx := NewIndexer(cfg)
	idx.BuildIndex()
	srv := NewServer(cfg, idx)

	// 1. Rename orig.txt -> new.txt
	req := httptest.NewRequest(http.MethodPost, "/api/rename?path=/data/orig.txt&new_name=new.txt", nil)
	rec := httptest.NewRecorder()
	srv.handleRename(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200 OK, got %d: %s", rec.Code, rec.Body.String())
	}
	if _, err := os.Stat(filepath.Join(tmp, "new.txt")); err != nil {
		t.Errorf("new.txt not found on disk: %v", err)
	}
	if _, err := os.Stat(filePath); !os.IsNotExist(err) {
		t.Errorf("orig.txt still exists on disk")
	}

	// 2. Renaming root mount rejected
	rootReq := httptest.NewRequest(http.MethodPost, "/api/rename?path=/data&new_name=data2", nil)
	rootRec := httptest.NewRecorder()
	srv.handleRename(rootRec, rootReq)
	if rootRec.Code != http.StatusBadRequest {
		t.Errorf("expected 400 Bad Request when renaming root, got %d", rootRec.Code)
	}
}

func TestHandleDelete_SafeWithToken(t *testing.T) {
	tmp := t.TempDir()
	filePath := filepath.Join(tmp, "delete_me.txt")
	os.WriteFile(filePath, []byte("data"), 0644)

	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: tmp},
		},
	}
	cfg.Manage.Enabled = true
	cfg.Manage.AllowDelete = true
	cfg.Manage.DeleteToken = "secret123"
	idx := NewIndexer(cfg)
	idx.BuildIndex()
	srv := NewServer(cfg, idx)

	// 1. Missing token
	req1 := httptest.NewRequest(http.MethodPost, "/api/delete?path=/data/delete_me.txt", nil)
	rec1 := httptest.NewRecorder()
	srv.handleDelete(rec1, req1)
	if rec1.Code != http.StatusForbidden {
		t.Errorf("expected 403 Forbidden without token, got %d", rec1.Code)
	}

	// 2. Wrong token
	req2 := httptest.NewRequest(http.MethodPost, "/api/delete?path=/data/delete_me.txt&token=wrong", nil)
	rec2 := httptest.NewRecorder()
	srv.handleDelete(rec2, req2)
	if rec2.Code != http.StatusForbidden {
		t.Errorf("expected 403 Forbidden with wrong token, got %d", rec2.Code)
	}

	// 3. Delete root mount rejected even with correct token
	req3 := httptest.NewRequest(http.MethodPost, "/api/delete?path=/data&token=secret123", nil)
	rec3 := httptest.NewRecorder()
	srv.handleDelete(rec3, req3)
	if rec3.Code != http.StatusBadRequest {
		t.Errorf("expected 400 Bad Request when deleting root mount, got %d", rec3.Code)
	}

	// 4. Correct token via header
	req4 := httptest.NewRequest(http.MethodPost, "/api/delete?path=/data/delete_me.txt", nil)
	req4.Header.Set("X-Delete-Token", "secret123")
	rec4 := httptest.NewRecorder()
	srv.handleDelete(rec4, req4)
	if rec4.Code != http.StatusOK {
		t.Fatalf("expected 200 OK with correct token, got %d: %s", rec4.Code, rec4.Body.String())
	}
	if _, err := os.Stat(filePath); !os.IsNotExist(err) {
		t.Errorf("delete_me.txt still exists on disk")
	}
}

func TestHandleZip(t *testing.T) {
	tmp := t.TempDir()
	os.WriteFile(filepath.Join(tmp, "f1.txt"), []byte("file1"), 0644)
	sub := filepath.Join(tmp, "sub")
	os.Mkdir(sub, 0755)
	os.WriteFile(filepath.Join(sub, "f2.txt"), []byte("file2"), 0644)

	cfg := &Config{
		Roots: []RootMapping{
			{URL: "/data", Path: tmp},
		},
	}
	idx := NewIndexer(cfg)
	idx.BuildIndex()
	srv := NewServer(cfg, idx)

	req := httptest.NewRequest(http.MethodGet, "/api/zip?path=/data", nil)
	rec := httptest.NewRecorder()
	srv.handleZip(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200 OK, got %d", rec.Code)
	}
	if rec.Header().Get("Content-Type") != "application/zip" {
		t.Errorf("expected application/zip Content-Type, got %s", rec.Header().Get("Content-Type"))
	}

	zipReader, err := zip.NewReader(bytes.NewReader(rec.Body.Bytes()), int64(rec.Body.Len()))
	if err != nil {
		t.Fatalf("invalid zip output: %v", err)
	}

	names := make(map[string]bool)
	for _, f := range zipReader.File {
		names[f.Name] = true
	}
	if !names["f1.txt"] {
		t.Errorf("missing f1.txt in zip, got %v", names)
	}
	if !names["sub/f2.txt"] {
		t.Errorf("missing sub/f2.txt in zip, got %v", names)
	}
}

