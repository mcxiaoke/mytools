//go:build !windows

package guard

import "os"

// volumeRootSystemNames 见 protected_windows.go 的同名变量说明。
// 非 Windows 平台没有这些目录，留空。
var volumeRootSystemNames []string

// builtinProtected 返回内置的系统/用户关键路径。
//
// subtree = true 表示"该目录及其全部内容"都不可删；false 表示只保护目录自身
// （用户主目录），里面的文件必须能正常删。
//
// 这份清单是硬编码的，配置文件只能追加、不能移除。
func builtinProtected() []protectedPath {
	subtree := []string{
		"/etc", "/usr", "/bin", "/sbin", "/boot", "/lib", "/lib64",
		"/var", "/dev", "/proc", "/sys", "/run",
	}

	var out []protectedPath
	for _, p := range subtree {
		out = append(out, protectedPath{Path: p, Subtree: true})
	}

	// 仅自身保护：用户主目录（等价于 Windows 的 %USERPROFILE%）
	out = append(out, protectedPath{Path: "/root", Subtree: false})
	if home := os.Getenv("HOME"); home != "" {
		out = append(out, protectedPath{Path: home, Subtree: false})
	}

	return out
}
