//go:build windows

package platform

import (
	"path/filepath"
	"strings"

	goi18n "github.com/nicksnyder/go-i18n/v2/i18n"

	"saferm/internal/i18n"
)

// winReservedDevices 是 Win32 的保留设备名（不区分大小写）。
// 与 Win32 一致：后面跟任意扩展名也算（nul.txt、aux.log 与 nul 同义），
// Go 1.26 的 filepath.Abs 对它们的改写行为也与此一致。
var winReservedDevices = map[string]bool{
	"CON": true, "PRN": true, "AUX": true, "NUL": true,
	"COM1": true, "COM2": true, "COM3": true, "COM4": true, "COM5": true,
	"COM6": true, "COM7": true, "COM8": true, "COM9": true,
	"LPT1": true, "LPT2": true, "LPT3": true, "LPT4": true, "LPT5": true,
	"LPT6": true, "LPT7": true, "LPT8": true, "LPT9": true,
}

// checkLiteralPath 检查路径的最终分量是否为 Windows 保留设备名。
//
// 必须在 filepath.Abs 之前调用：Go 1.26 起它会把这类路径整个改写成设备
// 命名空间形式（`C:\work\nul` → `\\.\nul`，目录前缀一并丢失），而 Win32 API
// 对普通路径又会把保留名解析成设备——两条路都访问不到"真实存在、名字恰好
// 叫 nul 的文件"。因此本工具不做任何猜测，直接拒绝该路径（设计文档 §5.3）。
// 这与 Windows 自带 del 的行为一致：删不掉，但绝不误删别的东西。
func checkLiteralPath(p string) error {
	base := strings.ToUpper(filepath.Base(filepath.Clean(p)))
	if i := strings.IndexByte(base, '.'); i >= 0 {
		base = base[:i]
	}
	base = strings.TrimRight(base, " ")
	if winReservedDevices[base] {
		return i18n.E(&goi18n.Message{
			ID:    "ErrReservedDevice",
			Other: "{{.V}} 是 Windows 保留设备名（con/prn/aux/nul/com1-9/lpt1-9），任何 API 都无法按字面访问它；本工具拒绝处理该路径，原数据未改动",
		}, i18n.Data{"V": base})
	}
	return nil
}
