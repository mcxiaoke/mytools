//go:build windows

package i18n

import (
	"strings"

	"golang.org/x/sys/windows"
)

// muiLanguageName 是 GetUserPreferredUILanguages 的标志位：
// 要求返回 BCP-47 语言名（如 "zh-CN"）而不是数字 LANGID。
const muiLanguageName = 0x8

// Detect 返回系统 UI 语言：中文系统返回 zh-CN，其它返回 en。
//
// 用"用户首选 UI 语言"（即显示语言）而不是区域格式，与"中文系统默认中文"
// 的需求一致；拿不到时保守回退英文。
func Detect() string {
	langs, err := windows.GetUserPreferredUILanguages(muiLanguageName)
	if err != nil || len(langs) == 0 {
		return "en"
	}
	if strings.HasPrefix(strings.ToLower(strings.TrimSpace(langs[0])), "zh") {
		return "zh-CN"
	}
	return "en"
}
