package main

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"
)

//go:embed web/index.html
var indexHTML []byte

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

// Routes returns the configured HTTP mux.
func (s *Server) Routes() *http.ServeMux {
	mux := http.NewServeMux()
	mux.HandleFunc("/", s.handleIndex)
	mux.HandleFunc("/favicon.ico", s.handleFavicon)
	mux.HandleFunc("/api/roots", s.handleRoots)
	mux.HandleFunc("/api/list", s.handleList)
	mux.HandleFunc("/api/search", s.handleSearch)
	mux.HandleFunc("/api/stats", s.handleStats)
	mux.HandleFunc("/raw/", s.handleRaw)
	return mux
}

// handleIndex serves the embedded SPA for all non-API, non-raw paths.
func (s *Server) handleIndex(w http.ResponseWriter, r *http.Request) {
	if strings.HasPrefix(r.URL.Path, "/api/") || strings.HasPrefix(r.URL.Path, "/raw/") {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-cache")
	w.Write(indexHTML)
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
		logger.Warn("server: list %s: %v", p, err)
		http.Error(w, "not found", http.StatusNotFound)
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
	writeJSON(w, map[string]any{
		"indexed":  s.indexer.Stats(),
		"building": s.indexer.IsBuilding(),
	})
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

	info, err := os.Stat(realPath)
	if err != nil || info.IsDir() {
		http.NotFound(w, r)
		return
	}

	// set download header if requested (RFC 5987 encoded for non-ASCII)
	if r.URL.Query().Get("download") == "1" {
		w.Header().Set("Content-Disposition", contentDisposition(filepath.Base(realPath)))
	}

	http.ServeFile(w, r, realPath)
}

// contentDisposition builds a Content-Disposition header value with
// RFC 5987 encoding for non-ASCII filenames.
func contentDisposition(filename string) string {
	encoded := url.PathEscape(filename)
	return fmt.Sprintf(`attachment; filename*=UTF-8''%s`, encoded)
}

// writeJSON encodes v as JSON and writes it to the response.
func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	if err := json.NewEncoder(w).Encode(v); err != nil {
		logger.Error("server: json encode: %v", err)
	}
}

// loggingMiddleware wraps a handler with request logging.
func loggingMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		next.ServeHTTP(w, r)
		logger.Debug("%s %s %s %v", r.Method, r.URL.Path, r.RemoteAddr, time.Since(start))
	})
}
