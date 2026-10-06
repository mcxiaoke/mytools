//go:build windows

package trash

import (
	"errors"

	"golang.org/x/sys/windows"
)

// isCrossDevice 判断 rename 失败是否因为"跨卷"。
//
// 跨卷是本工具刻意不支持的情形（不做复制+删除降级），
// 所以要在文案里说清楚，而不是甩一个原始系统错误。
func isCrossDevice(err error) bool {
	return errors.Is(err, windows.ERROR_NOT_SAME_DEVICE)
}
