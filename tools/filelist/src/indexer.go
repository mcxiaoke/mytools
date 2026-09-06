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

	// internal fields, not serialized to JSON or disk
	gen   uint64 // generation stamp used by incremental GC
	lname string // lower-cased name, for search
	lpath string // lower-cased path, for search
}

// stamp is the change signature of a directory: modification time + size.
// A directory whose stamp is unchanged is assumed to have an unchanged subtree.
type stamp struct {
	Mod  time.Time
	Size int64
	Gen  uint64
}

// persistVersion bumps whenever the on-disk layout changes; older files are
// discarded and rebuilt instead of being misread.
const persistVersion = 2

// persistData is the on-disk index cache.
type persistData struct {
	Version int
	Entries map[string]Entry
	Dirs    map[string]stamp
}

// buildResult reports what a single index pass did.
type buildResult struct {
	Added    int           `json:"added"`
	Changed  int           `json:"changed"`
	Removed  int           `json:"removed"`
	Skipped  int           `json:"skipped"` // subtrees skipped because their directory stamp was unchanged
	Duration time.Duration `json:"durationMs"`
}

// Indexer maintains an in-memory index of all configured directories
// and supports background re-indexing with disk persistence.
//
// Incremental strategy:
//   - every entry/directory carries a generation stamp;
//   - a directory whose (mtime, size) is unchanged has its whole subtree
//     skipped — no ReadDir, no stat of the files inside;
//   - files whose (mtime, size) are unchanged reuse the existing entry;
//   - entries not seen in this generation and not under a skipped subtree
//     are dropped.
//
// When nothing changed, a pass costs one stat per root and nothing else.
type Indexer struct {
	mu       sync.RWMutex
	entries  map[string]Entry // virtual path -> entry
	dirStamp map[string]stamp // virtual path -> directory signature
	gen      uint64

	cfg         *Config
	persistPath string
	interval    time.Duration
	stopCh      chan struct{}
	building    bool // true while index is being (re)built
	lastBuild   time.Time
	lastResult  buildResult
	indexSaved  bool // true once the current index has been persisted at least once
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
		entries:     make(map[string]Entry),
		dirStamp:    make(map[string]stamp),
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
// (or a full rebuild when index.incremental is false or the index is empty).
// Scanning runs under a read lock so searches are not blocked; only the final
// merge takes the write lock briefly.
func (idx *Indexer) BuildIndex() buildResult {
	start := time.Now()

	idx.mu.Lock()
	if idx.building {
		idx.mu.Unlock()
		return idx.lastResult
	}
	idx.building = true
	idx.gen++
	gen := idx.gen
	incremental := idx.cfg.IndexIncremental() && len(idx.entries) > 0
	idx.mu.Unlock()

	defer func() {
		idx.mu.Lock()
		idx.building = false
		idx.mu.Unlock()
	}()

	// ---- scan phase (read lock: searches keep working) ----
	idx.mu.RLock()
	updates, touched, retained, res, walkErrs := idx.scan(gen, incremental)
	idx.mu.RUnlock()

	// ---- merge phase (write lock, short) ----
	idx.mu.Lock()
	for p, e := range updates {
		e.gen = gen
		idx.entries[p] = e
	}
	for p, st := range touched {
		st.Gen = gen
		idx.dirStamp[p] = st
	}
	if !incremental {
		// full rebuild: drop everything this pass did not see
		for p := range idx.entries {
			if idx.entries[p].gen != gen {
				delete(idx.entries, p)
				res.Removed++
			}
		}
		for p := range idx.dirStamp {
			if idx.dirStamp[p].Gen != gen {
				delete(idx.dirStamp, p)
			}
		}
	} else {
		for p, e := range idx.entries {
			if e.gen == gen {
				continue
			}
			if hasRetainedAncestor(p, retained) {
				continue
			}
			delete(idx.entries, p)
			res.Removed++
		}
		for p, st := range idx.dirStamp {
			if st.Gen == gen || hasRetainedAncestor(p, retained) {
				continue
			}
			delete(idx.dirStamp, p)
		}
	}
	idx.lastBuild = time.Now()
	res.Duration = time.Since(start)
	idx.lastResult = res
	total := len(idx.entries)
	incrementalFlag := incremental
	idx.mu.Unlock()

	for _, e := range walkErrs {
		logger.Warn("indexer: %v", e)
	}
	logger.Info("indexer: %d entries (+%d ~%d -%d, skipped %d subtrees) in %v%s",
		total, res.Added, res.Changed, res.Removed, res.Skipped, res.Duration,
		func() string {
			if incrementalFlag {
				return ""
			}
			return " [full]"
		}())

	if idx.persistPath != "" && (res.Added+res.Changed+res.Removed > 0 || idx.indexSaved == false) {
		if err := idx.save(); err != nil {
			logger.Error("indexer: persist failed: %v", err)
		}
	}

	return res
}

// scan walks the configured roots and returns the diff to apply.
// It must be called with at least the read lock held.
func (idx *Indexer) scan(gen uint64, incremental bool) (map[string]Entry, map[string]stamp, map[string]bool, buildResult, []error) {
	updates := make(map[string]Entry)
	touched := make(map[string]stamp)
	retained := make(map[string]bool)
	var res buildResult
	var errs []error

	for _, r := range idx.cfg.Roots {
		info, err := os.Stat(r.Path)
		if err != nil {
			errs = append(errs, fmt.Errorf("cannot access %s: %w", r.Path, err))
			continue
		}
		if !info.IsDir() {
			errs = append(errs, fmt.Errorf("%s is not a directory", r.Path))
			continue
		}

		st := stamp{Mod: info.ModTime(), Size: info.Size()}
		if incremental && idx.dirStamp[r.URL].same(st) {
			// whole root untouched — reuse everything under it
			retained[r.URL] = true
			res.Skipped++
			continue
		}
		touched[r.URL] = st
		idx.walkRoot(r, r.URL, 0, gen, incremental, updates, touched, retained, &res, &errs)
	}

	return updates, touched, retained, res, errs
}

// walkRoot recursively walks a directory, recording changes into updates.
// depth is the depth of dir itself (root = 0).
func (idx *Indexer) walkRoot(r RootMapping, vdir string, depth int, gen uint64, incremental bool,
	updates map[string]Entry, touched map[string]stamp, retained map[string]bool,
	res *buildResult, errs *[]error) {

	realDir, ok := idx.MapVirtualToReal(vdir)
	if !ok {
		return
	}

	items, err := os.ReadDir(realDir)
	if err != nil {
		*errs = append(*errs, fmt.Errorf("read dir %s: %w", realDir, err))
		return
	}

	maxDepth := idx.cfg.Index.MaxDepth
	for _, de := range items {
		name := de.Name()
		if name == "" {
			continue
		}
		vpath := path.Join(vdir, name)

		if de.IsDir() {
			if idx.Excluded(name, true) {
				continue
			}
			fi, err := de.Info()
			if err != nil {
				continue
			}
			st := stamp{Mod: fi.ModTime(), Size: fi.Size()}
			old, existed := idx.entries[vpath]
			if !existed {
				res.Added++
			} else if !old.IsDir || !old.ModTime.Equal(st.Mod) || old.Size != st.Size {
				res.Changed++
			}
			updates[vpath] = newEntry(name, vpath, fi.Size(), fi.ModTime(), true)

			if incremental && idx.dirStamp[vpath].same(st) {
				// subtree unchanged: skip ReadDir/stat for everything below
				retained[vpath] = true
				res.Skipped++
				continue
			}
			if maxDepth > 0 && depth+1 >= maxDepth {
				// depth limit reached: keep the directory entry, do not descend
				continue
			}
			touched[vpath] = st
			idx.walkRoot(r, vpath, depth+1, gen, incremental, updates, touched, retained, res, errs)
			continue
		}

		// Symlinks are reported by ReadDir as non-directories, so we never
		// follow them while walking (no loops, no escaping the root).
		if idx.Excluded(name, false) {
			continue
		}
		fi, err := de.Info()
		if err != nil {
			continue
		}
		mod, size := fi.ModTime(), fi.Size()
		old, existed := idx.entries[vpath]
		switch {
		case !existed:
			res.Added++
			updates[vpath] = newEntry(name, vpath, size, mod, false)
		case old.IsDir || !old.ModTime.Equal(mod) || old.Size != size:
			res.Changed++
			updates[vpath] = newEntry(name, vpath, size, mod, false)
		default:
			// unchanged file: reuse the existing entry, just restamp it
			updates[vpath] = old
		}
	}
}

// newEntry builds an index entry with the search helper fields filled in.
func newEntry(name, vpath string, size int64, mod time.Time, isDir bool) Entry {
	return Entry{
		Name:    name,
		Path:    vpath,
		Size:    size,
		ModTime: mod,
		IsDir:   isDir,
		lname:   strings.ToLower(name),
		lpath:   strings.ToLower(vpath),
	}
}

// same reports whether two directory stamps are equal.
func (s stamp) same(o stamp) bool { return s.Size == o.Size && s.Mod.Equal(o.Mod) }

// hasRetainedAncestor reports whether vpath itself or any of its parent
// directories was skipped as unchanged during this pass.
func hasRetainedAncestor(vpath string, retained map[string]bool) bool {
	if retained[vpath] {
		return true
	}
	for i := len(vpath) - 1; i >= 0; i-- {
		if vpath[i] == '/' {
			if retained[vpath[:i]] {
				return true
			}
		}
	}
	return false
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
		var score int
		switch {
		case e.lname == query:
			score = 100
		case strings.HasPrefix(e.lname, query):
			score = 80
		case strings.Contains(e.lname, query):
			score = 60
		case strings.Contains(e.lpath, query):
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
	select {
	case <-idx.stopCh:
		// already stopped
	default:
		close(idx.stopCh)
	}
}

// IsBuilding returns whether the index is currently being (re)built.
func (idx *Indexer) IsBuilding() bool {
	idx.mu.RLock()
	defer idx.mu.RUnlock()
	return idx.building
}

// Stats returns index statistics for the /api/stats endpoint.
func (idx *Indexer) Stats() map[string]any {
	idx.mu.RLock()
	defer idx.mu.RUnlock()
	return map[string]any{
		"indexed":     len(idx.entries),
		"building":    idx.building,
		"lastBuild":   idx.lastBuild,
		"incremental": idx.cfg.IndexIncremental(),
		"lastPass":    idx.lastResult,
	}
}

// save persists the current index to disk using gob encoding.
// It writes to a temporary file first and renames on success so a crash
// mid-write cannot corrupt an existing cache.
func (idx *Indexer) save() error {
	idx.mu.RLock()
	data := persistData{
		Version: persistVersion,
		Entries: idx.entries,
		Dirs:    idx.dirStamp,
	}
	idx.mu.RUnlock()

	tmp := idx.persistPath + ".tmp"
	f, err := os.Create(tmp)
	if err != nil {
		return err
	}
	if err := gob.NewEncoder(f).Encode(data); err != nil {
		f.Close()
		os.Remove(tmp)
		return err
	}
	if err := f.Close(); err != nil {
		os.Remove(tmp)
		return err
	}
	if err := os.Rename(tmp, idx.persistPath); err != nil {
		os.Remove(tmp)
		return err
	}
	idx.mu.Lock()
	idx.indexSaved = true
	idx.mu.Unlock()
	return nil
}

// load restores a previously persisted index from disk.
func (idx *Indexer) load() error {
	f, err := os.Open(idx.persistPath)
	if err != nil {
		return err
	}
	defer f.Close()

	var data persistData
	if err := gob.NewDecoder(f).Decode(&data); err != nil {
		return fmt.Errorf("decode %s: %w", idx.persistPath, err)
	}
	if data.Version != persistVersion {
		return fmt.Errorf("%s: index format v%d, expected v%d (will rebuild)",
			idx.persistPath, data.Version, persistVersion)
	}

	// rebuild the search helper fields dropped during encoding
	entries := make(map[string]Entry, len(data.Entries))
	for p, e := range data.Entries {
		e.gen = 0
		e.lname = strings.ToLower(e.Name)
		e.lpath = strings.ToLower(e.Path)
		entries[p] = e
	}
	if data.Dirs == nil {
		data.Dirs = make(map[string]stamp)
	}

	idx.mu.Lock()
	idx.entries = entries
	idx.dirStamp = data.Dirs
	idx.indexSaved = true
	idx.mu.Unlock()

	logger.Info("indexer: loaded %d entries from persisted index", len(entries))
	return nil
}
