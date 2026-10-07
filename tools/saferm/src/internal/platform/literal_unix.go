//go:build !windows

package platform

// checkLiteralPath 在非 Windows 平台上是空操作：nul/con 等在那里是合法文件名。
func checkLiteralPath(p string) error { return nil }
