//go:build !windows

package platform

import (
	"bufio"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"syscall"
)

// VolumeRoot 返回 p 所在的挂载点，例如 `/`、`/home`、`/mnt/data`。
//
// Linux 上没有"盘符"概念，挂载点才是判定同卷的依据（设计文档 §5.0/§7.3）。
// 挂载点表从 /proc/self/mounts 读取；读不到就退到 /，宁可粗一点也不要猜。
func VolumeRoot(p string) (string, error) {
	target, err := deepestExisting(p)
	if err != nil {
		return "", err
	}
	mounts, err := mountPoints()
	if err != nil {
		return "/", nil
	}
	return longestMountPrefix(mounts, target), nil
}

// SameVolume 判断两个路径是否位于同一个文件系统。
//
// 判据是比较 st_dev（设备号），这直接对应 rename(2) 能否成功：
// 内核跨文件系统 rename 返回 EXDEV，而本工具刻意不做"复制+删除"的降级。
func SameVolume(a, b string) (bool, error) {
	ta, err := deepestExisting(a)
	if err != nil {
		return false, err
	}
	tb, err := deepestExisting(b)
	if err != nil {
		return false, err
	}
	da, err := statDevice(ta)
	if err != nil {
		return false, err
	}
	db, err := statDevice(tb)
	if err != nil {
		return false, err
	}
	return da == db, nil
}

// ExpandShortName 在 Linux 上是恒等操作：不存在 8.3 短名。
func ExpandShortName(p string) (string, error) {
	return AbsClean(p)
}

// IsReparsePoint 判断 p 自身是否是符号链接。
//
// 用 Lstat：不跟随链接，判断的是"这个条目本身"。
func IsReparsePoint(p string) (bool, error) {
	fi, err := os.Lstat(p)
	if err != nil {
		return false, fmt.Errorf("lstat %q: %w", p, err)
	}
	return fi.Mode()&os.ModeSymlink != 0, nil
}

// SetHidden 在 Linux 上是空操作：隐藏与否由名字（点前缀）决定，
// 而回收根默认就叫 `.saferm-trash`，已经是点开头。
func SetHidden(string) error { return nil }

func statDevice(p string) (uint64, error) {
	// Stat 而非 Lstat：这里问的是"该条目所在文件系统"，符号链接要解析到父目录语义上。
	// renameVolumeTarget 已保证传入的是父目录链上的已存在路径。
	fi, err := os.Stat(p)
	if err != nil {
		return 0, fmt.Errorf("stat %q: %w", p, err)
	}
	st, ok := fi.Sys().(*syscall.Stat_t)
	if !ok {
		return 0, fmt.Errorf("stat %q: unexpected Sys() type %T", p, fi.Sys())
	}
	return uint64(st.Dev), nil
}

func mountPoints() ([]string, error) {
	f, err := os.Open("/proc/self/mounts")
	if err != nil {
		return nil, err
	}
	defer f.Close()

	var out []string
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		fields := strings.Fields(sc.Text())
		if len(fields) < 2 {
			continue
		}
		out = append(out, unescapeMount(fields[1]))
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
