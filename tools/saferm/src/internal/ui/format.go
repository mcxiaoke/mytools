package ui

import (
	"fmt"
	"strconv"
	"strings"
)

// HumanBytes 把字节数写成便于阅读的形式（1024 进制，保留一位小数）。
//
// truncated 为真时前缀 "≥"：扫描被早退计数截断时，数值只是下界，
// 不能让人以为"就这么大"。
func HumanBytes(n int64, truncated bool) string {
	prefix := ""
	if truncated {
		prefix = "≥ "
	}
	const unit = 1024
	if n < unit {
		return fmt.Sprintf("%s%d B", prefix, n)
	}
	units := []string{"KiB", "MiB", "GiB", "TiB", "PiB"}
	value := float64(n)
	for _, u := range units {
		value /= unit
		if value < unit {
			return fmt.Sprintf("%s%.1f %s", prefix, value, u)
		}
	}
	return fmt.Sprintf("%s%.1f EiB", prefix, value/unit)
}

// HumanCount 给整数加千位分隔符，避免"1234567 个文件"这种读不清的数字。
func HumanCount(n int64) string {
	s := strconv.FormatInt(n, 10)
	neg := strings.HasPrefix(s, "-")
	if neg {
		s = s[1:]
	}
	var b strings.Builder
	for i, r := range s {
		if i > 0 && (len(s)-i)%3 == 0 {
			b.WriteByte(',')
		}
		b.WriteRune(r)
	}
	if neg {
		return "-" + b.String()
	}
	return b.String()
}
