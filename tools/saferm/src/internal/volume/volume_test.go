package volume

import (
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"saferm/internal/guard"
	"saferm/internal/platform"
)

func mustMkdir(t *testing.T, dir string) {
	t.Helper()
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatalf("mkdir %q: %v", dir, err)
	}
}

func mustWrite(t *testing.T, path, content string) {
	t.Helper()
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatalf("write %q: %v", path, err)
	}
}

// cfgWithRoot 构造"给指定目标的卷挂一个显式回收根"的配置。
//
// 测试一律用显式配置：默认位置会落在真实卷根（C:\ 或 /）下，
// 那些路径既不该被测试创建，也依赖运行环境的权限。
func cfgWithRoot(t *testing.T, target, root string) Config {
	t.Helper()
	vol, err := platform.VolumeRoot(target)
	if err != nil {
		t.Fatalf("VolumeRoot(%q): %v", target, err)
	}
	return Config{Roots: map[string]string{Key(vol): root}, Version: "test"}
}

func TestMarkerRoundTripAndDetection(t *testing.T) {
	root := t.TempDir()

	if _, ok, err := ReadMarker(root); err != nil || ok {
		t.Fatalf("没有标记时应返回 ok=false, nil，实际 ok=%v err=%v", ok, err)
	}

	want := Marker{Version: "v9.9.9", CreatedAt: "2026-10-06T21:00:00+08:00"}
	if err := WriteMarker(root, want); err != nil {
		t.Fatalf("WriteMarker: %v", err)
	}
	got, ok, err := ReadMarker(root)
	if err != nil || !ok {
		t.Fatalf("写入后应能读到标记，实际 ok=%v err=%v", ok, err)
	}
	if got != want {
		t.Fatalf("标记内容 = %+v，期望 %+v", got, want)
	}
}

// 同名但内容不是我们的标记 → 视为"没有标记"。
// 宁可多拒绝一次，也不要把别人的目录认成自己的地盘。
func TestMarkerRejectsForeignFile(t *testing.T) {
	root := t.TempDir()
	mustWrite(t, MarkerPath(root), "this is someone else's file\nversion = \"x\"\n")

	if _, ok, err := ReadMarker(root); err != nil || ok {
		t.Fatalf("外来文件不应被认作标记，实际 ok=%v err=%v", ok, err)
	}
}

func TestResolvePrefersConfiguredRootAndReportsNeedCreate(t *testing.T) {
	base := t.TempDir()
	target := filepath.Join(base, "work", "a.txt")
	mustMkdir(t, filepath.Dir(target))
	mustWrite(t, target, "x")

	trash := filepath.Join(base, "trash")

	plans, viols, err := Resolve([]string{target}, cfgWithRoot(t, target, trash))
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if len(viols) > 0 {
		t.Fatalf("不应有拒绝：%v", viols.Error())
	}
	if len(plans) != 1 {
		t.Fatalf("期望 1 个落点，实际 %d", len(plans))
	}
	p := plans[0]
	if p.RootSource != "config" {
		t.Fatalf("RootSource = %q，期望 config", p.RootSource)
	}
	if !p.NeedCreate {
		t.Fatal("回收根尚不存在时应报告 NeedCreate=true（本包不创建任何目录）")
	}
	if filepath.Clean(p.TrashRoot) != filepath.Clean(trash) {
		t.Fatalf("TrashRoot = %q，期望 %q", p.TrashRoot, trash)
	}

	// 目录存在但为空 → 可以认领，仍然不需要创建
	mustMkdir(t, trash)
	plans, _, err = Resolve([]string{target}, cfgWithRoot(t, target, trash))
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if plans[0].NeedCreate {
		t.Fatal("空目录可被认领，不该再报告 NeedCreate")
	}

	// 空目录写入标记后照常可用
	if err := WriteMarker(trash, Marker{Version: "test"}); err != nil {
		t.Fatalf("WriteMarker: %v", err)
	}
	plans, _, err = Resolve([]string{target}, cfgWithRoot(t, target, trash))
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if len(plans) != 1 {
		t.Fatalf("有标记的目录必须可用，实际 %d 个落点", len(plans))
	}
}

// §4.7 规则 4：目录非空且没有标记 → 拒绝。
// 这条专门防配置笔误（把回收根写成 D:\ 或某个已有数据目录）。
func TestResolveRefusesNonEmptyUnmarkedRoot(t *testing.T) {
	base := t.TempDir()
	target := filepath.Join(base, "work", "a.txt")
	mustMkdir(t, filepath.Dir(target))
	mustWrite(t, target, "x")

	notATrash := filepath.Join(base, "important-data")
	mustMkdir(t, notATrash)
	mustWrite(t, filepath.Join(notATrash, "keep.txt"), "precious")

	plans, viols, err := Resolve([]string{target}, cfgWithRoot(t, target, notATrash))
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if len(plans) != 0 {
		t.Fatal("非空且无标记的目录不能用作回收根")
	}
	if !hasViolation(viols, guard.ReasonTrashUnusable) {
		t.Fatalf("期望 TrashUnusable，实际 = %v", violationsText(viols))
	}
	// 不能把已有数据删掉——这是本工具最容易造成二次事故的地方
	if _, err := os.Stat(filepath.Join(notATrash, "keep.txt")); err != nil {
		t.Fatalf("拒绝之后原有数据必须原封不动: %v", err)
	}
}

func TestResolveRefusesConfigRootEqualToVolumeRoot(t *testing.T) {
	vol := firstVolume(t)
	target := filepath.Join(vol, "saferm-volume-root-probe")
	// 不需要真的创建 target：VolumeRoot 会退到已存在的祖先
	cfg := Config{Roots: map[string]string{Key(vol): vol}, Version: "test"}

	plans, viols, err := Resolve([]string{target}, cfg)
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if len(plans) != 0 || !hasViolation(viols, guard.ReasonTrashUnusable) {
		t.Fatalf("卷根不能当回收根，实际 plans=%d viols=%v", len(plans), violationsText(viols))
	}
}

// §5.1 第 10 条：目标是回收根的祖先（回收根位于目标内部）。
func TestResolveRefusesTargetContainingRoot(t *testing.T) {
	base := t.TempDir()
	target := filepath.Join(base, "projects")
	mustMkdir(t, target)
	mustWrite(t, filepath.Join(target, "f.txt"), "x")

	innerRoot := filepath.Join(target, ".saferm-trash")

	plans, viols, err := Resolve([]string{target}, cfgWithRoot(t, target, innerRoot))
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if len(plans) != 0 || !hasViolation(viols, guard.ReasonAncestorOfTrash) {
		t.Fatalf("目标包含回收根时必须拒绝，实际 plans=%d viols=%v", len(plans), violationsText(viols))
	}
}

// §5.1 第 9 条：目标位于回收根之内。
// 回收根本身是"可用"的（带标记），这样才会走到与目标的关系判定，
// 而不是先被"不可用"拦下。
func TestResolveRefusesTargetInsideRoot(t *testing.T) {
	base := t.TempDir()
	trash := filepath.Join(base, "trash")
	target := filepath.Join(trash, "20261006-1", "old")
	mustMkdir(t, target)
	if err := WriteMarker(trash, Marker{Version: "test"}); err != nil {
		t.Fatalf("WriteMarker: %v", err)
	}

	plans, viols, err := Resolve([]string{target}, cfgWithRoot(t, target, trash))
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if len(plans) != 0 || !hasViolation(viols, guard.ReasonInsideTrash) {
		t.Fatalf("目标位于回收根内时必须拒绝，实际 plans=%d viols=%v", len(plans), violationsText(viols))
	}
}

// 跨卷必须被拒绝，且不能被任何开关绕过（本工具不做「复制+删除」降级）。
func TestResolveRefusesCrossVolume(t *testing.T) {
	a, b := twoVolumes(t)
	target := t.TempDir() // 与 a 同卷（TempDir 在系统盘/根卷上）
	volOfTarget, err := platform.VolumeRoot(target)
	if err != nil {
		t.Fatalf("VolumeRoot: %v", err)
	}

	// 挑一个与 target 不同卷的挂载点作为回收根所在卷
	other := a
	if same, _ := platform.SameVolume(target, a); same {
		other = b
	}
	if same, _ := platform.SameVolume(target, other); same {
		t.Skip("找不到与临时目录不同卷的挂载点")
	}

	rootOnOther := filepath.Join(other, "saferm-cross-volume-probe")
	cfg := Config{Roots: map[string]string{Key(volOfTarget): rootOnOther}, Version: "test"}

	plans, viols, err := Resolve([]string{target}, cfg)
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if len(plans) != 0 || !hasViolation(viols, guard.ReasonCrossVolume) {
		t.Fatalf("跨卷必须拒绝，实际 plans=%d viols=%v", len(plans), violationsText(viols))
	}
	if guard.ReasonCrossVolume.Bypassable() {
		t.Fatal("跨卷拒绝不应可被 --allow-dangerous 绕过")
	}
}

func TestResolveIsSideEffectFree(t *testing.T) {
	base := t.TempDir()
	target := filepath.Join(base, "work", "a.txt")
	mustMkdir(t, filepath.Dir(target))
	mustWrite(t, target, "x")

	trash := filepath.Join(base, "trash-not-created-yet")
	if _, _, err := Resolve([]string{target}, cfgWithRoot(t, target, trash)); err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	// dry-run 能安全使用的前提：Resolve 绝不创建目录、绝不写标记
	if _, err := os.Stat(trash); !os.IsNotExist(err) {
		t.Fatalf("Resolve 不应创建回收根，实际 stat 结果 err=%v", err)
	}
}

func TestProbeVolumesReportsRootPathAndConsistency(t *testing.T) {
	vol := firstVolume(t)
	probes, err := ProbeVolumes(Config{Version: "test"})
	if err != nil {
		t.Fatalf("ProbeVolumes: %v", err)
	}
	if len(probes) == 0 {
		t.Fatal("应至少探测到一个卷")
	}
	found := false
	for _, p := range probes {
		// 不论可用与否都必须报出路径：否则用户不知道去哪修配置
		if p.TrashRoot == "" {
			t.Fatalf("卷 %s 没有报出回收根路径", p.VolumeRoot)
		}
		if p.Usable != (p.Problem == "") {
			t.Fatalf("卷 %s 的 Usable=%v 与 Problem=%q 不一致", p.VolumeRoot, p.Usable, p.Problem)
		}
		if p.Exists && p.HasMarker && !p.Usable {
			t.Fatalf("卷 %s 已有带标记的回收根，却报告不可用：%s", p.VolumeRoot, p.Problem)
		}
		if Key(p.VolumeRoot) == Key(vol) {
			found = true
		}
	}
	if !found {
		t.Fatalf("探测结果里没有 %s 这一项", vol)
	}
}

func TestProbeVolumesDoesNotCreateAnything(t *testing.T) {
	vol := firstVolume(t)
	defaultRoot := filepath.Join(vol, DefaultTrashDirName)
	before, err := os.Lstat(defaultRoot)
	if err != nil && !os.IsNotExist(err) {
		t.Fatalf("lstat: %v", err)
	}

	if _, err := ProbeVolumes(Config{Version: "test"}); err != nil {
		t.Fatalf("ProbeVolumes: %v", err)
	}

	after, err := os.Lstat(defaultRoot)
	if err != nil && !os.IsNotExist(err) {
		t.Fatalf("lstat: %v", err)
	}
	if (before == nil) != (after == nil) {
		t.Fatalf("where 必须只读：默认回收根 %q 的存在状态被改变了", defaultRoot)
	}
}

// 兜底候选的路径形状必须落在"用户数据目录"下，且带上卷标识，
// 这样多块盘各自有独立目录，不会互相覆盖。
//
// 注意：完整链路（卷根不可写 → 退到兜底）需要一个只读介质才能真实验证，
// 见 docs/CHANGES-20261006.md 里的说明；这里只钉住路径形状。
func TestFallbackRootShape(t *testing.T) {
	vol := firstVolume(t)
	got := fallbackRoot(vol)
	if got == "" {
		t.Skip("本机没有可用的用户数据目录环境变量")
	}
	if !filepath.IsAbs(got) {
		t.Fatalf("兜底目录应当是绝对路径，实际 %q", got)
	}
	if !strings.Contains(got, "saferm") {
		t.Fatalf("兜底目录应当带 saferm 前缀以便识别，实际 %q", got)
	}
	if !strings.HasSuffix(got, sanitizeMountKey(Key(vol))) {
		t.Fatalf("兜底目录应当以卷标识结尾以免多盘互相覆盖，实际 %q（卷标识 %q）",
			got, sanitizeMountKey(Key(vol)))
	}
}

func TestSanitizeMountKey(t *testing.T) {
	cases := map[string]string{
		"/":      "root",
		"/data":  "data",
		"/mnt/x": "mnt_x",
	}
	for in, want := range cases {
		if got := sanitizeMountKey(in); got != want {
			t.Errorf("sanitizeMountKey(%q) = %q, 期望 %q", in, got, want)
		}
	}
}

func TestKeyMapsVolumeRoots(t *testing.T) {
	if runtime.GOOS == "windows" {
		if got := Key(`D:\`); got != "D" {
			t.Errorf(`Key(D:\) = %q, 期望 D`, got)
		}
		if got := Key(`d:\`); got != "D" {
			t.Errorf(`Key(d:\) = %q, 期望 D（大小写不敏感）`, got)
		}
		return
	}
	if got := Key("/"); got != "/" {
		t.Errorf(`Key(/) = %q, 期望 /`, got)
	}
	if got := Key("/data/"); got != "/data" {
		t.Errorf(`Key(/data/) = %q, 期望 /data`, got)
	}
}

// 显式配置的回收根不可用时必须报错，而不是悄悄退到别的目录 ——
// 那会掩盖真实配置问题，用户以为文件进了自己指定的位置。
func TestResolveDoesNotSilentlyFallbackFromExplicitConfig(t *testing.T) {
	base := t.TempDir()
	target := filepath.Join(base, "work", "a.txt")
	mustMkdir(t, filepath.Dir(target))
	mustWrite(t, target, "x")

	broken := filepath.Join(base, "not-empty")
	mustMkdir(t, broken)
	mustWrite(t, filepath.Join(broken, "x"), "y")

	cfg := cfgWithRoot(t, target, broken)
	cfg.DisableFallback = false // 即使兜底开着，显式配置失败也必须报错

	plans, viols, err := Resolve([]string{target}, cfg)
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if len(plans) != 0 || !hasViolation(viols, guard.ReasonTrashUnusable) {
		t.Fatalf("显式配置不可用时应报错，实际 plans=%d viols=%v", len(plans), violationsText(viols))
	}
}

func hasViolation(vs guard.Violations, want guard.Reason) bool {
	for _, v := range vs {
		if v.Reason == want {
			return true
		}
	}
	return false
}

func violationsText(vs guard.Violations) string {
	if len(vs) == 0 {
		return "(无)"
	}
	return vs.Error()
}

// firstVolume 返回本机第一个可用卷。
func firstVolume(t *testing.T) string {
	t.Helper()
	vols, err := platform.ListVolumes()
	if err != nil {
		t.Fatalf("ListVolumes: %v", err)
	}
	if len(vols) == 0 {
		t.Skip("本机没有可用卷")
	}
	return vols[0]
}

// twoVolumes 返回两个不同的卷，供跨卷断言使用；本机只有一个卷时跳过。
func twoVolumes(t *testing.T) (string, string) {
	t.Helper()
	vols, err := platform.ListVolumes()
	if err != nil {
		t.Fatalf("ListVolumes: %v", err)
	}
	if len(vols) < 2 {
		t.Skipf("本机只有 %d 个卷，跳过跨卷断言", len(vols))
	}
	return vols[0], vols[1]
}
