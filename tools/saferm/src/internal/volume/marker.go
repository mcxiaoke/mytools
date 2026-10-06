package volume

import (
	"bufio"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// MarkerName 是回收根的身份标记文件名（设计文档 §4.7）。
//
// 为什么必须有它：回收根是个会被写入大量用户文件的目录。如果配置笔误写成了
// `D:\` 或某个已有数据目录，本工具会把文件"镜像"进一个真实目录、把里面搅乱，
// 后续的 purge 更是会清空它。有标记就能在动手之前识别出"这不是我的地盘"。
const MarkerName = ".saferm-trash-root"

// markerMagic 是标记文件的第一行，用于区分"我们的标记"与"恰好同名的别的文件"。
const markerMagic = "saferm-trash-root"

// Marker 是标记文件的内容。
type Marker struct {
	Version   string
	CreatedAt string
}

// MarkerPath 返回某个回收根的标记文件路径。
func MarkerPath(root string) string { return filepath.Join(root, MarkerName) }

// ReadMarker 读取标记。第二个返回值为 false 表示"没有标记"（不是错误）。
//
// 文件存在但第一行不是 magic 时同样视为"没有标记"：
// 宁可多拒绝一次，也不要把别人的目录认成自己的。
func ReadMarker(root string) (Marker, bool, error) {
	f, err := os.Open(MarkerPath(root))
	if err != nil {
		if os.IsNotExist(err) {
			return Marker{}, false, nil
		}
		return Marker{}, false, fmt.Errorf("open marker of %q: %w", root, err)
	}
	defer f.Close()

	var m Marker
	first := true
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" {
			continue
		}
		if first {
			first = false
			if line != markerMagic {
				return Marker{}, false, nil
			}
			continue
		}
		key, value, ok := strings.Cut(line, "=")
		if !ok {
			continue
		}
		value = strings.Trim(strings.TrimSpace(value), `"`)
		switch strings.TrimSpace(key) {
		case "version":
			m.Version = value
		case "created_at":
			m.CreatedAt = value
		}
	}
	if err := sc.Err(); err != nil {
		return Marker{}, false, fmt.Errorf("read marker of %q: %w", root, err)
	}
	if first {
		return Marker{}, false, nil
	}
	return m, true, nil
}

// WriteMarker 写入（或覆盖）标记文件。
func WriteMarker(root string, m Marker) error {
	if m.Version == "" {
		m.Version = "unknown"
	}
	content := fmt.Sprintf("%s\nversion = %q\ncreated_at = %q\n", markerMagic, m.Version, m.CreatedAt)
	if err := os.WriteFile(MarkerPath(root), []byte(content), 0o644); err != nil {
		return fmt.Errorf("write marker of %q: %w", root, err)
	}
	return nil
}
