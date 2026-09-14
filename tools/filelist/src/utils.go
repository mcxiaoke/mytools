package main

import (
	"crypto/subtle"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"unicode/utf8"
)

// ── HTTP & Response Utilities ──────────────────────────────────

// writeJSON encodes v as JSON and writes it to the response.
func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	if err := json.NewEncoder(w).Encode(v); err != nil {
		logger.Error("server: json encode: %v", err)
	}
}

// writeJSONError writes a JSON error object {"error": msg} with the specified HTTP status code.
func writeJSONError(w http.ResponseWriter, msg string, code int) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(code)
	if err := json.NewEncoder(w).Encode(map[string]string{"error": msg}); err != nil {
		logger.Error("server: json encode error: %v", err)
	}
}

// contentDisposition builds a Content-Disposition header value with
// RFC 5987 encoding for non-ASCII filenames.
func contentDisposition(filename string) string {
	encoded := url.PathEscape(filename)
	return fmt.Sprintf(`attachment; filename*=UTF-8''%s`, encoded)
}

// constEqual compares two strings in constant time without leaking length/timing.
func constEqual(a, b string) bool {
	return subtle.ConstantTimeCompare([]byte(a), []byte(b)) == 1
}

// isInlineRisk reports whether a file could execute script when rendered
// inline by the browser.
func isInlineRisk(name string) bool {
	switch strings.ToLower(filepath.Ext(name)) {
	case ".html", ".htm", ".xhtml", ".svg", ".svgz", ".mhtml":
		return true
	}
	return false
}

// ── Filesystem & Path Utilities ─────────────────────────────────

// withinRoot reports whether realPath (after resolving symlinks) still lives
// inside rootPath.
func withinRoot(realPath, rootPath string) bool {
	resolved, err := filepath.EvalSymlinks(realPath)
	if err != nil {
		// cannot resolve (missing file, permission) — fall through to the
		// lexical check below; os.Stat later decides the response.
		resolved = realPath
	}
	rootResolved, err := filepath.EvalSymlinks(rootPath)
	if err != nil {
		rootResolved = rootPath
	}
	return pathWithin(resolved, rootResolved)
}

// pathWithin reports whether child is parent or lives under it.
func pathWithin(child, parent string) bool {
	rel, err := filepath.Rel(parent, child)
	if err == nil && rel != ".." && !strings.HasPrefix(rel, ".."+string(filepath.Separator)) {
		return true
	}
	// case-insensitive fallback: Windows drive letters, 8.3 names, mounts
	c := strings.ToLower(filepath.Clean(child))
	p := strings.ToLower(filepath.Clean(parent))
	if c == p {
		return true
	}
	return strings.HasPrefix(c, p+string(filepath.Separator))
}

// ── Filename & Upload Utilities ─────────────────────────────────

// windowsReservedNames lists device names reserved in DOS/Windows.
var windowsReservedNames = map[string]bool{
	"CON": true, "PRN": true, "AUX": true, "NUL": true,
	"COM1": true, "COM2": true, "COM3": true, "COM4": true,
	"COM5": true, "COM6": true, "COM7": true, "COM8": true, "COM9": true,
	"LPT1": true, "LPT2": true, "LPT3": true, "LPT4": true,
	"LPT5": true, "LPT6": true, "LPT7": true, "LPT8": true, "LPT9": true,
}

const (
	// maxFilenameBytes leaves ~15 bytes headroom for numeric collision suffixes within 255 bytes limit.
	maxFilenameBytes = 240
	maxExtBytes      = 32
)

// truncateUTF8 truncates string s so that len(s) <= maxBytes at a valid UTF-8 rune boundary.
func truncateUTF8(s string, maxBytes int) string {
	if maxBytes <= 0 {
		return ""
	}
	if len(s) <= maxBytes {
		return s
	}
	var curBytes int
	for i, r := range s {
		runeLen := utf8.RuneLen(r)
		if runeLen < 0 {
			runeLen = 1
		}
		if curBytes+runeLen > maxBytes {
			return s[:i]
		}
		curBytes += runeLen
	}
	return s
}

// sanitizeFilename sanitizes an uploaded filename across platforms:
// - Strips any leading directory paths (both / and \)
// - Replaces illegal filesystem characters (<>:"/\|?*) and control characters with '_'
// - Trims trailing dots and spaces (Windows restriction)
// - Avoids Windows reserved device names (CON, AUX, etc.)
// - Truncates overly long names at valid UTF-8 boundaries to fit filesystem limits (max 240 bytes)
// - Fallbacks to "upload" if the filename becomes empty
func sanitizeFilename(name string) string {
	// Strip directory paths from both POSIX and Windows styles
	if idx := strings.LastIndexAny(name, `/\`); idx != -1 {
		name = name[idx+1:]
	}
	name = filepath.Clean(name)

	// Replace illegal characters across OS and control characters
	var sb strings.Builder
	for _, r := range name {
		switch {
		case r < 32 || r == 127:
			sb.WriteRune('_')
		case r == '/' || r == '\\' || r == ':' || r == '*' || r == '?' || r == '"' || r == '<' || r == '>' || r == '|':
			sb.WriteRune('_')
		default:
			sb.WriteRune(r)
		}
	}
	cleaned := strings.TrimRight(sb.String(), ". ")

	stem, ext := splitNameExt(cleaned)
	stem = strings.TrimRight(stem, ". ")
	stem = strings.TrimLeft(stem, " ")

	// Truncate extension if unusually long
	if len(ext) > maxExtBytes {
		ext = truncateUTF8(ext, maxExtBytes)
	}

	// Fallback to "upload" if stem is empty
	if stem == "" {
		stem = "upload"
	}

	// Avoid Windows reserved device names (CON, AUX, NUL, COM1, etc.)
	checkStem := stem
	if firstDot := strings.Index(checkStem, "."); firstDot > 0 {
		checkStem = checkStem[:firstDot]
	}
	if windowsReservedNames[strings.ToUpper(stem)] || windowsReservedNames[strings.ToUpper(checkStem)] {
		stem = "_" + stem
	}

	// Truncate stem to ensure total filename length <= maxFilenameBytes
	maxStem := maxFilenameBytes - len(ext)
	if maxStem < 1 {
		maxStem = 1
	}
	if len(stem) > maxStem {
		stem = truncateUTF8(stem, maxStem)
		stem = strings.TrimRight(stem, ". ")
		if stem == "" {
			stem = "upload"
		}
	}

	return stem + ext
}

// splitNameExt separates the base stem and extension of a filename.
// For dotfiles like ".gitignore", the whole filename is treated as stem with no extension.
func splitNameExt(filename string) (stem, ext string) {
	ext = filepath.Ext(filename)
	if ext == filename {
		return filename, ""
	}
	return strings.TrimSuffix(filename, ext), ext
}

// availableFilename generates a non-conflicting filename in dir.
// If filename already exists, it appends a numeric suffix: name (1).ext, name (2).ext, etc.
func availableFilename(dir, filename string) string {
	target := filepath.Join(dir, filename)
	if _, err := os.Lstat(target); os.IsNotExist(err) {
		return filename
	}

	stem, ext := splitNameExt(filename)
	for i := 1; ; i++ {
		candidate := fmt.Sprintf("%s (%d)%s", stem, i, ext)
		candidatePath := filepath.Join(dir, candidate)
		if _, err := os.Lstat(candidatePath); os.IsNotExist(err) {
			return candidate
		}
	}
}
