//go:build windows

package guard

import (
	"path/filepath"
	"strings"
	"testing"
)

// 保留设备名目标（nul/con/aux…）必须在预检阶段被干净拒绝：
// 检查发生在路径规范化层，任何开关都绕不过，也不触碰文件系统。
// 与 Windows 自带 del 的行为一致——删不掉，但绝不误删别的东西。
func TestReservedDeviceNameTargetIsRejected(t *testing.T) {
	base := t.TempDir()
	// 无需真实创建 nul 文件：拒绝发生在路径字面检查层，早于任何 stat
	target := filepath.Join(base, "nul")

	for _, allowDangerous := range []bool{false, true} {
		rep, err := Check([]string{target}, Options{Cwd: base, AllowDangerous: allowDangerous})
		if err != nil {
			t.Fatalf("Check: %v", err)
		}
		if !rep.Failed() {
			t.Fatalf("保留设备名目标应被拒绝（allowDangerous=%v）", allowDangerous)
		}
		if len(rep.Targets) != 0 {
			t.Fatalf("不应产生任何可移动目标，实际 %+v", rep.Targets)
		}
		msg := rep.Violations.Error()
		if !strings.Contains(msg, "保留设备名") || !strings.Contains(msg, "原数据未改动") {
			t.Errorf("拒绝文案应说明原因与结果：\n%s", msg)
		}
	}
}
