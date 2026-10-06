//go:build windows

package guard

import "os"

// volumeRootSystemNames 是"位于任意卷根下、必须保护"的系统目录名。
// $Recycle.Bin 与 System Volume Information 每个卷都有，删了会连带系统数据。
var volumeRootSystemNames = []string{
	"$Recycle.Bin",
	"System Volume Information",
	"Recovery",
}

// builtinProtected 返回内置的系统/用户关键路径。
//
// 注意两种力度的区别（设计文档 §5.1 第 7 条）：
//   - subtree = true：该目录及其**全部内容**都不可删（系统目录）。
//   - subtree = false：只保护该目录**自身**（用户主目录），里面的文件必须能正常删，
//     否则这个工具连"删我下载目录里的文件"都做不到。
//
// 这份清单是硬编码的，配置文件只能追加、不能移除。
func builtinProtected() []protectedPath {
	var out []protectedPath

	// 子树保护：系统目录
	for _, name := range []string{"SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramData"} {
		if v := os.Getenv(name); v != "" {
			out = append(out, protectedPath{Path: v, Subtree: true})
		}
	}

	// 仅自身保护：用户主目录
	if v := os.Getenv("USERPROFILE"); v != "" {
		out = append(out, protectedPath{Path: v, Subtree: false})
	}

	return out
}
