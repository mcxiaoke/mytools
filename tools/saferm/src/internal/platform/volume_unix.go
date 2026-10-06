//go:build !windows

package platform

import (
	"fmt"
	"os"
	"syscall"
)

// VolumeRoot 返回 p 所在的挂载点，例如 `/`、`/home`、`/mnt/data`。
//
// Linux 上没有"盘符"概念，挂载点才是判定同卷的依据（设计文档 §5.0/§7.3）。
// 挂载点表从 /proc/self/mounts 读取；读不到就退到 /，宁可粗一点也不要猜。
func VolumeRoot(p string) (string, error) {
	target, err := DeepestExisting(p)
	if err != nil {
		return "", err
	}
	entries, err := parseMounts()
	if err != nil {
		return "/", nil
	}
	mounts := make([]string, 0, len(entries))
	for _, e := range entries {
		mounts = append(mounts, e.MountPoint)
	}
	return longestMountPrefix(mounts, target), nil
}

// SameVolume 判断两个路径是否位于同一个文件系统。
//
// 判据是比较 st_dev（设备号），这直接对应 rename(2) 能否成功：
// 内核跨文件系统 rename 返回 EXDEV，而本工具刻意不做"复制+删除"的降级。
func SameVolume(a, b string) (bool, error) {
	ta, err := DeepestExisting(a)
	if err != nil {
		return false, err
	}
	tb, err := DeepestExisting(b)
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

// statDevice 取路径所在文件系统的设备号。
//
// 用 Stat（跟随链接）：这里问的是"该条目实际位于哪个文件系统"。
// 入参应当是 deepestExisting 的结果。
func statDevice(p string) (uint64, error) {
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
