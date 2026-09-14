package main

import (
	"bytes"
	"embed"
	"fmt"
	"io"
	"io/fs"
	"net/http"
	"os"
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

