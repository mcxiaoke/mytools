package main

import (
	"archive/zip"
	"bytes"
	"crypto/subtle"
	"embed"
	"encoding/json"
	"fmt"
	"io"
	"io/fs"
	"net/http"
	"os"
	"path"
	"path/filepath"
	"strings"
	"time"
)

//go:embed web/*
var webFS embed.FS

// basePlaceholder is replaced at request time with the configured base path.
const basePlaceholder = "__FILELIST_BASE__"

// uploadPlaceholder is replaced at request time with true/false indicating if upload is enabled.
const uploadPlaceholder = "__FILELIST_UPLOAD__"

// managePlaceholder is replaced at request time with JSON client manage configuration.
const managePlaceholder = "__FILELIST_MANAGE__"

// versionPlaceholder is replaced at request time with current build version for cache busting.
const versionPlaceholder = "__FILELIST_VERSION__"

// gitCommitPlaceholder is replaced at request time with git commit hash.
const gitCommitPlaceholder = "__FILELIST_GIT_COMMIT__"

// buildTimePlaceholder is replaced at request time with binary build timestamp.
const buildTimePlaceholder = "__FILELIST_BUILD_TIME__"

// tokenCookieName holds the access token once a visitor has authenticated.
const tokenCookieName = "filelist_token"

// faviconSVG is a simple folder icon served at /favicon.ico.
var faviconSVG = []byte(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><path fill="#ffb340" d="M1 3h5l2 2h7v9H1z"/></svg>`)

// Server holds shared dependencies for HTTP handlers.
type Server struct {
	cfg     *Config
	indexer *Indexer
}

// NewServer creates a new HTTP server instance.
func NewServer(cfg *Config, idx *Indexer) *Server {
	return &Server{cfg: cfg, indexer: idx}
}

// Routes returns the configured HTTP mux. All routes are relative to the
// configured base path — the stripBase middleware removes the prefix before
// the mux sees the request.
func (s *Server) Routes() *http.ServeMux {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/roots", s.handleRoots)
	mux.HandleFunc("/api/list", s.handleList)
	mux.HandleFunc("/api/search", s.handleSearch)
	mux.HandleFunc("/api/stats", s.handleStats)
	mux.HandleFunc("/api/upload", s.handleUpload)
	mux.HandleFunc("/api/content", s.handleContent)
	mux.HandleFunc("/api/mkdir", s.handleMkdir)
	mux.HandleFunc("/api/rename", s.handleRename)
	mux.HandleFunc("/api/delete", s.handleDelete)
	mux.HandleFunc("/api/zip", s.handleZip)
	mux.HandleFunc("/raw/", s.handleRaw)
	mux.HandleFunc("/favicon.ico", s.handleFavicon)
	mux.Handle("/static/", s.handleStatic())
	mux.HandleFunc("/", s.handleIndex)
	return mux
}

// handleStatic serves static assets from embedded web/static directory.
func (s *Server) handleStatic() http.Handler {
	sub, err := fs.Sub(webFS, "web/static")
	if err != nil {
		panic(err)
	}
	fileServer := http.FileServer(http.FS(sub))
	return http.StripPrefix("/static/", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-cache")
		fileServer.ServeHTTP(w, r)
	}))
}

// Handler returns the full middleware chain: security headers, base-path
// stripping, optional token auth, then the routes.
func (s *Server) Handler() http.Handler {
	return securityHeaders(s.stripBase(s.authMiddleware(s.Routes())))
}

// handleIndex serves the embedded SPA for all non-API, non-raw paths.
func (s *Server) handleIndex(w http.ResponseWriter, r *http.Request) {
	if strings.HasPrefix(r.URL.Path, "/api/") || strings.HasPrefix(r.URL.Path, "/raw/") || strings.HasPrefix(r.URL.Path, "/static/") {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-cache")
	w.Write(s.pageHTML())
}

// pageHTML returns the embedded SPA with the base path and upload enabled injected.
func (s *Server) pageHTML() []byte {
	tmpl, err := webFS.ReadFile("web/index.html")
	if err != nil {
		return []byte("internal error: template missing")
	}
	out := bytes.ReplaceAll(tmpl, []byte(basePlaceholder), []byte(s.cfg.Server.BasePath))
	uploadVal := []byte("false")
	if s.cfg.Upload.Enabled {
		uploadVal = []byte("true")
	}
	out = bytes.ReplaceAll(out, []byte(uploadPlaceholder), uploadVal)
	manageConfigJSON, _ := json.Marshal(map[string]bool{
		"enabled":     s.cfg.ManageEnabled(),
		"allowEdit":   s.cfg.ManageEdit(),
		"allowMkdir":  s.cfg.ManageMkdir(),
		"allowRename": s.cfg.ManageRename(),
		"allowDelete": s.cfg.ManageDelete(),
	})
	out = bytes.ReplaceAll(out, []byte(managePlaceholder), manageConfigJSON)
	out = bytes.ReplaceAll(out, []byte(versionPlaceholder), []byte(version+"-"+gitCommit))
	out = bytes.ReplaceAll(out, []byte(gitCommitPlaceholder), []byte(gitCommit))
	return bytes.ReplaceAll(out, []byte(buildTimePlaceholder), []byte(buildTime))
}

// handleFavicon serves a simple SVG favicon to avoid 404 noise.
func (s *Server) handleFavicon(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "image/svg+xml")
	w.Header().Set("Cache-Control", "public, max-age=86400")
	w.Write(faviconSVG)
}

// handleRoots returns the configured root mappings as Entry objects.
// Each root is represented as a directory entry whose Name is the URL path
// segment (e.g. /data -> "data") and Path is the configured URL.
func (s *Server) handleRoots(w http.ResponseWriter, r *http.Request) {
	entries := make([]Entry, len(s.cfg.Roots))
	for i, root := range s.cfg.Roots {
		e := Entry{
			Name:  strings.TrimPrefix(root.URL, "/"),
			Path:  root.URL,
			IsDir: true,
		}
		if info, err := os.Stat(root.Path); err == nil {
			e.Size = info.Size()
			e.ModTime = info.ModTime()
		}
		entries[i] = e
	}
	writeJSON(w, entries)
}

// handleList returns the contents of a directory at the given virtual path.
// Empty path returns the roots list; any non-empty path (including "/")
// lists the actual directory contents.
func (s *Server) handleList(w http.ResponseWriter, r *http.Request) {
	p := r.URL.Query().Get("path")
	if p == "" {
		s.handleRoots(w, r)
		return
	}
	entries, err := s.indexer.ListDir(p)
	if err != nil {
		switch {
		case os.IsPermission(err):
			logger.Warn("server: list %s: %v", p, err)
			http.Error(w, "forbidden", http.StatusForbidden)
		case os.IsNotExist(err):
			http.Error(w, "not found", http.StatusNotFound)
		default:
			logger.Warn("server: list %s: %v", p, err)
			http.Error(w, "cannot read directory", http.StatusInternalServerError)
		}
		return
	}
	writeJSON(w, entries)
}

// handleSearch queries the index and returns matching entries.
func (s *Server) handleSearch(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query().Get("q")
	if q == "" {
		writeJSON(w, []Entry{})
		return
	}
	results := s.indexer.Search(q, 500)
	writeJSON(w, results)
}

// handleStats returns index statistics.
func (s *Server) handleStats(w http.ResponseWriter, r *http.Request) {
	stats := s.indexer.Stats()
	stats["version"] = version
	stats["gitCommit"] = gitCommit
	stats["buildTime"] = buildTime
	writeJSON(w, stats)
}

// handleRaw serves a raw file for download or preview.
func (s *Server) handleRaw(w http.ResponseWriter, r *http.Request) {
	vpath := strings.TrimPrefix(r.URL.Path, "/raw")
	if vpath == "" || vpath == "/" {
		http.NotFound(w, r)
		return
	}

	realPath, ok := s.indexer.MapVirtualToReal(vpath)
	if !ok {
		http.NotFound(w, r)
		return
	}

	// Block symlinks that point outside the configured root: a shared folder
	// containing a link to /etc (or another disk) must not expose it.
	if !s.cfg.Security.AllowOutsideSymlinks {
		if root, ok := s.indexer.RootFor(vpath); ok && !withinRoot(realPath, root.Path) {
			logger.Warn("server: blocked symlink outside root: %s", vpath)
			http.Error(w, "forbidden: target outside root", http.StatusForbidden)
			return
		}
	}

	info, err := os.Stat(realPath)
	if err != nil || info.IsDir() {
		http.NotFound(w, r)
		return
	}

	name := filepath.Base(realPath)

	// Always download, or force download for html/svg which would otherwise
	// execute in the site's own origin (stored XSS).
	forced := r.URL.Query().Get("download") == "1"
	if !forced && s.cfg.InlineHTMLBlocked() && isInlineRisk(name) {
		forced = true
	}
	if forced {
		w.Header().Set("Content-Disposition", contentDisposition(name))
	}

	http.ServeFile(w, r, realPath)
}

// securityHeaders adds hardening headers to every response. nosniff matters
// most: without it a shared .html file would render in the site's origin.
func securityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("Referrer-Policy", "no-referrer")
		next.ServeHTTP(w, r)
	})
}

// stripBase removes the configured base path prefix so the mux and handlers
// always see root-relative paths. When no base path is set it is a no-op.
func (s *Server) stripBase(next http.Handler) http.Handler {
	base := s.cfg.Server.BasePath
	if base == "" {
		return next
	}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		p := r.URL.Path
		if p == base {
			http.Redirect(w, r, base+"/", http.StatusFound)
			return
		}
		if !strings.HasPrefix(p, base+"/") {
			http.NotFound(w, r)
			return
		}
		r2 := r.Clone(r.Context())
		r2.URL.Path = strings.TrimPrefix(p, base)
		if r2.URL.RawPath != "" {
			r2.URL.RawPath = strings.TrimPrefix(r2.URL.RawPath, base)
		}
		next.ServeHTTP(w, r2)
	})
}

// authMiddleware enforces server.token when one is configured. The token can
// be supplied as ?token=..., an Authorization: Bearer header, or the cookie
// set on first successful visit, so browsing works without any extra clicks.
func (s *Server) authMiddleware(next http.Handler) http.Handler {
	token := s.cfg.Server.Token
	if token == "" {
		return next
	}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if s.tokenOK(r, token) {
			if r.URL.Query().Get("token") != "" {
				s.setTokenCookie(w, token)
			}
			next.ServeHTTP(w, r)
			return
		}
		s.unauthorized(w, r)
	})
}

// tokenOK checks the cookie, Authorization header and query string.
func (s *Server) tokenOK(r *http.Request, token string) bool {
	if c, err := r.Cookie(tokenCookieName); err == nil && constEqual(c.Value, token) {
		return true
	}
	if v := r.Header.Get("Authorization"); strings.HasPrefix(v, "Bearer ") &&
		constEqual(strings.TrimSpace(strings.TrimPrefix(v, "Bearer ")), token) {
		return true
	}
	if q := r.URL.Query().Get("token"); constEqual(q, token) {
		return true
	}
	return false
}

// setTokenCookie stores the token so subsequent requests are authenticated
// without carrying it in the URL.
func (s *Server) setTokenCookie(w http.ResponseWriter, token string) {
	p := s.cfg.Server.BasePath
	if p == "" {
		p = "/"
	}
	http.SetCookie(w, &http.Cookie{
		Name:     tokenCookieName,
		Value:    token,
		Path:     p,
		MaxAge:   int(30 * 24 * time.Hour / time.Second),
		HttpOnly: true,
		SameSite: http.SameSiteLaxMode,
	})
}

// unauthorized renders a short login hint for browsers and JSON for clients.
func (s *Server) unauthorized(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	if strings.HasPrefix(r.URL.Path, "/api/") {
		w.Header().Set("Content-Type", "application/json; charset=utf-8")
		w.WriteHeader(http.StatusUnauthorized)
		fmt.Fprintf(w, `{"error":"unauthorized","hint":"append ?token=<your-token> or send Authorization: Bearer <token>"}`)
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.WriteHeader(http.StatusUnauthorized)
	page := `<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>FileList - 需要访问令牌</title>
<style>body{font-family:system-ui,-apple-system,"Segoe UI",Roboto,"Noto Sans SC",sans-serif;
background:#f7f8fa;color:#1a1a2e;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
.box{background:#fff;border:1px solid #e2e5ea;border-radius:8px;padding:32px 40px;box-shadow:0 1px 3px rgba(0,0,0,.08);text-align:center}
h1{font-size:18px;margin:0 0 8px}p{color:#6b7280;font-size:14px;margin:0}
code{background:#f0f1f5;padding:2px 6px;border-radius:4px}</style></head>
<body><div class="box"><h1>需要访问令牌</h1><p>请在地址后追加 <code>?token=你的令牌</code> 后重试。</p></div></body></html>`
	fmt.Fprint(w, page)
}

// loggingMiddleware wraps a handler with request logging.
func loggingMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		next.ServeHTTP(w, r)
		logger.Debug("%s %s %s %v", r.Method, r.URL.Path, r.RemoteAddr, time.Since(start))
	})
}

// handleUpload processes file uploads to a directory via multipart/form-data.
func (s *Server) handleUpload(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if !s.cfg.Upload.Enabled {
		http.Error(w, "upload disabled", http.StatusForbidden)
		return
	}

	vpath := r.URL.Query().Get("path")
	if vpath == "" || vpath == "/" {
		http.Error(w, "cannot upload to root view, please select a directory", http.StatusBadRequest)
		return
	}

	realDir, ok := s.indexer.MapVirtualToReal(vpath)
	if !ok {
		http.Error(w, "destination path not found", http.StatusNotFound)
		return
	}

	info, err := os.Stat(realDir)
	if err != nil || !info.IsDir() {
		http.Error(w, "destination is not a valid directory", http.StatusBadRequest)
		return
	}

	if !s.cfg.Security.AllowOutsideSymlinks {
		if root, ok := s.indexer.RootFor(vpath); ok && !withinRoot(realDir, root.Path) {
			logger.Warn("server: upload blocked outside root: %s", vpath)
			http.Error(w, "forbidden: target outside root", http.StatusForbidden)
			return
		}
	}

	reader, err := r.MultipartReader()
	if err != nil {
		logger.Warn("server: upload multipart error: %v", err)
		http.Error(w, "invalid multipart request: "+err.Error(), http.StatusBadRequest)
		return
	}

	var uploaded []string
	for {
		part, err := reader.NextPart()
		if err == io.EOF {
			break
		}
		if err != nil {
			logger.Error("server: upload read part: %v", err)
			http.Error(w, "error reading upload: "+err.Error(), http.StatusInternalServerError)
			return
		}

		filename := part.FileName()
		if filename == "" {
			part.Close()
			continue
		}

		// Prevent directory traversal attacks and ensure safe filename across OS
		cleanName := sanitizeFilename(filename)
		if cleanName == "" {
			part.Close()
			continue
		}

		// Exclude sensitive or configured files
		if s.indexer.Excluded(cleanName, false) {
			logger.Warn("server: upload blocked excluded file: %s", cleanName)
			part.Close()
			continue
		}

		finalName := availableFilename(realDir, cleanName)
		dstPath := filepath.Join(realDir, finalName)

		dst, err := os.OpenFile(dstPath, os.O_CREATE|os.O_WRONLY|os.O_EXCL, 0644)
		if err != nil {
			// In case of concurrent creation, retry availableFilename
			finalName = availableFilename(realDir, cleanName)
			dstPath = filepath.Join(realDir, finalName)
			dst, err = os.OpenFile(dstPath, os.O_CREATE|os.O_WRONLY|os.O_EXCL, 0644)
			if err != nil {
				part.Close()
				logger.Error("server: failed creating file %s: %v", dstPath, err)
				http.Error(w, fmt.Sprintf("cannot create file %s: %v", finalName, err), http.StatusInternalServerError)
				return
			}
		}

		_, copyErr := io.Copy(dst, part)
		dst.Close()
		part.Close()
		if copyErr != nil {
			logger.Error("server: failed writing file %s: %v", dstPath, copyErr)
			os.Remove(dstPath)
			http.Error(w, fmt.Sprintf("failed writing file %s: %v", finalName, copyErr), http.StatusInternalServerError)
			return
		}

		uploaded = append(uploaded, finalName)
	}

	logger.Info("server: uploaded %d file(s) to %s: %v", len(uploaded), vpath, uploaded)
	writeJSON(w, map[string]any{
		"success":  true,
		"uploaded": uploaded,
	})
}

// handleContent reads (GET) or writes (PUT/POST) text file content.
func (s *Server) handleContent(w http.ResponseWriter, r *http.Request) {
	vpath := r.URL.Query().Get("path")
	if vpath == "" || vpath == "/" {
		http.Error(w, "missing or invalid path", http.StatusBadRequest)
		return
	}

	realPath, ok := s.indexer.MapVirtualToReal(vpath)
	if !ok {
		http.Error(w, "path not found", http.StatusNotFound)
		return
	}

	if !s.cfg.Security.AllowOutsideSymlinks {
		if root, ok := s.indexer.RootFor(vpath); ok && !withinRoot(realPath, root.Path) {
			logger.Warn("server: content access blocked outside root: %s", vpath)
			http.Error(w, "forbidden: target outside root", http.StatusForbidden)
			return
		}
	}

	switch r.Method {
	case http.MethodGet:
		info, err := os.Stat(realPath)
		if err != nil {
			if os.IsNotExist(err) {
				http.Error(w, "file not found", http.StatusNotFound)
			} else {
				http.Error(w, err.Error(), http.StatusInternalServerError)
			}
			return
		}
		if info.IsDir() {
			http.Error(w, "cannot view directory as text content", http.StatusBadRequest)
			return
		}
		const maxReadSize = 10 * 1024 * 1024 // 10MB limit
		if info.Size() > maxReadSize {
			http.Error(w, "file too large for text viewer (max 10MB)", http.StatusRequestEntityTooLarge)
			return
		}

		data, err := os.ReadFile(realPath)
		if err != nil {
			http.Error(w, "cannot read file: "+err.Error(), http.StatusInternalServerError)
			return
		}

		writeJSON(w, map[string]any{
			"path":     vpath,
			"name":     filepath.Base(realPath),
			"size":     info.Size(),
			"modTime":  info.ModTime(),
			"content":  string(data),
			"editable": s.cfg.ManageEdit(),
		})

	case http.MethodPut, http.MethodPost:
		if !s.cfg.ManageEdit() {
			http.Error(w, "text editing disabled", http.StatusForbidden)
			return
		}

		baseName := filepath.Base(vpath)
		if s.indexer.Excluded(baseName, false) {
			logger.Warn("server: edit blocked on excluded file: %s", baseName)
			http.Error(w, "editing excluded or sensitive file is blocked", http.StatusForbidden)
			return
		}

		const maxWriteSize = 10 * 1024 * 1024 // 10MB limit
		r.Body = http.MaxBytesReader(w, r.Body, maxWriteSize)
		data, err := io.ReadAll(r.Body)
		if err != nil {
			http.Error(w, "read request body error: "+err.Error(), http.StatusBadRequest)
			return
		}

		dir := filepath.Dir(realPath)
		tmpFile := filepath.Join(dir, fmt.Sprintf(".tmp.%d.%s", time.Now().UnixNano(), baseName))
		if err := os.WriteFile(tmpFile, data, 0644); err != nil {
			http.Error(w, "cannot write temporary file: "+err.Error(), http.StatusInternalServerError)
			return
		}

		if err := os.Rename(tmpFile, realPath); err != nil {
			os.Remove(tmpFile)
			logger.Error("server: failed atomic save for %s: %v", realPath, err)
			http.Error(w, "cannot save file: "+err.Error(), http.StatusInternalServerError)
			return
		}

		logger.Info("server: saved text file %s (%d bytes)", vpath, len(data))
		writeJSON(w, map[string]any{
			"success": true,
			"path":    vpath,
			"size":    len(data),
		})

	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

// handleMkdir creates a new subdirectory inside a virtual path.
func (s *Server) handleMkdir(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeJSONError(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if !s.cfg.ManageMkdir() {
		writeJSONError(w, "mkdir disabled", http.StatusForbidden)
		return
	}

	vpath := r.URL.Query().Get("path")
	name := r.URL.Query().Get("name")
	if strings.Contains(r.Header.Get("Content-Type"), "application/json") {
		var req struct {
			Path string `json:"path"`
			Name string `json:"name"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err == nil {
			if vpath == "" {
				vpath = req.Path
			}
			if name == "" {
				name = req.Name
			}
		}
	}
	if name == "" {
		name = r.FormValue("name")
	}

	if vpath == "" || vpath == "/" {
		writeJSONError(w, "cannot create directory in root view, please select a mount", http.StatusBadRequest)
		return
	}

	cleanName := sanitizeFilename(name)
	if cleanName == "" {
		writeJSONError(w, "invalid directory name", http.StatusBadRequest)
		return
	}

	parentReal, ok := s.indexer.MapVirtualToReal(vpath)
	if !ok {
		writeJSONError(w, "destination path not found", http.StatusNotFound)
		return
	}

	info, err := os.Stat(parentReal)
	if err != nil || !info.IsDir() {
		writeJSONError(w, "destination parent is not a directory", http.StatusBadRequest)
		return
	}

	targetReal := filepath.Join(parentReal, cleanName)
	if !s.cfg.Security.AllowOutsideSymlinks {
		if root, ok := s.indexer.RootFor(vpath); ok && !withinRoot(targetReal, root.Path) {
			logger.Warn("server: mkdir blocked outside root: %s", targetReal)
			writeJSONError(w, "forbidden: target outside root", http.StatusForbidden)
			return
		}
	}

	if _, err := os.Stat(targetReal); err == nil {
		writeJSONError(w, "directory or file already exists", http.StatusConflict)
		return
	}

	if err := os.Mkdir(targetReal, 0755); err != nil {
		logger.Error("server: failed mkdir %s: %v", targetReal, err)
		writeJSONError(w, "failed to create directory: "+err.Error(), http.StatusInternalServerError)
		return
	}

	newVpath := strings.TrimSuffix(vpath, "/") + "/" + cleanName
	logger.Info("server: created directory %s", newVpath)
	writeJSON(w, map[string]any{
		"success": true,
		"name":    cleanName,
		"path":    newVpath,
	})
}

// handleRename renames a file or directory within its parent directory.
func (s *Server) handleRename(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeJSONError(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if !s.cfg.ManageRename() {
		writeJSONError(w, "rename disabled", http.StatusForbidden)
		return
	}

	vpath := r.URL.Query().Get("path")
	newName := r.URL.Query().Get("new_name")
	if strings.Contains(r.Header.Get("Content-Type"), "application/json") {
		var req struct {
			Path    string `json:"path"`
			NewName string `json:"newName"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err == nil {
			if vpath == "" {
				vpath = req.Path
			}
			if newName == "" {
				newName = req.NewName
			}
		}
	}
	if newName == "" {
		newName = r.FormValue("new_name")
	}
	if newName == "" {
		newName = r.FormValue("newName")
	}

	if vpath == "" || vpath == "/" {
		writeJSONError(w, "cannot rename root view", http.StatusBadRequest)
		return
	}
	for _, root := range s.cfg.Roots {
		if vpath == root.URL || vpath == root.URL+"/" {
			writeJSONError(w, "cannot rename root mount", http.StatusBadRequest)
			return
		}
	}

	cleanName := sanitizeFilename(newName)
	if cleanName == "" {
		writeJSONError(w, "invalid new name", http.StatusBadRequest)
		return
	}

	oldReal, ok := s.indexer.MapVirtualToReal(vpath)
	if !ok {
		writeJSONError(w, "target not found", http.StatusNotFound)
		return
	}

	parentReal := filepath.Dir(oldReal)
	newReal := filepath.Join(parentReal, cleanName)

	if !s.cfg.Security.AllowOutsideSymlinks {
		if root, ok := s.indexer.RootFor(vpath); ok && !withinRoot(newReal, root.Path) {
			logger.Warn("server: rename blocked outside root: %s", newReal)
			writeJSONError(w, "forbidden: target outside root", http.StatusForbidden)
			return
		}
	}

	if oldReal == newReal {
		writeJSON(w, map[string]any{"success": true, "renamed": false})
		return
	}

	if _, err := os.Stat(newReal); err == nil {
		writeJSONError(w, "destination name already exists", http.StatusConflict)
		return
	}

	if err := os.Rename(oldReal, newReal); err != nil {
		logger.Error("server: failed rename %s -> %s: %v", oldReal, newReal, err)
		writeJSONError(w, "failed to rename: "+err.Error(), http.StatusInternalServerError)
		return
	}

	newVpath := strings.TrimSuffix(filepath.ToSlash(filepath.Dir(vpath)), "/")
	if newVpath == "." || newVpath == "" {
		newVpath = "/" + cleanName
	} else {
		newVpath = newVpath + "/" + cleanName
	}

	logger.Info("server: renamed %s -> %s", vpath, newVpath)
	writeJSON(w, map[string]any{
		"success": true,
		"oldPath": vpath,
		"newPath": newVpath,
		"name":    cleanName,
	})
}

// handleDelete removes a file or directory with deleteToken authentication.
func (s *Server) handleDelete(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost && r.Method != http.MethodDelete {
		writeJSONError(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if !s.cfg.ManageDelete() {
		if !s.cfg.ManageEnabled() || !s.cfg.Manage.AllowDelete {
			writeJSONError(w, "delete disabled", http.StatusForbidden)
			return
		}
		writeJSONError(w, "delete operation locked: deleteToken is not configured", http.StatusForbidden)
		return
	}

	vpath := r.URL.Query().Get("path")
	token := r.Header.Get("X-Delete-Token")
	if strings.Contains(r.Header.Get("Content-Type"), "application/json") {
		var req struct {
			Path  string `json:"path"`
			Token string `json:"token"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err == nil {
			if vpath == "" {
				vpath = req.Path
			}
			if token == "" {
				token = req.Token
			}
		}
	}
	if token == "" {
		token = r.URL.Query().Get("token")
	}
	if token == "" {
		token = r.FormValue("token")
	}

	if vpath == "" || vpath == "/" {
		writeJSONError(w, "cannot delete root view", http.StatusBadRequest)
		return
	}
	for _, root := range s.cfg.Roots {
		if vpath == root.URL || vpath == root.URL+"/" {
			writeJSONError(w, "cannot delete root mount", http.StatusBadRequest)
			return
		}
	}

	expectedToken := s.cfg.ManageDeleteToken()
	if subtle.ConstantTimeCompare([]byte(token), []byte(expectedToken)) != 1 {
		logger.Warn("server: delete rejected: invalid token for %s", vpath)
		writeJSONError(w, "forbidden: invalid delete token", http.StatusForbidden)
		return
	}

	realPath, ok := s.indexer.MapVirtualToReal(vpath)
	if !ok {
		writeJSONError(w, "target not found", http.StatusNotFound)
		return
	}

	if !s.cfg.Security.AllowOutsideSymlinks {
		if root, ok := s.indexer.RootFor(vpath); ok && !withinRoot(realPath, root.Path) {
			logger.Warn("server: delete blocked outside root: %s", realPath)
			writeJSONError(w, "forbidden: target outside root", http.StatusForbidden)
			return
		}
	}

	info, err := os.Lstat(realPath)
	if err != nil {
		if os.IsNotExist(err) {
			writeJSONError(w, "target not found", http.StatusNotFound)
		} else {
			writeJSONError(w, err.Error(), http.StatusInternalServerError)
		}
		return
	}

	if info.IsDir() {
		recursive := r.URL.Query().Get("recursive") == "true" || r.FormValue("recursive") == "true"
		if recursive {
			err = os.RemoveAll(realPath)
		} else {
			err = os.Remove(realPath)
		}
	} else {
		err = os.Remove(realPath)
	}

	if err != nil {
		logger.Error("server: failed delete %s: %v", realPath, err)
		writeJSONError(w, "failed to delete: "+err.Error(), http.StatusInternalServerError)
		return
	}

	logger.Info("server: deleted %s", vpath)
	writeJSON(w, map[string]any{
		"success": true,
		"deleted": vpath,
	})
}

// handleZip packages a directory and streams it as a ZIP archive on-the-fly.
func (s *Server) handleZip(w http.ResponseWriter, r *http.Request) {
	vpath := r.URL.Query().Get("path")
	if vpath == "" || vpath == "/" {
		writeJSONError(w, "cannot zip entire root view", http.StatusBadRequest)
		return
	}

	realPath, ok := s.indexer.MapVirtualToReal(vpath)
	if !ok {
		writeJSONError(w, "path not found", http.StatusNotFound)
		return
	}

	info, err := os.Stat(realPath)
	if err != nil || !info.IsDir() {
		writeJSONError(w, "target is not a valid directory", http.StatusBadRequest)
		return
	}

	var rootDir string
	if root, ok := s.indexer.RootFor(vpath); ok {
		rootDir = root.Path
	} else {
		rootDir = realPath
	}

	if !s.cfg.Security.AllowOutsideSymlinks && !withinRoot(realPath, rootDir) {
		logger.Warn("server: zip blocked outside root: %s", realPath)
		writeJSONError(w, "forbidden: target outside root", http.StatusForbidden)
		return
	}

	dirName := path.Base(vpath)
	if dirName == "" || dirName == "." || dirName == "/" {
		dirName = filepath.Base(realPath)
	}
	if dirName == "." || dirName == "/" || dirName == "\\" {
		dirName = "archive"
	}

	w.Header().Set("Content-Type", "application/zip")
	w.Header().Set("Content-Disposition", contentDisposition(dirName+".zip"))
	w.Header().Set("Cache-Control", "no-cache")

	zw := zip.NewWriter(w)
	defer zw.Close()

	_ = filepath.Walk(realPath, func(curPath string, curInfo os.FileInfo, walkErr error) error {
		if walkErr != nil {
			return nil
		}

		rel, err := filepath.Rel(realPath, curPath)
		if err != nil || rel == "." {
			return nil
		}
		rel = filepath.ToSlash(rel)

		if !s.cfg.Security.AllowOutsideSymlinks && !withinRoot(curPath, rootDir) {
			if curInfo.IsDir() {
				return filepath.SkipDir
			}
			return nil
		}

		name := curInfo.Name()
		if curInfo.IsDir() {
			if s.indexer.Excluded(name, true) {
				return filepath.SkipDir
			}
			header, err := zip.FileInfoHeader(curInfo)
			if err != nil {
				return nil
			}
			header.Name = rel + "/"
			_, _ = zw.CreateHeader(header)
			return nil
		}

		if s.indexer.Excluded(name, false) {
			return nil
		}

		header, err := zip.FileInfoHeader(curInfo)
		if err != nil {
			return nil
		}
		header.Name = rel
		header.Method = zip.Deflate

		writer, err := zw.CreateHeader(header)
		if err != nil {
			return nil
		}

		file, err := os.Open(curPath)
		if err != nil {
			return nil
		}
		defer file.Close()

		_, _ = io.Copy(writer, file)
		return nil
	})
}


