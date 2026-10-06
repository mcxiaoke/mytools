//go:build !windows

package platform

import (
	"bufio"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// mountEntry 是 /proc/self/mounts 里的一行。
type mountEntry struct {
	Device     string
	MountPoint string
	FSType     string
}

// parseMounts 读取并解析 /proc/self/mounts。
func parseMounts() ([]mountEntry, error) {
	f, err := os.Open("/proc/self/mounts")
	if err != nil {
		return nil, err
	}
	defer f.Close()

	var out []mountEntry
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		fields := strings.Fields(sc.Text())
		if len(fields) < 3 {
			continue
		}
		out = append(out, mountEntry{
			Device:     unescapeMount(fields[0]),
			MountPoint: unescapeMount(fields[1]),
			FSType:     fields[2],
		})
	}
	if err := sc.Err(); err != nil {
		return nil, err
	}
	return out, nil
}

// unescapeMount 还原 /proc/self/mounts 里的八进制转义（空格写作 \040 等）。
func unescapeMount(s string) string {
	if !strings.Contains(s, "\\") {
		return s
	}
	var b strings.Builder
	for i := 0; i < len(s); i++ {
		if s[i] == '\\' && i+3 < len(s) {
			var v int
			if _, err := fmt.Sscanf(s[i+1:i+4], "%03o", &v); err == nil {
				b.WriteByte(byte(v))
				i += 3
				continue
			}
		}
		b.WriteByte(s[i])
	}
	return b.String()
}

// longestMountPrefix 返回 p 所属的最长挂载点前缀（按路径分隔符对齐，避免 /data2 被 /data 命中）。
func longestMountPrefix(mounts []string, p string) string {
	best := "/"
	for _, m := range mounts {
		if m == "" || !strings.HasPrefix(m, "/") {
			continue
		}
		m = filepath.Clean(m)
		if !isPathPrefix(m, p) {
			continue
		}
		if len(m) > len(best) {
			best = m
		}
	}
	return best
}

// isPathPrefix 判断 base 是否是 p 的路径前缀（base == p，或 p 在 base 的下一层）。
func isPathPrefix(base, p string) bool {
	if base == p {
		return true
	}
	if base == "/" {
		return strings.HasPrefix(p, "/")
	}
	return strings.HasPrefix(p, base+"/")
}
