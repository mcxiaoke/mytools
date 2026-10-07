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
//
// 末段为 Windows 保留设备名（nul/con/aux…）的路径会在这里被拒绝：
// Go 1.26 的 filepath.Abs 会把它们改写成设备命名空间形式，guard 若拿着
// 被改写过的路径继续走，就会对错误的对象做判断。拒绝发生在任何检查
// 与移动之前，原数据必然原封不动（C6/C7）。
func AbsClean(p string) (string, error) {
	if err := checkLiteralPath(p); err != nil {
		return "", err
	}
	abs, err := filepath.Abs(p)
	if err != nil {
		return "", fmt.Errorf("resolve absolute path %q: %w", p, err)
	}
	return filepath.Clean(abs), nil
}

// DeepestExisting 返回 p 及其祖先中第一个真实存在的路径。
//
// 用途：回收根可能尚未创建，但"它在哪个卷上""它能不能被写"仍然必须能判定。
// 用 Lstat 而非 Stat：不跟随符号链接，得到的是"该条目本身所在的位置"。
//
// 注意这里**不能**取父目录：卷挂载点自身（Linux 的 /data、Windows 的 D:\）
// 也是合法的入参，取父目录会把 /data 误判成 /。卷的判定在 Windows 与 Linux
// 上都是纯字面（按挂载点前缀），因此直接对 p 本身求最深的已存在祖先才正确。
func DeepestExisting(p string) (string, error) {
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
