package main

import (
	"bufio"
	"compress/gzip"
	"encoding/binary"
	"fmt"
	"hash/crc32"
	"hash/fnv"
	"io"
	"os"
	"path"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// CompactFolder represents a directory in the compact index.
// Size: 40 bytes with explicit alignment. Zero pointers (noscan).
type CompactFolder struct {
	ParentID  uint32 // Index in Folders slice, 0xFFFFFFFF indicates root
	NameOff   uint32 // Offset into StringArena.buf (base name)
	NameLen   uint16 // Length of base name in StringArena.buf
	PathLen   uint16 // Length of full virtual path in StringArena.buf
	PathOff   uint32 // Offset into StringArena.buf (full virtual path)
	FileStart uint32 // Starting index in Files slice
	FileCount uint32 // Number of direct child files
	ModTime   int64  // UnixNano modification timestamp
	Size      int64  // Aggregate size or direct size
}

// CompactFile represents a single file in the compact index.
// Size: 32 bytes with explicit alignment. Zero pointers (noscan).
type CompactFile struct {
	FolderID uint32 // Index of parent folder in Folders slice
	NameOff  uint32 // Offset into StringArena.buf
	NameLen  uint16 // Length of name in StringArena.buf
	_        uint16 // Explicit alignment padding
	Size     int64  // File size in bytes
	ModTime  int64  // UnixNano modification timestamp
}

// StringArena stores names contiguously in a single byte slice,
// avoiding per-string heap object overhead.
type StringArena struct {
	buf []byte
}

func (a *StringArena) Append(s string) (uint32, uint16) {
	off := uint32(len(a.buf))
	a.buf = append(a.buf, s...)
	return off, uint16(len(s))
}

func (a *StringArena) AppendBytes(b []byte) (uint32, uint16) {
	off := uint32(len(a.buf))
	a.buf = append(a.buf, b...)
	return off, uint16(len(b))
}

func (a *StringArena) Bytes(off uint32, length uint16) []byte {
	return a.buf[off : off+uint32(length)]
}

func (a *StringArena) String(off uint32, length uint16) string {
	return string(a.buf[off : off+uint32(length)])
}

// CompactIndex is a high-density, cache-friendly in-memory file index.
type CompactIndex struct {
	Folders   []CompactFolder
	Files     []CompactFile
	Arena     StringArena
	folderMap map[string]uint32 // virtual dir path -> FolderID
}

// NewCompactIndex creates an empty CompactIndex.
func NewCompactIndex() *CompactIndex {
	return &CompactIndex{
		Folders:   make([]CompactFolder, 0),
		Files:     make([]CompactFile, 0),
		Arena:     StringArena{buf: make([]byte, 0)},
		folderMap: make(map[string]uint32),
	}
}

// BuildFolderPath reconstructs the virtual URL path of a folder.
func (idx *CompactIndex) BuildFolderPath(folderID uint32) string {
	if folderID >= uint32(len(idx.Folders)) {
		return ""
	}
	f := &idx.Folders[folderID]
	if f.PathLen > 0 {
		return idx.Arena.String(f.PathOff, f.PathLen)
	}

	var segments []string
	curr := folderID
	for curr != 0xFFFFFFFF && curr < uint32(len(idx.Folders)) {
		folder := &idx.Folders[curr]
		name := idx.Arena.String(folder.NameOff, folder.NameLen)
		segments = append(segments, name)
		curr = folder.ParentID
	}

	// Reverse segments to root -> child order
	for i, j := 0, len(segments)-1; i < j; i, j = i+1, j-1 {
		segments[i], segments[j] = segments[j], segments[i]
	}

	// Root segment already starts with /
	if len(segments) == 0 {
		return "/"
	}
	res := segments[0]
	for _, seg := range segments[1:] {
		res = path.Join(res, seg)
	}
	return res
}

// BuildFilePath reconstructs the full virtual URL path of a file.
func (idx *CompactIndex) BuildFilePath(fileID uint32) string {
	if fileID >= uint32(len(idx.Files)) {
		return ""
	}
	f := &idx.Files[fileID]
	dirPath := idx.BuildFolderPath(f.FolderID)
	fileName := idx.Arena.String(f.NameOff, f.NameLen)
	return path.Join(dirPath, fileName)
}

// FindFile locates a file inside a parent folder using binary search on sorted child files.
func (idx *CompactIndex) FindFile(folderID uint32, name string) (*CompactFile, bool) {
	if folderID >= uint32(len(idx.Folders)) {
		return nil, false
	}
	folder := &idx.Folders[folderID]
	if folder.FileCount == 0 {
		return nil, false
	}
	sub := idx.Files[folder.FileStart : folder.FileStart+folder.FileCount]
	i := sort.Search(int(folder.FileCount), func(k int) bool {
		return idx.Arena.String(sub[k].NameOff, sub[k].NameLen) >= name
	})
	if i < int(folder.FileCount) {
		if idx.Arena.String(sub[i].NameOff, sub[i].NameLen) == name {
			return &sub[i], true
		}
	}
	return nil, false
}

// ── Search & Relevance Matching (Zero Allocations on Candidate Stream) ──

func asciiEqualFoldBytes(target []byte, queryLower []byte) bool {
	if len(target) != len(queryLower) {
		return false
	}
	for i := 0; i < len(target); i++ {
		tb := target[i]
		if tb >= 'A' && tb <= 'Z' {
			tb += 'a' - 'A'
		}
		if tb != queryLower[i] {
			return false
		}
	}
	return true
}

func asciiHasPrefixFoldBytes(target []byte, queryLower []byte) bool {
	if len(target) < len(queryLower) {
		return false
	}
	for i := 0; i < len(queryLower); i++ {
		tb := target[i]
		if tb >= 'A' && tb <= 'Z' {
			tb += 'a' - 'A'
		}
		if tb != queryLower[i] {
			return false
		}
	}
	return true
}

func asciiContainsFoldBytes(target []byte, queryLower []byte) bool {
	if len(queryLower) == 0 {
		return true
	}
	if len(target) < len(queryLower) {
		return false
	}
	last := len(target) - len(queryLower)
	for i := 0; i <= last; i++ {
		match := true
		for j := 0; j < len(queryLower); j++ {
			tb := target[i+j]
			if tb >= 'A' && tb <= 'Z' {
				tb += 'a' - 'A'
			}
			if tb != queryLower[j] {
				match = false
				break
			}
		}
		if match {
			return true
		}
	}
	return false
}

type compactSearchResult struct {
	isFolder  bool
	id        uint32
	score     int
	matchType string
	vpath     string
}

// Search searches files and folders matching the query.
// Scoring semantics are identical to Indexer.Search:
//   - Exact name match: 100
//   - Prefix name match: 80
//   - Contains name match: 60
//   - Directory path contains: 40 (directories only)
func (idx *CompactIndex) Search(query string, limit int) []Entry {
	query = strings.TrimSpace(query)
	if query == "" {
		return nil
	}
	queryLower := []byte(strings.ToLower(query))

	var candidates []compactSearchResult

	// 1. Scan files (pure name matches only)
	for i := range idx.Files {
		f := &idx.Files[i]
		nameBytes := idx.Arena.Bytes(f.NameOff, f.NameLen)

		var score int
		switch {
		case asciiEqualFoldBytes(nameBytes, queryLower):
			score = 100
		case asciiHasPrefixFoldBytes(nameBytes, queryLower):
			score = 80
		case asciiContainsFoldBytes(nameBytes, queryLower):
			score = 60
		default:
			continue
		}

		candidates = append(candidates, compactSearchResult{
			isFolder:  false,
			id:        uint32(i),
			score:     score,
			matchType: "name",
			vpath:     idx.BuildFilePath(uint32(i)),
		})
	}

	// 2. Scan folders (name matches and path matches)
	for i := range idx.Folders {
		folder := &idx.Folders[i]
		nameBytes := idx.Arena.Bytes(folder.NameOff, folder.NameLen)

		var score int
		var matchType string
		switch {
		case asciiEqualFoldBytes(nameBytes, queryLower):
			score = 100
			matchType = "name"
		case asciiHasPrefixFoldBytes(nameBytes, queryLower):
			score = 80
			matchType = "name"
		case asciiContainsFoldBytes(nameBytes, queryLower):
			score = 60
			matchType = "name"
		default:
			// Check full virtual path for directories (zero allocation)
			if folder.PathLen > 0 {
				pathBytes := idx.Arena.Bytes(folder.PathOff, folder.PathLen)
				if asciiContainsFoldBytes(pathBytes, queryLower) {
					score = 40
					matchType = "path"
				}
			}
		}

		if score > 0 {
			candidates = append(candidates, compactSearchResult{
				isFolder:  true,
				id:        uint32(i),
				score:     score,
				matchType: matchType,
				vpath:     idx.BuildFolderPath(uint32(i)),
			})
		}
	}

	// 3. Sort candidates by score descending (ties broken by path ascending)
	sort.Slice(candidates, func(i, j int) bool {
		if candidates[i].score != candidates[j].score {
			return candidates[i].score > candidates[j].score
		}
		return candidates[i].vpath < candidates[j].vpath
	})

	if limit > 0 && len(candidates) > limit {
		candidates = candidates[:limit]
	}

	// 4. Materialize Entry objects only for the top-N results
	out := make([]Entry, len(candidates))
	for i, c := range candidates {
		if c.isFolder {
			folder := &idx.Folders[c.id]
			out[i] = Entry{
				Name:      idx.Arena.String(folder.NameOff, folder.NameLen),
				Path:      c.vpath,
				Size:      folder.Size,
				ModTime:   time.Unix(0, folder.ModTime),
				IsDir:     true,
				MatchType: c.matchType,
			}
		} else {
			file := &idx.Files[c.id]
			out[i] = Entry{
				Name:      idx.Arena.String(file.NameOff, file.NameLen),
				Path:      c.vpath,
				Size:      file.Size,
				ModTime:   time.Unix(0, file.ModTime),
				IsDir:     false,
				MatchType: c.matchType,
			}
		}
	}

	return out
}

// ── Binary Persistence (FLIX v3 Format) ──

const (
	flixMagic   = "FLIX"
	flixVersion = uint16(3)
)

func computeRootsHash(roots []RootMapping) uint64 {
	h := fnv.New64a()
	for _, r := range roots {
		h.Write([]byte(r.URL))
		h.Write([]byte{0})
		h.Write([]byte(r.Path))
		h.Write([]byte{0})
	}
	return h.Sum64()
}

// Save writes the compact index to disk in binary FLIX format.
// If compress is true, the payload is gzip compressed.
func (idx *CompactIndex) Save(filePath string, roots []RootMapping, compress bool) error {
	tmpPath := filePath + ".tmp"
	f, err := os.Create(tmpPath)
	if err != nil {
		return err
	}
	defer func() {
		f.Close()
		os.Remove(tmpPath)
	}()

	// Header: Magic(4) + Version(2) + Flags(2) + RootsHash(8) + FolderCount(4) + FileCount(4) + ArenaLen(4) + Checksum(4)
	flags := uint16(0)
	if compress {
		flags |= 1
	}

	rootsHash := computeRootsHash(roots)
	folderCount := uint32(len(idx.Folders))
	fileCount := uint32(len(idx.Files))
	arenaLen := uint32(len(idx.Arena.buf))

	// Write magic directly to file (uncompressed)
	if _, err := f.Write([]byte(flixMagic)); err != nil {
		return err
	}

	// Write placeholder header directly to file (uncompressed)
	headerBuf := make([]byte, 28)
	binary.LittleEndian.PutUint16(headerBuf[0:2], flixVersion)
	binary.LittleEndian.PutUint16(headerBuf[2:4], flags)
	binary.LittleEndian.PutUint64(headerBuf[4:12], rootsHash)
	binary.LittleEndian.PutUint32(headerBuf[12:16], folderCount)
	binary.LittleEndian.PutUint32(headerBuf[16:20], fileCount)
	binary.LittleEndian.PutUint32(headerBuf[20:24], arenaLen)
	if _, err := f.Write(headerBuf); err != nil {
		return err
	}

	// Prepare payload writer (buffered, and wrapped in gzip if compressed)
	bw := bufio.NewWriterSize(f, 256*1024)
	var payloadOut io.Writer = bw
	var gw *gzip.Writer
	if compress {
		gw = gzip.NewWriter(bw)
		payloadOut = gw
	}

	// Compute CRC32 checksum over folders, files, and arena
	crc := crc32.NewIEEE()
	crcWriter := io.MultiWriter(payloadOut, crc)

	// Write Folders (40 bytes each)
	folderBytes := make([]byte, 40)
	for i := range idx.Folders {
		fo := &idx.Folders[i]
		binary.LittleEndian.PutUint32(folderBytes[0:4], fo.ParentID)
		binary.LittleEndian.PutUint32(folderBytes[4:8], fo.NameOff)
		binary.LittleEndian.PutUint16(folderBytes[8:10], fo.NameLen)
		binary.LittleEndian.PutUint16(folderBytes[10:12], fo.PathLen)
		binary.LittleEndian.PutUint32(folderBytes[12:16], fo.PathOff)
		binary.LittleEndian.PutUint32(folderBytes[16:20], fo.FileStart)
		binary.LittleEndian.PutUint32(folderBytes[20:24], fo.FileCount)
		binary.LittleEndian.PutUint64(folderBytes[24:32], uint64(fo.ModTime))
		binary.LittleEndian.PutUint64(folderBytes[32:40], uint64(fo.Size))
		if _, err := crcWriter.Write(folderBytes); err != nil {
			return err
		}
	}

	// Write Files (32 bytes each)
	fileBytes := make([]byte, 32)
	for i := range idx.Files {
		fi := &idx.Files[i]
		binary.LittleEndian.PutUint32(fileBytes[0:4], fi.FolderID)
		binary.LittleEndian.PutUint32(fileBytes[4:8], fi.NameOff)
		binary.LittleEndian.PutUint16(fileBytes[8:10], fi.NameLen)
		binary.LittleEndian.PutUint16(fileBytes[10:12], 0) // padding
		binary.LittleEndian.PutUint64(fileBytes[12:20], uint64(fi.Size))
		binary.LittleEndian.PutUint64(fileBytes[20:28], uint64(fi.ModTime))
		binary.LittleEndian.PutUint32(fileBytes[28:32], 0) // padding
		if _, err := crcWriter.Write(fileBytes); err != nil {
			return err
		}
	}

	// Write Arena buffer
	if len(idx.Arena.buf) > 0 {
		if _, err := crcWriter.Write(idx.Arena.buf); err != nil {
			return err
		}
	}

	// Checksum in header trailer
	checksum := crc.Sum32()
	binary.LittleEndian.PutUint32(headerBuf[24:28], checksum)

	if gw != nil {
		if err := gw.Close(); err != nil {
			return err
		}
	}
	if err := bw.Flush(); err != nil {
		return err
	}

	// Now rewind and write header with checksum
	if _, err := f.Seek(4, io.SeekStart); err != nil {
		return err
	}
	if _, err := f.Write(headerBuf); err != nil {
		return err
	}

	if err := f.Close(); err != nil {
		return err
	}

	return os.Rename(tmpPath, filePath)
}

// LoadCompactIndex restores a compact index from disk.
func LoadCompactIndex(filePath string, roots []RootMapping) (*CompactIndex, error) {
	f, err := os.Open(filePath)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	magic := make([]byte, 4)
	if _, err := io.ReadFull(f, magic); err != nil {
		return nil, fmt.Errorf("read magic: %w", err)
	}
	if string(magic) != flixMagic {
		return nil, fmt.Errorf("invalid format: expected %s, got %s", flixMagic, string(magic))
	}

	headerBuf := make([]byte, 28)
	if _, err := io.ReadFull(f, headerBuf); err != nil {
		return nil, fmt.Errorf("read header: %w", err)
	}

	ver := binary.LittleEndian.Uint16(headerBuf[0:2])
	flags := binary.LittleEndian.Uint16(headerBuf[2:4])
	rootsHash := binary.LittleEndian.Uint64(headerBuf[4:12])
	folderCount := binary.LittleEndian.Uint32(headerBuf[12:16])
	fileCount := binary.LittleEndian.Uint32(headerBuf[16:20])
	arenaLen := binary.LittleEndian.Uint32(headerBuf[20:24])
	_ = binary.LittleEndian.Uint32(headerBuf[24:28]) // checksum

	if ver != flixVersion {
		return nil, fmt.Errorf("unsupported version %d", ver)
	}
	if roots != nil && rootsHash != computeRootsHash(roots) {
		return nil, fmt.Errorf("roots configuration changed, index invalidated")
	}

	br := bufio.NewReaderSize(f, 256*1024)
	var r io.Reader = br
	if flags&1 != 0 {
		gz, err := gzip.NewReader(br)
		if err != nil {
			return nil, fmt.Errorf("gzip reader: %w", err)
		}
		defer gz.Close()
		r = gz
	}

	idx := &CompactIndex{
		Folders:   make([]CompactFolder, folderCount),
		Files:     make([]CompactFile, fileCount),
		Arena:     StringArena{buf: make([]byte, arenaLen)},
		folderMap: make(map[string]uint32, folderCount),
	}

	// Read Folders
	folderBytes := make([]byte, 40)
	for i := uint32(0); i < folderCount; i++ {
		if _, err := io.ReadFull(r, folderBytes); err != nil {
			return nil, fmt.Errorf("read folder %d: %w", i, err)
		}
		idx.Folders[i] = CompactFolder{
			ParentID:  binary.LittleEndian.Uint32(folderBytes[0:4]),
			NameOff:   binary.LittleEndian.Uint32(folderBytes[4:8]),
			NameLen:   binary.LittleEndian.Uint16(folderBytes[8:10]),
			PathLen:   binary.LittleEndian.Uint16(folderBytes[10:12]),
			PathOff:   binary.LittleEndian.Uint32(folderBytes[12:16]),
			FileStart: binary.LittleEndian.Uint32(folderBytes[16:20]),
			FileCount: binary.LittleEndian.Uint32(folderBytes[20:24]),
			ModTime:   int64(binary.LittleEndian.Uint64(folderBytes[24:32])),
			Size:      int64(binary.LittleEndian.Uint64(folderBytes[32:40])),
		}
	}

	// Read Files
	fileBytes := make([]byte, 32)
	for i := uint32(0); i < fileCount; i++ {
		if _, err := io.ReadFull(r, fileBytes); err != nil {
			return nil, fmt.Errorf("read file %d: %w", i, err)
		}
		idx.Files[i] = CompactFile{
			FolderID: binary.LittleEndian.Uint32(fileBytes[0:4]),
			NameOff:  binary.LittleEndian.Uint32(fileBytes[4:8]),
			NameLen:  binary.LittleEndian.Uint16(fileBytes[8:10]),
			Size:     int64(binary.LittleEndian.Uint64(fileBytes[12:20])),
			ModTime:  int64(binary.LittleEndian.Uint64(fileBytes[20:28])),
		}
	}

	// Read Arena
	if arenaLen > 0 {
		if _, err := io.ReadFull(r, idx.Arena.buf); err != nil {
			return nil, fmt.Errorf("read arena: %w", err)
		}
	}

	// Rebuild folderMap
	for i := range idx.Folders {
		vpath := idx.BuildFolderPath(uint32(i))
		idx.folderMap[vpath] = uint32(i)
	}

	return idx, nil
}

// ── Converter from Map[string]Entry (for Benchmark & Migration) ──

// BuildCompactIndexFromEntries converts an existing map[string]Entry into a CompactIndex.
func BuildCompactIndexFromEntries(entries map[string]Entry, roots []RootMapping) *CompactIndex {
	idx := NewCompactIndex()

	// 1. Separate folders and files
	type rawItem struct {
		vpath string
		entry Entry
	}
	var dirs []rawItem
	var files []rawItem

	for p, e := range entries {
		if e.IsDir {
			dirs = append(dirs, rawItem{p, e})
		} else {
			files = append(files, rawItem{p, e})
		}
	}

	// 2. Sort directories by path length / depth so parent is always created before children
	sort.Slice(dirs, func(i, j int) bool {
		return len(dirs[i].vpath) < len(dirs[j].vpath)
	})

	// Add root folders if missing
	for _, r := range roots {
		if _, ok := idx.folderMap[r.URL]; !ok {
			off, nlen := idx.Arena.Append(r.URL)
			id := uint32(len(idx.Folders))
			idx.Folders = append(idx.Folders, CompactFolder{
				ParentID: 0xFFFFFFFF,
				NameOff:  off,
				NameLen:  nlen,
				PathOff:  off,
				PathLen:  nlen,
				ModTime:  time.Now().UnixNano(),
			})
			idx.folderMap[r.URL] = id
		}
	}

	// Add subdirectories
	for _, d := range dirs {
		if _, exists := idx.folderMap[d.vpath]; exists {
			continue
		}
		parentVPath := path.Dir(d.vpath)
		parentID, ok := idx.folderMap[parentVPath]
		if !ok {
			parentID = 0xFFFFFFFF
		}

		name := d.entry.Name
		if name == "" {
			name = path.Base(d.vpath)
		}
		off, nlen := idx.Arena.Append(name)
		poff, plen := idx.Arena.Append(d.vpath)
		id := uint32(len(idx.Folders))
		idx.Folders = append(idx.Folders, CompactFolder{
			ParentID: parentID,
			NameOff:  off,
			NameLen:  nlen,
			PathOff:  poff,
			PathLen:  plen,
			ModTime:  d.entry.ModTime.UnixNano(),
			Size:     d.entry.Size,
		})
		idx.folderMap[d.vpath] = id
	}

	// 3. Group files by parent folder
	filesByFolder := make(map[uint32][]rawItem)
	for _, f := range files {
		parentVPath := path.Dir(f.vpath)
		parentID, ok := idx.folderMap[parentVPath]
		if !ok {
			// Auto-create missing parent folder
			name := path.Base(parentVPath)
			off, nlen := idx.Arena.Append(name)
			poff, plen := idx.Arena.Append(parentVPath)
			parentID = uint32(len(idx.Folders))
			idx.Folders = append(idx.Folders, CompactFolder{
				ParentID: 0xFFFFFFFF,
				NameOff:  off,
				NameLen:  nlen,
				PathOff:  poff,
				PathLen:  plen,
				ModTime:  time.Now().UnixNano(),
			})
			idx.folderMap[parentVPath] = parentID
		}
		filesByFolder[parentID] = append(filesByFolder[parentID], f)
	}

	// 4. Populate Files slice and sort within each folder
	for folderID := range idx.Folders {
		folderFiles := filesByFolder[uint32(folderID)]
		if len(folderFiles) == 0 {
			continue
		}

		// Sort files by name in this folder
		sort.Slice(folderFiles, func(i, j int) bool {
			return folderFiles[i].entry.Name < folderFiles[j].entry.Name
		})

		idx.Folders[folderID].FileStart = uint32(len(idx.Files))
		idx.Folders[folderID].FileCount = uint32(len(folderFiles))

		for _, item := range folderFiles {
			off, nlen := idx.Arena.Append(item.entry.Name)
			idx.Files = append(idx.Files, CompactFile{
				FolderID: uint32(folderID),
				NameOff:  off,
				NameLen:  nlen,
				Size:     item.entry.Size,
				ModTime:  item.entry.ModTime.UnixNano(),
			})
		}
	}

	return idx
}

// TotalEntries returns the total number of indexed folders and files (excluding root anchors).
func (idx *CompactIndex) TotalEntries() int {
	if idx == nil {
		return 0
	}
	rootCount := 0
	for i := range idx.Folders {
		if idx.Folders[i].ParentID == 0xFFFFFFFF {
			rootCount++
		}
	}
	return len(idx.Folders) - rootCount + len(idx.Files)
}

// AllEntries materializes and returns all entries as a map of virtual path -> Entry.
// Useful for debugging, testing, and migration verification.
func (idx *CompactIndex) AllEntries() map[string]Entry {
	if idx == nil {
		return nil
	}
	m := make(map[string]Entry, len(idx.Folders)+len(idx.Files))
	for i := range idx.Folders {
		fo := &idx.Folders[i]
		if fo.ParentID == 0xFFFFFFFF {
			continue // Root anchors are configuration, not indexed entries
		}
		vpath := idx.BuildFolderPath(uint32(i))
		m[vpath] = Entry{
			Name:    idx.Arena.String(fo.NameOff, fo.NameLen),
			Path:    vpath,
			Size:    fo.Size,
			ModTime: time.Unix(0, fo.ModTime),
			IsDir:   true,
		}
	}
	for i := range idx.Files {
		fi := &idx.Files[i]
		vpath := idx.BuildFilePath(uint32(i))
		m[vpath] = Entry{
			Name:    idx.Arena.String(fi.NameOff, fi.NameLen),
			Path:    vpath,
			Size:    fi.Size,
			ModTime: time.Unix(0, fi.ModTime),
			IsDir:   false,
		}
	}
	return m
}

// CountSubtreeEntries counts the total number of entries (the folder itself, its files,
// and all descendant folders and their files) in a folder subtree.
func (idx *CompactIndex) CountSubtreeEntries(folderID uint32, children [][]uint32) int {
	if folderID >= uint32(len(idx.Folders)) {
		return 0
	}
	fo := &idx.Folders[folderID]
	count := 1 + int(fo.FileCount) // folder itself + files
	if int(folderID) < len(children) {
		for _, childFID := range children[folderID] {
			count += idx.CountSubtreeEntries(childFID, children)
		}
	}
	return count
}

// ── Direct Tree Walker and Incremental Pass Builder ──

type dirItem struct {
	name string
	info os.FileInfo
}

type fileItem struct {
	name string
	info os.FileInfo
}

type compactBuilder struct {
	cfg         *Config
	old         *CompactIndex
	incremental bool

	folders   []CompactFolder
	files     []CompactFile
	arena     StringArena
	folderMap map[string]uint32

	oldChildren [][]uint32 // child folder IDs in old index
	res         buildResult
	errs        []error
	progress    *scanProgress
}

func newCompactBuilder(cfg *Config, old *CompactIndex, incremental bool) *compactBuilder {
	b := &compactBuilder{
		cfg:         cfg,
		old:         old,
		incremental: incremental,
		progress:    &scanProgress{start: time.Now(), lastLog: time.Now()},
	}

	if old != nil {
		b.folders = make([]CompactFolder, 0, len(old.Folders))
		b.files = make([]CompactFile, 0, len(old.Files))
		b.arena = StringArena{buf: make([]byte, 0, len(old.Arena.buf))}
		b.folderMap = make(map[string]uint32, len(old.Folders))
		if incremental {
			b.oldChildren = make([][]uint32, len(old.Folders))
			for i := range old.Folders {
				p := old.Folders[i].ParentID
				if p != 0xFFFFFFFF && int(p) < len(old.Folders) {
					b.oldChildren[p] = append(b.oldChildren[p], uint32(i))
				}
			}
		}
	} else {
		b.folderMap = make(map[string]uint32)
	}

	return b
}

func (b *compactBuilder) addFolder(parentID uint32, name, vpath string, modTime int64, size int64) uint32 {
	off, nlen := b.arena.Append(name)
	poff, plen := b.arena.Append(vpath)
	fid := uint32(len(b.folders))
	b.folders = append(b.folders, CompactFolder{
		ParentID: parentID,
		NameOff:  off,
		NameLen:  nlen,
		PathOff:  poff,
		PathLen:  plen,
		ModTime:  modTime,
		Size:     size,
	})
	b.folderMap[vpath] = fid
	return fid
}

func (b *compactBuilder) isExcluded(name string, isDir bool) bool {
	if isDir {
		for _, d := range b.cfg.Index.ExcludeDirs {
			if matchName(d, name) {
				return true
			}
		}
		return false
	}
	for _, f := range b.cfg.Index.ExcludeFiles {
		if matchName(f, name) {
			return true
		}
	}
	return false
}

// copySubtree copies the folder, its files, and all child subtrees from b.old into b.
func (b *compactBuilder) copySubtree(oldFID, newFID uint32) {
	if b.old == nil || oldFID >= uint32(len(b.old.Folders)) {
		return
	}
	oldFo := &b.old.Folders[oldFID]

	// 1. Copy files
	b.folders[newFID].FileStart = uint32(len(b.files))
	b.folders[newFID].FileCount = oldFo.FileCount
	for i := oldFo.FileStart; i < oldFo.FileStart+oldFo.FileCount; i++ {
		oldFile := &b.old.Files[i]
		fileName := b.old.Arena.String(oldFile.NameOff, oldFile.NameLen)
		off, nlen := b.arena.Append(fileName)
		b.files = append(b.files, CompactFile{
			FolderID: newFID,
			NameOff:  off,
			NameLen:  nlen,
			Size:     oldFile.Size,
			ModTime:  oldFile.ModTime,
		})
	}

	// 2. Copy child folders recursively
	if int(oldFID) < len(b.oldChildren) {
		for _, oldChildFID := range b.oldChildren[oldFID] {
			oldChild := &b.old.Folders[oldChildFID]
			childName := b.old.Arena.String(oldChild.NameOff, oldChild.NameLen)
			childVPath := b.old.BuildFolderPath(oldChildFID)
			newChildFID := b.addFolder(newFID, childName, childVPath, oldChild.ModTime, oldChild.Size)
			b.copySubtree(oldChildFID, newChildFID)
		}
	}
}

func (b *compactBuilder) walkDir(r RootMapping, vdir, realDir string, currentFID uint32, depth int) {
	items, err := os.ReadDir(realDir)
	if err != nil {
		b.errs = append(b.errs, fmt.Errorf("read dir %s: %w", realDir, err))
		return
	}

	b.progress.dirs++
	b.progress.entries += len(items)
	b.progress.current = realDir
	if now := time.Now(); now.Sub(b.progress.lastLog) >= scanProgressInterval {
		b.progress.lastLog = now
		logger.Info("indexer: scan in progress: %d entries in %d dirs, at %s (elapsed %s)",
			b.progress.entries, b.progress.dirs, b.progress.current, now.Sub(b.progress.start).Round(time.Second))
	}

	var subdirs []dirItem
	var files []fileItem

	for _, de := range items {
		name := de.Name()
		if name == "" {
			continue
		}
		if de.IsDir() {
			if b.isExcluded(name, true) {
				continue
			}
			fi, err := de.Info()
			if err != nil {
				continue
			}
			subdirs = append(subdirs, dirItem{name: name, info: fi})
		} else {
			if b.isExcluded(name, false) {
				continue
			}
			fi, err := de.Info()
			if err != nil {
				continue
			}
			files = append(files, fileItem{name: name, info: fi})
		}
	}

	// Handle files in this directory (alphabetically sorted)
	sort.Slice(files, func(i, j int) bool {
		return files[i].name < files[j].name
	})

	b.folders[currentFID].FileStart = uint32(len(b.files))
	b.folders[currentFID].FileCount = uint32(len(files))

	oldFID := uint32(0)
	hasOldFolder := false
	if b.old != nil {
		oldFID, hasOldFolder = b.old.folderMap[vdir]
	}

	for _, fi := range files {
		off, nlen := b.arena.Append(fi.name)
		fMod := fi.info.ModTime().UnixNano()
		fSize := fi.info.Size()
		if hasOldFolder {
			oldF, found := b.old.FindFile(oldFID, fi.name)
			if !found {
				b.res.Added++
			} else if oldF.ModTime != fMod || oldF.Size != fSize {
				b.res.Changed++
			}
		} else {
			b.res.Added++
		}
		b.files = append(b.files, CompactFile{
			FolderID: currentFID,
			NameOff:  off,
			NameLen:  nlen,
			Size:     fSize,
			ModTime:  fMod,
		})
	}

	if hasOldFolder {
		// Detect deleted files in oldFID
		oldFo := &b.old.Folders[oldFID]
		for i := oldFo.FileStart; i < oldFo.FileStart+oldFo.FileCount; i++ {
			oldName := b.old.Arena.String(b.old.Files[i].NameOff, b.old.Files[i].NameLen)
			idx := sort.Search(len(files), func(k int) bool { return files[k].name >= oldName })
			if idx >= len(files) || files[idx].name != oldName {
				b.res.Removed++
			}
		}
	}

	// Handle subdirectories
	seenSubdirs := make(map[string]bool, len(subdirs))
	for _, sd := range subdirs {
		seenSubdirs[sd.name] = true
		childVPath := path.Join(vdir, sd.name)
		childMod := sd.info.ModTime().UnixNano()
		childSize := sd.info.Size()

		childFID := b.addFolder(currentFID, sd.name, childVPath, childMod, childSize)

		if hasOldFolder {
			// Check if folder itself was newly added or changed
			if oldChildFID, ok := b.old.folderMap[childVPath]; !ok {
				b.res.Added++
			} else {
				oldChild := &b.old.Folders[oldChildFID]
				if oldChild.ModTime != childMod || oldChild.Size != childSize {
					b.res.Changed++
				}
			}
		} else {
			b.res.Added++
		}

		// Check if we can skip reading child subtree
		canSkip := false
		if b.incremental && b.old != nil && depth+1 >= b.cfg.Index.RescanDepth {
			if oldChildFID, ok := b.old.folderMap[childVPath]; ok {
				oldChild := &b.old.Folders[oldChildFID]
				if oldChild.ModTime == childMod && oldChild.Size == childSize {
					canSkip = true
					b.copySubtree(oldChildFID, childFID)
					b.res.Skipped++
				}
			}
		}
		if canSkip {
			continue
		}

		if b.cfg.Index.MaxDepth > 0 && depth+1 >= b.cfg.Index.MaxDepth {
			continue
		}

		childRealDir := filepath.Join(realDir, sd.name)
		b.walkDir(r, childVPath, childRealDir, childFID, depth+1)
	}

	if hasOldFolder && int(oldFID) < len(b.oldChildren) {
		// Detect deleted old subdirectories
		for _, oldChildFID := range b.oldChildren[oldFID] {
			oldChildName := b.old.Arena.String(b.old.Folders[oldChildFID].NameOff, b.old.Folders[oldChildFID].NameLen)
			if !seenSubdirs[oldChildName] {
				// Old child directory was deleted! Count all entries in its subtree as removed
				b.res.Removed += b.old.CountSubtreeEntries(oldChildFID, b.oldChildren)
			}
		}
	}
}

// buildCompactPass performs a complete scan pass and returns the new CompactIndex and buildResult.
func buildCompactPass(cfg *Config, old *CompactIndex, incremental bool) (*CompactIndex, buildResult, []error) {
	start := time.Now()
	b := newCompactBuilder(cfg, old, incremental)

	for _, r := range cfg.Roots {
		info, err := os.Stat(r.Path)
		if err != nil {
			b.errs = append(b.errs, fmt.Errorf("cannot access %s: %w", r.Path, err))
			continue
		}
		if !info.IsDir() {
			b.errs = append(b.errs, fmt.Errorf("%s is not a directory", r.Path))
			continue
		}

		rootMod := info.ModTime().UnixNano()
		rootSize := info.Size()
		rootFID := b.addFolder(0xFFFFFFFF, r.URL, r.URL, rootMod, rootSize)

		// Can skip root only when RescanDepth <= 0
		canSkipRoot := false
		if b.incremental && b.old != nil && b.cfg.Index.RescanDepth <= 0 {
			if oldRootFID, ok := b.old.folderMap[r.URL]; ok {
				oldRoot := &b.old.Folders[oldRootFID]
				if oldRoot.ModTime == rootMod && oldRoot.Size == rootSize {
					canSkipRoot = true
					b.copySubtree(oldRootFID, rootFID)
					b.res.Skipped++
				}
			}
		}
		if canSkipRoot {
			continue
		}

		logger.Info("indexer: scanning root %s -> %s", r.URL, r.Path)
		b.walkDir(r, r.URL, r.Path, rootFID, 0)
	}

	res := b.res
	res.Duration = time.Since(start)

	idx := &CompactIndex{
		Folders:   b.folders,
		Files:     b.files,
		Arena:     b.arena,
		folderMap: b.folderMap,
	}

	return idx, res, b.errs
}
