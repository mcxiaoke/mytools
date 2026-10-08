package main

import (
	"fmt"
	"os"
	"path"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// Entry represents a single indexed file or directory.
type Entry struct {
	Name    string    `json:"name"`
	Path    string    `json:"path"`    // virtual URL path, e.g. /data/sub/file.txt
	Size    int64     `json:"size"`    // file size in bytes
	ModTime time.Time `json:"modTime"` // modification time
	IsDir   bool      `json:"isDir"`   // true if directory

	// MatchType is set only on search results: "name" means the entry's
	// own name matched the query, "path" means only its full path did
	// (path matches are returned for directories only).
	MatchType string `json:"matchType,omitempty"`

	// internal fields, not serialized to JSON or disk
	gen   uint64 // generation stamp used by incremental GC
	lname string // lower-cased name, for search
	lpath string // lower-cased path, for search
}

// scanProgressInterval is how often a long-running scan logs its progress.
const scanProgressInterval = 10 * time.Second

// scanProgress accumulates scan counters for periodic progress logging.
// A scan runs in a single goroutine, so no synchronization is needed.
type scanProgress struct {
	start   time.Time
	lastLog time.Time
	dirs    int
	entries int
	current string // real path of the directory currently being walked
}

// buildResult reports what a single index pass did.
type buildResult struct {
	Added    int           `json:"added"`
	Changed  int           `json:"changed"`
	Removed  int           `json:"removed"`
	Skipped  int           `json:"skipped"` // subtrees skipped because their directory stamp was unchanged
	Duration time.Duration `json:"durationMs"`
}

// Indexer coordinates the in-memory compact index of all configured directories
// and background re-indexing with disk persistence.
//
// Concurrency Model:
//   - Searches read the immutable snapshot via atomic.Pointer[CompactIndex] with ZERO locks.
//   - Background scanning builds a new CompactIndex independently.
//   - A short atomic pointer swap updates the active index.
type Indexer struct {
	mu      sync.Mutex
	compact atomic.Pointer[CompactIndex]

	cfg          *Config
	persistPath  string
	interval     time.Duration
	fullInterval time.Duration // 0 = periodic full rebuild disabled
	lastFull     time.Time     // when the last full (non-incremental) build ran
	stopCh       chan struct{}
	building     bool // true while index is being (re)built
	lastBuild    time.Time
	lastResult   buildResult
	indexSaved   bool // true once the current index has been persisted at least once
}

// NewIndexer creates a new indexer, optionally loading a persisted compact index.
func NewIndexer(cfg *Config) *Indexer {
	interval, err := time.ParseDuration(cfg.Index.Interval)
	if err != nil {
		logger.Warn("indexer: invalid interval %q, using 5m", cfg.Index.Interval)
		interval = 5 * time.Minute
	}

	fullInterval := time.Duration(0)
	if cfg.Index.FullInterval != "" {
		if d, perr := time.ParseDuration(cfg.Index.FullInterval); perr != nil {
			logger.Warn("indexer: invalid fullInterval %q, periodic full rebuild disabled", cfg.Index.FullInterval)
		} else if d > 0 {
			fullInterval = d
		}
	}

	idx := &Indexer{
		cfg:          cfg,
		persistPath:  cfg.IndexPath(),
		interval:     interval,
		fullInterval: fullInterval,
		stopCh:       make(chan struct{}),
	}

	// try loading persisted compact index for fast startup
	if idx.persistPath != "" {
		if loaded, err := LoadCompactIndex(idx.persistPath, cfg.Roots); err != nil {
			logger.Warn("indexer: load persisted index: %v (will re-build)", err)
		} else {
			idx.compact.Store(loaded)
			idx.indexSaved = true
			logger.Info("indexer: loaded %d entries from persisted database (%s)", loaded.TotalEntries(), idx.persistPath)
			if fi, serr := os.Stat(idx.persistPath); serr == nil {
				idx.lastFull = fi.ModTime()
			}
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

// RootFor returns the root mapping that contains the given virtual path.
func (idx *Indexer) RootFor(vpath string) (RootMapping, bool) {
	vpath = path.Clean("/" + strings.TrimPrefix(vpath, "/"))
	for _, r := range idx.cfg.Roots {
		if vpath == r.URL || strings.HasPrefix(vpath, r.URL+"/") {
			return r, true
		}
	}
	return RootMapping{}, false
}

// mapRealToVirtual converts a real disk path under a root to its virtual URL path.
func mapRealToVirtual(rootURL, rootPath, realPath string) string {
	rel, err := filepath.Rel(rootPath, realPath)
	if err != nil || rel == "." {
		return rootURL
	}
	return path.Join(rootURL, filepath.ToSlash(rel))
}

// matchName reports whether name matches pattern, case-insensitively.
// Patterns containing "*" or "?" use filepath.Match semantics.
func matchName(pattern, name string) bool {
	p := strings.ToLower(pattern)
	n := strings.ToLower(name)
	if strings.ContainsAny(p, "*?[") {
		ok, err := filepath.Match(p, n)
		return err == nil && ok
	}
	return p == n
}

// Excluded reports whether a directory or file name is excluded by config.
func (idx *Indexer) Excluded(name string, isDir bool) bool {
	if isDir {
		for _, d := range idx.cfg.Index.ExcludeDirs {
			if matchName(d, name) {
				return true
			}
		}
		return false
	}
	for _, f := range idx.cfg.Index.ExcludeFiles {
		if matchName(f, name) {
			return true
		}
	}
	return false
}

// BuildIndex refreshes the in-memory index using the incremental strategy
// (or a full rebuild when index.incremental is false, the index is empty,
// or forceFull is set).
// Searches continue to operate lock-free during building; only the final pointer
// swap replaces the active snapshot.
func (idx *Indexer) BuildIndex(forceFull bool) buildResult {
	start := time.Now()

	idx.mu.Lock()
	if idx.building {
		idx.mu.Unlock()
		return idx.lastResult
	}
	idx.building = true
	idx.mu.Unlock()

	defer func() {
		idx.mu.Lock()
		idx.building = false
		idx.mu.Unlock()
	}()

	oldCompact := idx.compact.Load()
	incremental := idx.cfg.IndexIncremental() && oldCompact != nil && len(oldCompact.Folders) > 0 && !forceFull

	switch {
	case forceFull && idx.cfg.IndexIncremental():
		logger.Info("indexer: starting forced full scan of %d root(s) (index.fullInterval %s reached)",
			len(idx.cfg.Roots), idx.fullInterval)
	case incremental:
		logger.Info("indexer: starting incremental scan of %d root(s)", len(idx.cfg.Roots))
	default:
		logger.Info("indexer: starting full scan of %d root(s)", len(idx.cfg.Roots))
	}

	newCompact, res, walkErrs := buildCompactPass(idx.cfg, oldCompact, incremental)

	idx.compact.Store(newCompact)

	idx.mu.Lock()
	idx.lastBuild = time.Now()
	if !incremental {
		idx.lastFull = time.Now()
	}
	res.Duration = time.Since(start)
	idx.lastResult = res
	total := newCompact.TotalEntries()
	idx.mu.Unlock()

	for _, e := range walkErrs {
		logger.Warn("indexer: %v", e)
	}
	logger.Info("indexer: %d entries (+%d ~%d -%d, skipped %d subtrees) in %v%s",
		total, res.Added, res.Changed, res.Removed, res.Skipped, res.Duration,
		func() string {
			if incremental {
				return ""
			}
			return " [full]"
		}())

	if idx.persistPath != "" && (res.Added+res.Changed+res.Removed > 0 || !idx.indexSaved) {
		if err := idx.save(); err != nil {
			logger.Error("indexer: persist failed: %v", err)
		}
	}

	return res
}

// Search queries the compact index and returns matching entries scored and sorted.
// Operates completely lock-free on the immutable snapshot.
func (idx *Indexer) Search(query string, limit int) []Entry {
	c := idx.compact.Load()
	if c == nil {
		return nil
	}
	return c.Search(query, limit)
}

// ListDir returns the immediate contents of a directory at the given virtual path.
// This reads from the filesystem in real-time (not the index), applying the
// same exclusion rules as the index so listing and search stay consistent.
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
		if idx.Excluded(de.Name(), de.IsDir()) {
			continue
		}
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

// fullDue reports whether a forced full rebuild is overdue.
func (idx *Indexer) fullDue() bool {
	if idx.fullInterval <= 0 {
		return false
	}
	idx.mu.Lock()
	defer idx.mu.Unlock()
	return time.Since(idx.lastFull) >= idx.fullInterval
}

// Start launches the background re-indexing goroutine.
func (idx *Indexer) Start() {
	go func() {
		idx.BuildIndex(idx.fullDue())

		ticker := time.NewTicker(idx.interval)
		defer ticker.Stop()

		for {
			select {
			case <-ticker.C:
				idx.BuildIndex(idx.fullDue())
			case <-idx.stopCh:
				return
			}
		}
	}()
}

// Stop signals the background indexer to stop.
func (idx *Indexer) Stop() {
	select {
	case <-idx.stopCh:
		// already stopped
	default:
		close(idx.stopCh)
	}
}

// IsBuilding returns whether the index is currently being (re)built.
func (idx *Indexer) IsBuilding() bool {
	idx.mu.Lock()
	defer idx.mu.Unlock()
	return idx.building
}

// Stats returns index statistics for the /api/stats endpoint.
func (idx *Indexer) Stats() map[string]any {
	c := idx.compact.Load()
	indexed := 0
	if c != nil {
		indexed = c.TotalEntries()
	}
	idx.mu.Lock()
	building := idx.building
	lastBuild := idx.lastBuild
	lastPass := idx.lastResult
	idx.mu.Unlock()

	return map[string]any{
		"indexed":     indexed,
		"building":    building,
		"lastBuild":   lastBuild,
		"incremental": idx.cfg.IndexIncremental(),
		"lastPass":    lastPass,
	}
}

// TotalEntries returns the total number of indexed entries.
func (idx *Indexer) TotalEntries() int {
	c := idx.compact.Load()
	if c == nil {
		return 0
	}
	return c.TotalEntries()
}

// AllEntries materializes and returns all entries as a map of virtual path -> Entry.
func (idx *Indexer) AllEntries() map[string]Entry {
	c := idx.compact.Load()
	if c == nil {
		return nil
	}
	return c.AllEntries()
}

// save persists the current index to disk using binary FLIX format (Raw uncompressed mode by default).
func (idx *Indexer) save() error {
	c := idx.compact.Load()
	if c == nil {
		return nil
	}
	if err := c.Save(idx.persistPath, idx.cfg.Roots, false); err != nil {
		return err
	}
	idx.mu.Lock()
	idx.indexSaved = true
	idx.mu.Unlock()
	return nil
}

// load restores a previously persisted index from disk.
func (idx *Indexer) load() error {
	loaded, err := LoadCompactIndex(idx.persistPath, idx.cfg.Roots)
	if err != nil {
		return err
	}
	idx.compact.Store(loaded)
	idx.mu.Lock()
	idx.indexSaved = true
	idx.mu.Unlock()
	logger.Info("indexer: loaded %d entries from persisted database", loaded.TotalEntries())
	return nil
}
