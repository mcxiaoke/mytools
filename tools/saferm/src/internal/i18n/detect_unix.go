//go:build !windows

package i18n

import (
	"os"
	"strings"
)

// Detect 按 POSIX 惯例从环境变量判断语言：中文环境返回 zh-CN，其它返回 en。
func Detect() string {
	for _, key := range []string{"LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG"} {
		v := strings.TrimSpace(os.Getenv(key))
		if v == "" {
			continue
		}
		if strings.HasPrefix(strings.ToLower(v), "zh") {
			return "zh-CN"
		}
		return "en"
	}
	return "en"
}
