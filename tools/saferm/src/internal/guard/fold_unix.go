//go:build !windows

package guard

// platformFold 在非 Windows 平台上是恒等操作：路径按字节比较、大小写敏感。
func platformFold(p string) string { return p }
