//go:build windows

package guard

import "strings"

// platformFold 在 Windows 上做大小写折叠与分量级的尾随点/空格裁剪。
func platformFold(p string) string {
	parts := strings.Split(p, "/")
	for i, s := range parts {
		parts[i] = strings.TrimRight(s, " .")
	}
	return strings.ToLower(strings.Join(parts, "/"))
}
