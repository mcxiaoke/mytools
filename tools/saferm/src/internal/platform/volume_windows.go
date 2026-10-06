//go:build windows

package platform

import (
	"fmt"
	"strings"

	"golang.org/x/sys/windows"
)

// maxVolumePath 是 GetVolumePathNameW 的起始缓冲区大小（按 UTF-16 码元计）。
// 文档要求至少 MAX_PATH；不够时函数返回 ERROR_FILENAME_EXCED_RANGE，我们扩容重试。
const maxVolumePath = 260

// VolumeRoot 返回 p 所在卷的挂载点，形如 `D:\`。
//
// p 与其父目录都可以尚不存在：内部会退到最深的已存在祖先再问系统。
// 用 GetVolumePathNameW 而不是"取盘符"，是为了正确支持挂载到文件夹的卷
// （例如 C:\Mount\Data 是另一个卷，按盘符猜会猜错）。
func VolumeRoot(p string) (string, error) {
	target, err := renameVolumeTarget(p)
	if err != nil {
		return "", err
	}
	return volumeRootOf(target)
}

func volumeRootOf(existing string) (string, error) {
	path16, err := windows.UTF16PtrFromString(existing)
	if err != nil {
		return "", fmt.Errorf("encode path %q: %w", existing, err)
	}
	buf := make([]uint16, maxVolumePath)
	for {
		err := windows.GetVolumePathName(path16, &buf[0], uint32(len(buf)))
		if err == nil {
			return windows.UTF16ToString(buf), nil
		}
		if err == windows.ERROR_FILENAME_EXCED_RANGE && len(buf) < 32768 {
			buf = make([]uint16, len(buf)*2)
			continue
		}
		return "", fmt.Errorf("GetVolumePathNameW(%q): %w", existing, err)
	}
}

// SameVolume 判断两个路径是否位于同一个卷。
//
// 这是"能否用一次 rename 完成移动"的唯一判据：不同卷时 os.Rename 会返回
// ERROR_NOT_SAME_DEVICE，而本工具刻意不做"复制+删除"的降级（见设计文档 §3.3）。
func SameVolume(a, b string) (bool, error) {
	ra, err := VolumeRoot(a)
	if err != nil {
		return false, err
	}
	rb, err := VolumeRoot(b)
	if err != nil {
		return false, err
	}
	// Windows 路径大小写不敏感
	return strings.EqualFold(ra, rb), nil
}

// ExpandShortName 把路径中的 8.3 短名展开成磁盘上的真名。
//
// 必要性：`D:\PROGRA~1` 与 `D:\Program Files` 字面比较并不相等，
// 只做字符串比较会让"cwd 祖先""卷根"等护栏判定被短名绕过（设计文档 §5.0）。
// 短名不是重解析点，展开它不违反"不解析最后一级符号链接"的语义。
//
// GetLongPathNameW 要求路径存在；不存在时返回错误，由上层按"拒绝"处理，不猜。
func ExpandShortName(p string) (string, error) {
	abs, err := AbsClean(p)
	if err != nil {
		return "", err
	}
	path16, err := windows.UTF16PtrFromString(abs)
	if err != nil {
		return "", fmt.Errorf("encode path %q: %w", abs, err)
	}
	buf := make([]uint16, maxVolumePath)
	for {
		n, err := windows.GetLongPathName(path16, &buf[0], uint32(len(buf)))
		if err == nil {
			return windows.UTF16ToString(buf[:n]), nil
		}
		if err == windows.ERROR_FILENAME_EXCED_RANGE && len(buf) < 32768 {
			buf = make([]uint16, len(buf)*2)
			continue
		}
		return "", fmt.Errorf("GetLongPathNameW(%q): %w", abs, err)
	}
}

// IsReparsePoint 判断 p 自身是否是重解析点（符号链接 / junction / 挂载点）。
//
// 扫描时必须用它来"不向下递归"——Go 的 filepath.WalkDir 对 Windows junction
// 的处理依赖 Lstat 的 Mode，不能假定它一定被识别为符号链接，因此这里直接问系统。
func IsReparsePoint(p string) (bool, error) {
	path16, err := windows.UTF16PtrFromString(p)
	if err != nil {
		return false, fmt.Errorf("encode path %q: %w", p, err)
	}
	attrs, err := windows.GetFileAttributes(path16)
	if err != nil {
		return false, fmt.Errorf("GetFileAttributesW(%q): %w", p, err)
	}
	if attrs == windows.INVALID_FILE_ATTRIBUTES {
		return false, fmt.Errorf("GetFileAttributesW(%q): invalid attributes", p)
	}
	return attrs&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0, nil
}

// SetHidden 给已存在的文件或目录加上隐藏属性（保留原有属性）。
//
// 用途：回收根不该出现在文件管理器里，也不该被 Everything/OneDrive 之类反复扫描。
func SetHidden(p string) error {
	path16, err := windows.UTF16PtrFromString(p)
	if err != nil {
		return fmt.Errorf("encode path %q: %w", p, err)
	}
	attrs, err := windows.GetFileAttributes(path16)
	if err != nil {
		return fmt.Errorf("GetFileAttributesW(%q): %w", p, err)
	}
	if attrs == windows.INVALID_FILE_ATTRIBUTES {
		return fmt.Errorf("GetFileAttributesW(%q): invalid attributes", p)
	}
	if attrs&windows.FILE_ATTRIBUTE_HIDDEN != 0 {
		return nil
	}
	if err := windows.SetFileAttributes(path16, attrs|windows.FILE_ATTRIBUTE_HIDDEN); err != nil {
		return fmt.Errorf("SetFileAttributesW(%q): %w", p, err)
	}
	return nil
}
