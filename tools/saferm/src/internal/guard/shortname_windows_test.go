//go:build windows

package guard

import (
	"os"
	"path/filepath"
	"testing"

	"golang.org/x/sys/windows"
)

// 这组测试对应设计文档 §5.0 第 2 步（也是评审指出的那个"能绕过护栏的真漏洞"）：
// `D:\PROGRA~1` 与 `D:\Program Files` 字面并不相等，只做字符串比较的话，
// 攻击面/事故面就是"用短名拼出受保护路径或 cwd 的上级，从而绕过护栏"。
//
// 这里的断言分两层：先证明"字面比较确实会不同"（前提成立），
// 再证明 guard 仍然拒绝 —— 少了第一层，测试可能在短名被禁用时变成假通过。

func TestShortNameCannotBypassProtectedPath(t *testing.T) {
	long := `C:\Program Files`
	if _, err := os.Stat(long); err != nil {
		t.Skipf("本机没有 %s", long)
	}
	short := shortPath(t, long)

	if equalPath(short, long) {
		t.Skipf("本机 %s 没有生成 8.3 短名（短名与长名相同），跳过", long)
	}
	// 前提成立：纯字面比较会认为两者不同，这正是漏洞所在。
	if pathKey(short) == pathKey(long) {
		t.Fatalf("前提不成立：%q 与 %q 的 pathKey 应当不同", short, long)
	}

	rep, err := Check([]string{short}, Options{Cwd: t.TempDir()})
	if err != nil {
		t.Fatalf("Check: %v", err)
	}
	if !hasReason(rep.Violations, ReasonProtectedPath) {
		t.Fatalf("短名拼写的受保护路径必须被拒，实际 = %v（短名 %q）", reasonsOf(rep.Violations), short)
	}
}

func TestShortNameCannotBypassCwdAncestor(t *testing.T) {
	tmp := t.TempDir()
	// 名字刻意超过 8.3 长度，且不含会被展开规则特殊处理的前缀
	longDir := filepath.Join(tmp, "a-very-long-directory-name-0123456789")
	sub := filepath.Join(longDir, "sub")
	mustMkdir(t, sub)

	short := shortPath(t, longDir)
	if equalPath(short, longDir) {
		t.Skip("本卷未为新目录生成 8.3 短名，跳过")
	}
	if pathKey(short) == pathKey(longDir) {
		t.Fatalf("前提不成立：%q 与 %q 的 pathKey 应当不同", short, longDir)
	}

	// cwd 在短名目录的下级，用短名拼出它的上级：必须被第 5 条拦住
	rep, err := Check([]string{short}, Options{Cwd: sub})
	if err != nil {
		t.Fatalf("Check: %v", err)
	}
	if !hasReason(rep.Violations, ReasonAncestorOfCwd) {
		t.Fatalf("短名拼写的 cwd 上级必须被拒，实际 = %v（短名 %q，cwd %q）",
			reasonsOf(rep.Violations), short, sub)
	}
}

// shortPath 取 long 的 8.3 短名；本卷未启用 8.3 或无法取得时跳过测试。
func shortPath(t *testing.T, long string) string {
	t.Helper()
	p16, err := windows.UTF16PtrFromString(long)
	if err != nil {
		t.Fatalf("编码路径 %q: %v", long, err)
	}
	buf := make([]uint16, 512)
	for {
		n, err := windows.GetShortPathName(p16, &buf[0], uint32(len(buf)))
		if err == nil {
			return windows.UTF16ToString(buf[:n])
		}
		if err == windows.ERROR_FILENAME_EXCED_RANGE && len(buf) < 32768 {
			buf = make([]uint16, len(buf)*2)
			continue
		}
		t.Skipf("GetShortPathNameW(%q) 失败（本卷可能未启用 8.3）: %v", long, err)
		return ""
	}
}
