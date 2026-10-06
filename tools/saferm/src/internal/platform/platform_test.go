package platform

import (
	"os"
	"path/filepath"
	"runtime"
	"strings"
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

// 回归测试：卷挂载点自身的 VolumeRoot 必须是它自己。
//
// 曾经用 Dir(p) 求父目录的卷来"规避符号链接"，结果 /data 这类挂载点被误判成 /，
// 会让"目标是卷根"漏检、也会让回收根选错卷。挂载点的判定是纯字面的，不该取父目录。
func TestVolumeRootOfMountPointIsItself(t *testing.T) {
	var candidates []string
	if runtime.GOOS == "windows" {
		candidates = []string{"C:\\", "D:\\", "E:\\", "F:\\"}
	} else {
		candidates = []string{"/", "/proc"}
	}

	checked := 0
	for _, m := range candidates {
		if _, err := os.Stat(m); err != nil {
			continue
		}
		got, err := VolumeRoot(m)
		if err != nil {
			t.Fatalf("VolumeRoot(%q): %v", m, err)
		}
		if !strings.EqualFold(filepath.Clean(got), filepath.Clean(m)) {
			t.Errorf("VolumeRoot(%q) = %q，应当等于它自己", m, got)
		}
		checked++
	}
	if checked == 0 {
		t.Skip("没有可用的挂载点样本")
	}
}

// 网络共享（UNC）必须能正确解析出卷，否则"在网络盘上安全删除"这条需求就没法成立。
//
// 用环境变量 opt-in，避免在别的机器上因共享不可达而失败：
//
//	$env:SAFERM_TEST_UNC = '\\192.168.1.118\data\temp\devtest'
func TestVolumeRootOnUNCShare(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("UNC 路径只在 Windows 上有意义")
	}
	share := os.Getenv("SAFERM_TEST_UNC")
	if share == "" {
		t.Skip("未设置 SAFERM_TEST_UNC，跳过")
	}
	if _, err := os.Stat(share); err != nil {
		t.Skipf("共享不可达 %q: %v", share, err)
	}

	root, err := VolumeRoot(share)
	if err != nil {
		t.Fatalf("VolumeRoot(%q): %v", share, err)
	}
	if !strings.HasPrefix(root, `\\`) {
		t.Fatalf("VolumeRoot(%q) = %q，UNC 路径的卷挂载点应当也是 UNC 形式", share, root)
	}

	same, err := SameVolume(share, root)
	if err != nil {
		t.Fatalf("SameVolume: %v", err)
	}
	if !same {
		t.Fatalf("共享根 %q 应当与共享本身同卷", root)
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
