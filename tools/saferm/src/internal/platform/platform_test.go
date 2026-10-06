package platform

import (
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

// 这些测试刻意不依赖具体盘符/挂载点：换机器换 CI 都应该过。
// 真正"跨卷必须被拒绝"的端到端断言放在 tests/ 里，用真实存在的第二块盘做。

func TestAbsCleanIsAbsoluteAndLiteral(t *testing.T) {
	got, err := AbsClean(filepath.Join("a", "..", "b", "c"))
	if err != nil {
		t.Fatalf("AbsClean: %v", err)
	}
	if !filepath.IsAbs(got) {
		t.Fatalf("AbsClean returned non-absolute path %q", got)
	}
	want := filepath.Join(mustGetwd(t), "b", "c")
	if got != want {
		t.Fatalf("AbsClean = %q, want %q", got, want)
	}
}

func TestVolumeRootOnExistingAndMissingPath(t *testing.T) {
	wd := mustGetwd(t)

	// 已存在的路径
	root, err := VolumeRoot(wd)
	if err != nil {
		t.Fatalf("VolumeRoot(%q): %v", wd, err)
	}
	if root == "" {
		t.Fatalf("VolumeRoot(%q) returned empty mount point", wd)
	}
	if !filepath.IsAbs(root) {
		t.Fatalf("VolumeRoot(%q) = %q, want absolute", wd, root)
	}

	// 不存在的路径（模拟尚未创建的回收根）必须同样能判定卷
	missing := filepath.Join(wd, "definitely-not-here-9f3a2c", "deeper", "leaf")
	root2, err := VolumeRoot(missing)
	if err != nil {
		t.Fatalf("VolumeRoot(%q): %v", missing, err)
	}
	if root2 != root {
		t.Fatalf("VolumeRoot of missing path = %q, want same volume as cwd %q", root2, root)
	}
}

func TestSameVolumeSamePathAndMissingSibling(t *testing.T) {
	wd := mustGetwd(t)

	// 自己与自己必然同卷
	same, err := SameVolume(wd, wd)
	if err != nil {
		t.Fatalf("SameVolume: %v", err)
	}
	if !same {
		t.Fatalf("SameVolume(%q, %q) = false, want true", wd, wd)
	}

	// 同卷下尚未创建的子路径，也应判为同卷（回收根就是这样）
	missing := filepath.Join(wd, "definitely-not-here-9f3a2c")
	same, err = SameVolume(wd, missing)
	if err != nil {
		t.Fatalf("SameVolume(missing): %v", err)
	}
	if !same {
		t.Fatalf("SameVolume(%q, %q) = false, want true", wd, missing)
	}
}

func TestSameVolumeAcrossVolumesIsFalse(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("非 Windows 平台挂载点因机器而异，跨卷断言放在 tests/ 里用真实挂载点做")
	}
	a, aOK := existingVolume("C:\\")
	b, bOK := existingVolume("D:\\")
	if !aOK || !bOK {
		t.Skip("本机没有同时存在 C: 与 D:，跳过跨卷断言")
	}
	same, err := SameVolume(a, b)
	if err != nil {
		t.Fatalf("SameVolume(%q, %q): %v", a, b, err)
	}
	if same {
		t.Fatalf("SameVolume(%q, %q) = true, want false (不同盘符必须是不同卷)", a, b)
	}
}

func TestExpandShortNameIsIdempotentOnExistingPath(t *testing.T) {
	wd := mustGetwd(t)
	got, err := ExpandShortName(wd)
	if err != nil {
		t.Fatalf("ExpandShortName(%q): %v", wd, err)
	}
	// 展开结果必须仍是绝对路径，且再展开一次不再变化
	again, err := ExpandShortName(got)
	if err != nil {
		t.Fatalf("ExpandShortName(%q): %v", got, err)
	}
	if again != got {
		t.Fatalf("ExpandShortName not idempotent: %q -> %q", got, again)
	}
}

func TestIsReparsePointOnRealDirIsFalse(t *testing.T) {
	wd := mustGetwd(t)
	isLink, err := IsReparsePoint(wd)
	if err != nil {
		t.Fatalf("IsReparsePoint(%q): %v", wd, err)
	}
	if isLink {
		t.Fatalf("IsReparsePoint(%q) = true, 普通目录不应被判为链接/junction", wd)
	}
}

func TestSetHiddenDoesNotBreakNormalUse(t *testing.T) {
	// 只验证不报错、且不影响后续读写；Linux 上这是空操作。
	dir := t.TempDir()
	if err := SetHidden(dir); err != nil {
		t.Fatalf("SetHidden(%q): %v", dir, err)
	}
	if _, err := os.Stat(dir); err != nil {
		t.Fatalf("目录在 SetHidden 之后不可访问: %v", err)
	}
}

func mustGetwd(t *testing.T) string {
	t.Helper()
	wd, err := os.Getwd()
	if err != nil {
		t.Fatalf("getwd: %v", err)
	}
	return wd
}

func existingVolume(path string) (string, bool) {
	if _, err := os.Stat(path); err != nil {
		return "", false
	}
	return path, true
}
