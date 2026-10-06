package guard

import (
	"path/filepath"
	"strings"
)

// pathKey 把路径变换成"用于比较的键"。
//
// 比较必须比字符串相等更宽松，否则护栏会被等价写法绕过（设计文档 §5.0）：
//   - Windows：大小写不敏感；分隔符统一；每个分量去掉尾随的点与空格
//     —— Win32 解析路径时本来就会去掉，所以 `D:\repo ` 实际就是 `D:\repo`，
//     只比字符串会绕过"== cwd"。
//   - 非 Windows：按字节比较，大小写敏感，不做任何变形。
//
// 键空间里统一用 `/` 作分隔符，且除根外不留尾随分隔符。
func pathKey(p string) string {
	if p == "" {
		return ""
	}
	k := platformFold(filepath.ToSlash(filepath.Clean(p)))
	if k == "/" {
		return k
	}
	if len(k) > 1 {
		k = strings.TrimRight(k, "/")
	}
	if k == "" {
		return "/"
	}
	return k
}

// equalPath 判断两个已规范化的绝对路径是否指向同一位置。
func equalPath(a, b string) bool { return pathKey(a) == pathKey(b) }

// isAncestor 判断 anc 是否是 p 的严格上级（按路径分量对齐，避免 /data 命中 /data2）。
func isAncestor(anc, p string) bool {
	a, b := pathKey(anc), pathKey(p)
	if a == "" || a == b {
		return false
	}
	if a == "/" {
		return true
	}
	return strings.HasPrefix(b, a+"/")
}

// isInsideInclusive 判断 p 是否位于 root 之内（含 root 自身）。
// root 为空表示"没有可比的根"，一律返回 false。
func isInsideInclusive(p, root string) bool {
	if root == "" {
		return false
	}
	a, b := pathKey(root), pathKey(p)
	if a == b {
		return true
	}
	if a == "/" {
		return true
	}
	return strings.HasPrefix(b, a+"/")
}
