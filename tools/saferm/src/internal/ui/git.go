package ui

import (
	"os"
	"path/filepath"
	"runtime"
	"strings"

	"saferm/internal/platform"
)

// GitInfo 描述目标与版本库的关系。
//
// 这是设计文档 §6.1 第 5 步的"基础档"：只读地判断"在不在仓库里"，
// **绝不调用任何 git 写操作**（reset / clean / checkout / restore 一律不碰）。
// 增强档（调 `git status` 报未提交/未推送数量）留到 v2。
type GitInfo struct {
	InRepo   bool
	RepoRoot string
}

// vcsMarkers 是"这个目录就是仓库根"的标记。
var vcsMarkers = []string{".git", ".hg", ".svn"}

// DetectGit 从 target 起逐级向上找版本库根，到目标所在卷的挂载点为止。
//
// 必须同时识别 `.git` 的两种形态：普通仓库里它是目录，
// worktree 与 submodule 里它是内容为 `gitdir: ...` 的普通文件。
// 只判目录会让 submodule 路径漏检（评审里专门指出的一点）。
func DetectGit(target string) GitInfo {
	dir := target
	if info, err := os.Lstat(target); err == nil && !info.IsDir() {
		dir = filepath.Dir(target)
	}

	// 卷根只算一次：Linux 上每次都要解析挂载表，放在循环里是浪费
	volRoot, err := platform.VolumeRoot(dir)
	if err != nil {
		volRoot = ""
	}

	for {
		for _, marker := range vcsMarkers {
			if _, err := os.Lstat(filepath.Join(dir, marker)); err == nil {
				return GitInfo{InRepo: true, RepoRoot: dir}
			}
		}

		// 到卷根就停：跨卷继续向上找没有意义，也容易把别的卷上的仓库认成自己的
		if volRoot != "" && sameDir(dir, volRoot) {
			return GitInfo{}
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return GitInfo{}
		}
		dir = parent
	}
}

// sameDir 比较两个目录是否相同；Windows 上路径大小写不敏感。
func sameDir(a, b string) bool {
	a, b = filepath.Clean(a), filepath.Clean(b)
	if runtime.GOOS == "windows" {
		return strings.EqualFold(a, b)
	}
	return a == b
}
