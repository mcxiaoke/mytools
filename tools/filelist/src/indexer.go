package main

import (
	"encoding/gob"
	"fmt"
	"os"
	"path"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

// Entry represents a single indexed file or directory.
type Entry struct {
	Name    string    `json:"name"`
	Path    string    `json:"path"`    // virtual URL path, e.g. /data/sub/file.txt
	Size    int64     `json:"size"`    // file size in bytes
	ModTime time.Time `json:"modTime"` // modification time
	IsDir   bool      `json:"isDir"`   // true if directory
}

// Indexer maintains an in-memory index of all configured directories
// and supports background re-indexing with disk persistence.
type Indexer struct {
	mu          sync.RWMutex
	entries     []Entry
	cfg         *Config
	persistPath string
	interval    time.Duration
	stopCh      chan struct{}
	building    bool // true while index is being (re)built
}

// NewIndexer creates a new indexer, optionally loading a persisted index.
func NewIndexer(cfg *Config) *Indexer {
	interval, err := time.ParseDuration(cfg.Index.Interval)
	if err != nil {
		logger.Warn("indexer: invalid interval %q, using 5m", cfg.Index.Interval)
		interval = 5 * time.Minute
	}

	idx := &Indexer{
		cfg:         cfg,
		persistPath: cfg.Index.Persist,
		interval:    interval,
		stopCh:      make(chan struct{}),
	}

	// try loading persisted index for fast startup
	if cfg.Index.Persist != "" {
		if err := idx.load(); err != nil {
			logger.Warn("indexer: load persisted index: %v (will re-build)", err)
		}
	}

	return idx
}

// MapVirtualToReal converts a virtual URL path to a real disk path.
// Returns the real path and true if a matching root was found.
// Each root URL must be a subpath like /data — root "/" is not allowed
// (rejected at config load time). Matching is a simple longest-prefix lookup.
func (idx *Indexer) MapVirtualToReal(vpath string) (string, bool) {
	// clean the path to prevent traversal attacks
	vpath = path.Clean("/" + strings.TrimPrefix(vpath, "/"))

	for _, r := range idx.cfg.Roots {
		if vpath == r.URL {
			return r.Path, true
		}
		rel := strings.TrimPrefix(vpath, r.URL+"/")
		if rel != vpath { // means vpath starts with r.URL+"/"
			return filepath.Join(r.Path, filepath.FromSlash(rel)), true
		}
	}

	return "", false
}

// mapRealToVirtual converts a real disk path under a root to its virtual URL path.
func mapRealToVirtual(rootURL, rootPath, realPath string) string {
	rel, err := filepath.Rel(rootPath, realPath)
	if err != nil || rel == "." {
		return rootURL
	}
	return path.Join(rootURL, filepath.ToSlash(rel))
}

// BuildIndex walks all configured roots and rebuilds the in-memory index.
func (idx *Indexer) BuildIndex() {
	start := time.Now()

	idx.mu.Lock()
	idx.building = true
	idx.mu.Unlock()

	var entries []Entry
	for _, r := range idx.cfg.Roots {
		entries = append(entries, idx.walkRoot(r)...)
	}

	idx.mu.Lock()
	idx.entries = entries
	idx.building = false
	idx.mu.Unlock()

	logger.Info("indexer: indexed %d entries in %v", len(entries), time.Since(start))

	if idx.persistPath != "" {
		if err := idx.save(); err != nil {
			logger.Error("indexer: persist failed: %v", err)
		}
	}
}

// walkRoot recursively walks a single root directory and returns all entries.
func (idx *Indexer) walkRoot(r RootMapping) []Entry {
	info, err := os.Stat(r.Path)
	if err != nil {
		logger.Warn("indexer: cannot access %s: %v", r.Path, err)
		return nil
	}
	if !info.IsDir() {
		logger.Warn("indexer: %s is not a directory", r.Path)
		return nil
	}

	// build exclude set
	excludeSet := make(map[string]bool)
	for _, d := range idx.cfg.Index.ExcludeDirs {
		excludeSet[strings.ToLower(d)] = true
	}

	var entries []Entry
	_ = filepath.WalkDir(r.Path, func(realPath string, d os.DirEntry, err error) error {
		if err != nil || realPath == r.Path {
			return nil
		}
		// skip excluded directories
		if d.IsDir() && excludeSet[strings.ToLower(d.Name())] {
			return filepath.SkipDir
		}
		fi, err := d.Info()
		if err != nil {
			return nil
		}
		vpath := mapRealToVirtual(r.URL, r.Path, realPath)
		entries = append(entries, Entry{
			Name:    filepath.Base(realPath),
			Path:    vpath,
			Size:    fi.Size(),
			ModTime: fi.ModTime(),
			IsDir:   d.IsDir(),
		})
		return nil
	})
	return entries
}

// Search queries the index for entries matching the given substring.
// Results are scored and sorted by relevance.
func (idx *Indexer) Search(query string, limit int) []Entry {
	query = strings.ToLower(strings.TrimSpace(query))
	if query == "" {
		return nil
	}

	idx.mu.RLock()
	defer idx.mu.RUnlock()

	type scored struct {
		entry Entry
		score int
	}

	var results []scored
	for _, e := range idx.entries {
		nameLower := strings.ToLower(e.Name)
		pathLower := strings.ToLower(e.Path)

		var score int
		switch {
		case nameLower == query:
			score = 100
		case strings.HasPrefix(nameLower, query):
			score = 80
		case strings.Contains(nameLower, query):
			score = 60
		case strings.Contains(pathLower, query):
			score = 40
		default:
			continue
		}
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

// ListDir returns the immediate contents of a directory at the given virtual path.
// This reads from the filesystem in real-time (not the index).
func (idx *Indexer) ListDir(vpath string) ([]Entry, error) {
	vpath = path.Clean("/" + strings.TrimPrefix(vpath, "/"))
	realPath, ok := idx.MapVirtualToReal(vpath)
	if !ok {
		return nil, fmt.Errorf("path not found: %s", vpath)
	}

	dirEntries, err := os.ReadDir(realPath)
	if err != nil {
		return nil, err
	}

	var entries []Entry
	for _, de := range dirEntries {
		fi, err := de.Info()
		if err != nil {
			continue
		}
		childPath := path.Join(vpath, de.Name())
		entries = append(entries, Entry{
			Name:    de.Name(),
			Path:    childPath,
			Size:    fi.Size(),
			ModTime: fi.ModTime(),
			IsDir:   de.IsDir(),
		})
	}

	// sort: directories first, then files, alphabetically (case-insensitive)
	sort.Slice(entries, func(i, j int) bool {
		if entries[i].IsDir != entries[j].IsDir {
			return entries[i].IsDir
		}
		return strings.ToLower(entries[i].Name) < strings.ToLower(entries[j].Name)
	})

	return entries, nil
}

// Start launches the background re-indexing goroutine.
func (idx *Indexer) Start() {
	go func() {
		idx.BuildIndex()

		ticker := time.NewTicker(idx.interval)
		defer ticker.Stop()

		for {
			select {
			case <-ticker.C:
				idx.BuildIndex()
			case <-idx.stopCh:
				return
			}
		}
	}()
}

// Stop signals the background indexer to stop.
func (idx *Indexer) Stop() {
	close(idx.stopCh)
}

// IsBuilding returns whether the index is currently being (re)built.
func (idx *Indexer) IsBuilding() bool {
	idx.mu.RLock()
	defer idx.mu.RUnlock()
	return idx.building
}

// save persists the current index to disk using gob encoding.
func (idx *Indexer) save() error {
	idx.mu.RLock()
	entries := idx.entries
	idx.mu.RUnlock()

	f, err := os.Create(idx.persistPath)
	if err != nil {
		return err
	}
	defer f.Close()

	return gob.NewEncoder(f).Encode(entries)
}

// load restores a previously persisted index from disk.
func (idx *Indexer) load() error {
	f, err := os.Open(idx.persistPath)
	if err != nil {
		return err
	}
	defer f.Close()

	var entries []Entry
	if err := gob.NewDecoder(f).Decode(&entries); err != nil {
		return err
	}

	idx.mu.Lock()
	idx.entries = entries
	idx.mu.Unlock()

	logger.Info("indexer: loaded %d entries from persisted index", len(entries))
	return nil
}

// Stats returns the current index size.
func (idx *Indexer) Stats() int {
	idx.mu.RLock()
	defer idx.mu.RUnlock()
	return len(idx.entries)
}
