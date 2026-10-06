//go:build windows

package platform

import (
	"fmt"

	"golang.org/x/sys/windows"
)

// ListVolumes 返回本机当前存在的卷挂载点，形如 `C:\`、`D:\`。
//
// 只列出"真的能访问"的卷：GetLogicalDrives 会把空读卡器、断开的网络驱动器
// 也算进来，这类盘符 Lstat 会失败，这里直接过滤掉。
func ListVolumes() ([]string, error) {
	mask, err := windows.GetLogicalDrives()
	if err != nil {
		return nil, fmt.Errorf("GetLogicalDrives: %w", err)
	}
	var out []string
	for i := 0; i < 26; i++ {
		if mask&(1<<uint(i)) == 0 {
			continue
		}
		root := string(rune('A'+i)) + ":\\"
		if _, statErr := volumeRootOf(root); statErr != nil {
			continue
		}
		// 再确认能真的访问到（空读卡器能过 GetVolumePathNameW，但打不开）
		buf, err := windows.UTF16PtrFromString(root)
		if err != nil {
			continue
		}
		h, err := windows.CreateFile(buf, 0, windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE,
			nil, windows.OPEN_EXISTING, windows.FILE_FLAG_BACKUP_SEMANTICS, 0)
		if err != nil {
			continue
		}
		_ = windows.CloseHandle(h)
		out = append(out, root)
	}
	return out, nil
}

// fileAddSubdirectory 是"在该目录下创建子目录"的访问权限。
//
// x/sys/windows 只导出了 FILE_WRITE_DATA（= 0x2，对应创建文件），
// 而创建子目录对应的是 0x4（Win32 里也叫 FILE_APPEND_DATA）。
// 两者在盘根上权限并不相同：普通用户在 C:\ 根允许建目录、但不允许建文件，
// 所以只用 GENERIC_WRITE 探测会把 C:\ 误报成不可写。
const fileAddSubdirectory = 0x00000004

// IsWritableDir 判断"能否在该目录下安放回收根"，即能否创建子目录与文件。
//
// 做法是"以写权限打开目录但不写任何东西"，因此没有任何副作用 ——
// `saferm where` 承诺只读不写，不能靠"创建一个探测文件再删掉"来实现
// （那既破坏了只读承诺，也违背了 C8 的"代码里不出现删除调用"）。
//
// 先试 GENERIC_WRITE（已存在目录的常规情形），失败再试只建子目录所需的权限，
// 以覆盖"盘根只许建目录、不许建文件"的情况。
func IsWritableDir(dir string) (bool, error) {
	if ok, err := canOpenDirWith(dir, windows.GENERIC_WRITE); err != nil {
		return false, err
	} else if ok {
		return true, nil
	}
	return canOpenDirWith(dir, fileAddSubdirectory)
}

func canOpenDirWith(dir string, access uint32) (bool, error) {
	p, err := windows.UTF16PtrFromString(dir)
	if err != nil {
		return false, fmt.Errorf("encode path %q: %w", dir, err)
	}
	h, err := windows.CreateFile(p, access,
		windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE|windows.FILE_SHARE_DELETE,
		nil, windows.OPEN_EXISTING, windows.FILE_FLAG_BACKUP_SEMANTICS, 0)
	if err != nil {
		// 打不开就是不可写（权限不足 / 只读介质 / 已被删除）；
		// 具体原因交给统一文案，这里只需回答"行不行"。
		return false, nil
	}
	_ = windows.CloseHandle(h)
	return true, nil
}
