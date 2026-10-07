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
	// InRepo 表示目标位于某个版本库工作区内（含目标自身就是仓库根的情况）。
	// 它**只用于展示**：告诉用户"这里面的东西可能还没推到远端"。
	InRepo   bool
	RepoRoot string

	// IsRepoRoot 表示目标自身就是版本库根（直接含 .git/.hg/.svn）。
	//
	// 只有它才升危险级，InRepo 不升 —— 理由见 Decide 的注释。
	IsRepoRoot bool
}

// vcsMarkers 是"这个目录就是仓库根"的标记。
var vcsMarkers = []string{".git", ".hg", ".svn"}

// hasVCSMarker 判断目录自身是否直接含版本库元数据（只要几次 stat，不遍历）。
func hasVCSMarker(dir string) bool {
	for _, marker := range vcsMarkers {
		if _, err := os.Lstat(filepath.Join(dir, marker)); err == nil {
			return true
		}
	}
	return false
}

// DetectGit 判断目标与版本库的关系。
//
// 两种信号的代价差别很大，所以分开算：
//   - IsRepoRoot：只要 3 次 stat，目标是目录且直接含 .git/.hg/.svn；
//   - InRepo/RepoRoot：需要逐级向上找，可能要走好几层。
func DetectGit(target string) GitInfo {
	dir := target
	if info, err := os.Lstat(target); err != nil || !info.IsDir() {
		// 目标是文件（或不存在）：从它所在的目录开始找
		dir = filepath.Dir(target)
	}

	// 目标自身就是仓库根——这是唯一会升危险级的情况。
	// 护栏第 8 条本来就会拒绝它（可被 --allow-dangerous 绕过），
	// 所以这里的作用是"护栏被绕过/被配置关掉之后"的第二道防线。
	if dir == target && hasVCSMarker(dir) {
		return GitInfo{InRepo: true, RepoRoot: dir, IsRepoRoot: true}
	}

	// 卷根只算一次：Linux 上每次都要解析挂载表，放在循环里是浪费
	volRoot, err := platform.VolumeRoot(dir)
	if err != nil {
		volRoot = ""
	}

	for {
		if hasVCSMarker(dir) {
			return GitInfo{InRepo: true, RepoRoot: dir}
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
