// Package ui 负责"跟人打交道"的部分：扫描统计、风险分级、交互确认。
//
// 它刻意不碰任何文件系统写操作：扫描只读、确认只问。真正动手的是 trash 包。
package ui

import (
	"os"
	"path/filepath"

	"saferm/internal/platform"
)

// ScanStats 是一个目标的只读统计结果。
type ScanStats struct {
	Files int64
	Bytes int64
	Dirs  int64
	// Truncated 表示因为达到阈值而提前终止，数值只是下界。
	Truncated bool
	// Incomplete 表示遇到无权限/IO 错误，数值不可信。按最坏情况处理：
	// 一旦不完整就强制要求确认，绝不因为"数不出来"而放行。
	Incomplete bool
	ErrCount   int64
}

// ScanLimits 限定扫描的代价。达到任一上限就提前终止（早退计数），
// 避免在 node_modules 这种几十万文件的目录上卡住。
type ScanLimits struct {
	MaxFiles int64
	MaxBytes int64
}

func (l ScanLimits) reached(s ScanStats) bool {
	if l.MaxFiles > 0 && s.Files+s.Dirs >= l.MaxFiles {
		return true
	}
	if l.MaxBytes > 0 && s.Bytes >= l.MaxBytes {
		return true
	}
	return false
}

// Scan 统计 target 的内容规模。
//
// 两个硬性约束：
//   - **不跟随符号链接 / junction**：Go 的 WalkDir 对 junction 的处理依赖 Lstat 的
//     Mode，不能假定它一定被识别为链接，所以这里自己判断重解析点，
//     只把链接本身算作 1 个条目。
//   - 遇到错误不中断，只记 Incomplete 与计数：扫描结果只影响"要不要追问"，
//     不该让一次权限问题把整批操作拦死；但"数不出来"必须按最坏情况处理。
func Scan(target string, limits ScanLimits) ScanStats {
	var stats ScanStats

	info, err := os.Lstat(target)
	if err != nil {
		stats.Incomplete = true
		stats.ErrCount = 1
		return stats
	}

	// 顶层是链接或普通文件：只算 1 个条目
	if !info.IsDir() || isLink(target) {
		stats.Files = 1
		if !isLink(target) {
			stats.Bytes = info.Size()
		}
		return stats
	}

	// 迭代式遍历，避免极深目录把调用栈压爆
	stack := []string{target}
	for len(stack) > 0 {
		dir := stack[len(stack)-1]
		stack = stack[:len(stack)-1]

		entries, err := os.ReadDir(dir)
		if err != nil {
			stats.Incomplete = true
			stats.ErrCount++
			continue
		}
		for _, e := range entries {
			child := filepath.Join(dir, e.Name())
			if e.IsDir() {
				stats.Dirs++
				if isLink(child) {
					// 链接本身算 1 个条目，绝不递归进去
					stats.Files++
				} else {
					stack = append(stack, child)
				}
			} else {
				stats.Files++
				if info, err := e.Info(); err == nil && info.Mode().IsRegular() {
					stats.Bytes += info.Size()
				}
			}

			// 早退计数：目录数也要纳入，否则一棵"全是空目录"的树会扫到底
			if limits.reached(stats) {
				stats.Truncated = true
				return stats
			}
		}
	}
	return stats
}

// isLink 判断条目本身是否是符号链接 / junction。
func isLink(p string) bool {
	info, err := os.Lstat(p)
	if err != nil {
		return false
	}
	if info.Mode()&os.ModeSymlink != 0 {
		return true
	}
	// Windows 上 junction 不一定带 ModeSymlink，直接问系统
	if link, err := platform.IsReparsePoint(p); err == nil {
		return link
	}
	return false
}
