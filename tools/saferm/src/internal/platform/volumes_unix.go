//go:build !windows

package platform

import (
	"sort"
	"strings"

	"golang.org/x/sys/unix"
)

// pseudoFS 是没有实体存储、不该被当作"可放回收目录的卷"的文件系统类型。
var pseudoFS = map[string]bool{
	"proc": true, "sysfs": true, "devtmpfs": true, "devpts": true, "tmpfs": true,
	"cgroup": true, "cgroup2": true, "securityfs": true, "pstore": true,
	"debugfs": true, "tracefs": true, "configfs": true, "fusectl": true,
	"mqueue": true, "hugetlbfs": true, "bpf": true, "autofs": true,
	"binfmt_misc": true, "rpc_pipefs": true, "nsfs": true, "efivarfs": true,
	"ramfs": true, "squashfs": true, "overlay": true,
}

// ListVolumes 返回本机带有实体存储的挂载点，例如 `/`、`/data`、`/mnt/usb`。
//
// 过滤两类：伪文件系统（/proc 之类），以及挂载点位于 /proc /sys /dev /run 下的。
// 目的是给 `saferm where` 一份"用户真的会删文件的地方"的清单，
// 而不是把内核的挂载表原样倒出来。
func ListVolumes() ([]string, error) {
	entries, err := parseMounts()
	if err != nil {
		return nil, err
	}

	seen := map[string]bool{}
	var out []string
	for _, e := range entries {
		if pseudoFS[e.FSType] {
			continue
		}
		physical := strings.HasPrefix(e.Device, "/dev/") ||
			strings.Contains(e.FSType, "zfs") ||
			e.FSType == "nfs" || e.FSType == "nfs4" ||
			e.FSType == "cifs" || e.FSType == "smb3"
		if !physical {
			continue
		}
		if hasAnyPrefix(e.MountPoint, []string{"/proc", "/sys", "/dev", "/run"}) {
			continue
		}
		if seen[e.MountPoint] {
			continue
		}
		seen[e.MountPoint] = true
		out = append(out, e.MountPoint)
	}
	sort.Strings(out)
	return out, nil
}

// IsWritableDir 判断目录是否可写。
//
// 用 access(2) 的 W_OK 检查，只查询权限、不产生任何副作用 ——
// `saferm where` 承诺只读不写，不能靠"创建探测文件再删掉"实现。
func IsWritableDir(dir string) (bool, error) {
	if err := unix.Access(dir, unix.W_OK); err != nil {
		return false, nil
	}
	return true, nil
}

func hasAnyPrefix(p string, prefixes []string) bool {
	for _, pre := range prefixes {
		if p == pre || strings.HasPrefix(p, pre+"/") {
			return true
		}
	}
	return false
}
