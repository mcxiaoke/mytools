package volume

import (
	"os"
	"time"

	goi18n "github.com/nicksnyder/go-i18n/v2/i18n"

	"saferm/internal/i18n"
	"saferm/internal/platform"
)

// EnsureRoot 在确认之后创建并"认领"回收根：建目录、写标记、置隐藏属性。
//
// 这是本包唯一会写盘的操作，调用方必须先拿到用户确认，dry-run 下绝不能调用。
// 它仍然会重新做一次安全校验：从解析到确认之间目录可能被别的东西占用或塞满，
// 此处不重新检查就会出现"刚校验完就被污染"的窗口。
//
// 返回的 warnings 是"不影响可用性但用户应该知道"的信息（例如隐藏属性设置失败）。
func EnsureRoot(root, version string) (warnings []string, err error) {
	abs, err := platform.AbsClean(root)
	if err != nil {
		return nil, err
	}

	info, statErr := os.Lstat(abs)
	switch {
	case statErr == nil && !info.IsDir():
		return nil, i18n.E(&goi18n.Message{ID: "ErrEnsureNotDir", Other: "回收目录 {{.V}} 已存在且不是目录，拒绝使用"}, i18n.Data{"V": abs})
	case os.IsNotExist(statErr):
		if mkErr := os.MkdirAll(abs, 0o700); mkErr != nil {
			return nil, i18n.E(&goi18n.Message{ID: "ErrEnsureCreate", Other: "创建回收目录 {{.V}} 失败：{{.Err}}"}, i18n.Data{"V": abs, "Err": mkErr.Error()})
		}
	case statErr != nil:
		return nil, i18n.E(&goi18n.Message{ID: "ErrEnsureAccess", Other: "访问回收目录 {{.V}} 失败：{{.Err}}"}, i18n.Data{"V": abs, "Err": statErr.Error()})
	}

	// 认领前复核：非空且无标记的目录一律拒绝（§4.7 规则 4）。
	entries, err := os.ReadDir(abs)
	if err != nil {
		return nil, i18n.E(&goi18n.Message{ID: "ErrEnsureRead", Other: "读取回收目录 {{.V}} 失败：{{.Err}}"}, i18n.Data{"V": abs, "Err": err.Error()})
	}
	if len(entries) > 0 {
		if _, ok, mErr := ReadMarker(abs); mErr != nil {
			return nil, mErr
		} else if !ok {
			return nil, i18n.E(&goi18n.Message{
				ID:    "ErrEnsureNoMarker",
				Other: "回收目录 {{.Root}} 非空且没有 {{.Marker}} 标记，拒绝使用；请检查配置是否写成了别的目录",
			}, i18n.Data{"Root": abs, "Marker": MarkerName})
		}
		return nil, nil
	}

	if err := WriteMarker(abs, Marker{
		Version:   version,
		CreatedAt: time.Now().Format(time.RFC3339),
	}); err != nil {
		return nil, err
	}

	// 隐藏是为了不让文件管理器和索引/同步工具反复扫描它。
	// 失败不影响功能，但要让用户知道 —— 尤其 Windows 上回收根若在同步目录里，
	// 不排除会被上传。
	if err := platform.SetHidden(abs); err != nil {
		warnings = append(warnings, i18n.T(&goi18n.Message{
			ID: "WarnHideFailed", Other: "未能给回收目录 {{.V}} 设置隐藏属性：{{.Err}}",
		}, i18n.Data{"V": abs, "Err": err.Error()}))
	}
	return warnings, nil
}
