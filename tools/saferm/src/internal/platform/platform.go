// Package platform 把平台相关的文件系统语义收敛到极小的一层。
//
// 上层的 guard/trash/volume 只依赖本包导出的这几个函数，不直接碰 syscall，
// 这样 Windows 与 Linux 的差异全部集中在这里，便于逐一验证。
package platform

import (
	"fmt"
	"os"
	"path/filepath"
)

// AbsClean 返回绝对化并 Clean 过的字面路径。
//
// 刻意不用 filepath.EvalSymlinks：那会解析最后一级的符号链接，
// 与"删链接本身而不是链接目标"的语义冲突（设计文档 §5.0）。
func AbsClean(p string) (string, error) {
	abs, err := filepath.Abs(p)
	if err != nil {
		return "", fmt.Errorf("resolve absolute path %q: %w", p, err)
	}
	return filepath.Clean(abs), nil
}

// deepestExisting 返回 p 及其祖先中第一个真实存在的路径。
//
// 用途：回收根可能尚未创建，但"它在哪个卷上"仍然必须能判定。
// 用 Lstat 而非 Stat：不跟随符号链接，得到的是"该条目本身所在的位置"。
func deepestExisting(p string) (string, error) {
	abs, err := filepath.Abs(p)
	if err != nil {
		return "", fmt.Errorf("resolve absolute path %q: %w", p, err)
	}
	cur := filepath.Clean(abs)
	for {
		if _, err := os.Lstat(cur); err == nil {
			return cur, nil
		} else if !os.IsNotExist(err) {
			return "", fmt.Errorf("inspect %q: %w", cur, err)
		}
		parent := filepath.Dir(cur)
		if parent == cur {
			// 走到根仍然不存在（例如空读卡器上的盘符）
			return "", fmt.Errorf("no existing ancestor for %q", p)
		}
		cur = parent
	}
}

// renameVolumeTarget 返回"对这个路径做 rename 时，实际起作用的是哪个卷"。
//
// rename 发生在父目录所在的文件系统上，所以先取 Dir(p)：
//   - p 是符号链接时，被移动的是链接本身，而链接存放在父目录里；
//   - p 是普通文件/目录时，父目录与它必然同卷。
//
// 再对父目录求"最深的已存在祖先"，以支持尚未创建的回收根。
func renameVolumeTarget(p string) (string, error) {
	return deepestExisting(filepath.Dir(filepath.Clean(p)))
}
