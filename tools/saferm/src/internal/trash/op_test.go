package trash

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"saferm/internal/platform"
	"saferm/internal/volume"
)

// 测试用固定的时间与进程号，让操作目录名可预测、可断言。
var fixedNow = time.Date(2026, 10, 6, 21, 30, 0, 0, time.Local)

func testOptions() Options {
	return Options{
		Version: "test",
		Host:    "test-host",
		User:    "tester",
		Cwd:     "/somewhere",
		Now:     func() time.Time { return fixedNow },
		Pid:     4242,
	}
}

// newArea 造一个"卷内的临时工作区"：真实的卷根 + 一个已认领的回收根 + work 目录。
func newArea(t *testing.T) (workDir, volumeRoot, trashRoot string) {
	t.Helper()
	base := t.TempDir()

	var err error
	volumeRoot, err = platform.VolumeRoot(base)
	if err != nil {
		t.Fatalf("VolumeRoot(%q): %v", base, err)
	}
	trashRoot = filepath.Join(base, "trash")
	if _, err := volume.EnsureRoot(trashRoot, "test"); err != nil {
		t.Fatalf("EnsureRoot(%q): %v", trashRoot, err)
	}
	workDir = filepath.Join(base, "work")
	mustMkdir(t, workDir)
	return workDir, volumeRoot, trashRoot
}

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

func mustRead(t *testing.T, path string) string {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read %q: %v", path, err)
	}
	return string(data)
}

func mustNotExist(t *testing.T, path string) {
	t.Helper()
	if _, err := os.Lstat(path); !os.IsNotExist(err) {
		t.Fatalf("路径 %q 本应已消失，实际 err=%v", path, err)
	}
}

func mustExist(t *testing.T, path string) {
	t.Helper()
	if _, err := os.Lstat(path); err != nil {
		t.Fatalf("路径 %q 本应存在：%v", path, err)
	}
}

// 核心流程：预登记 → 逐项移动 → 结算。同时验证"先写清单再移动"。
func TestBeginPreRegistersThenMoves(t *testing.T) {
	work, volumeRoot, trashRoot := newArea(t)

	fileSrc := filepath.Join(work, "a.txt")
	mustWrite(t, fileSrc, "hello")

	dirSrc := filepath.Join(work, "nested")
	mustMkdir(t, dirSrc)
	mustWrite(t, filepath.Join(dirSrc, "inner.txt"), "inner")

	sources := []Source{
		{Input: "a.txt", Path: fileSrc},
		{Input: "nested", Path: dirSrc},
	}

	sess, err := Begin(trashRoot, volumeRoot, sources, testOptions())
	if err != nil {
		t.Fatalf("Begin: %v", err)
	}
	info := sess.Info()

	// 预登记：清单已落盘且全部为 pending，但**还没有移动任何东西**
	m, err := LoadManifest(info.Manifest)
	if err != nil {
		t.Fatalf("LoadManifest: %v", err)
	}
	if m.State != OpPending {
		t.Fatalf("预登记后整批状态 = %q，期望 %q", m.State, OpPending)
	}
	for i, it := range m.Items {
		if it.State != StatePending {
			t.Fatalf("第 %d 项预登记状态 = %q，期望 pending", i, it.State)
		}
	}
	mustExist(t, fileSrc)
	mustExist(t, dirSrc)
	mustRead(t, fileSrc)

	// 清单必须在操作目录**外面**，否则手工恢复时会看到杂质文件
	if strings.HasPrefix(info.Manifest, info.Dir+string(os.PathSeparator)) {
		t.Fatalf("清单文件不应位于操作目录内部：%q vs %q", info.Manifest, info.Dir)
	}

	// 逐项移动
	if err := sess.Move(0, Stats{Files: 1, Bytes: 5}); err != nil {
		t.Fatalf("Move(0): %v", err)
	}
	mustNotExist(t, fileSrc)
	dest0 := sess.Items()[0].Destination
	if got := mustRead(t, dest0); got != "hello" {
		t.Fatalf("搬过去的文件内容 = %q，期望 hello", got)
	}

	if err := sess.Move(1, Stats{Files: 1, Bytes: 5}); err != nil {
		t.Fatalf("Move(1): %v", err)
	}
	mustNotExist(t, dirSrc)
	dest1 := sess.Items()[1].Destination
	if got := mustRead(t, filepath.Join(dest1, "inner.txt")); got != "inner" {
		t.Fatalf("搬过去的目录内容 = %q，期望 inner", got)
	}
	mustExist(t, info.Dir)

	// 结算
	sum := sess.Finish()
	if sum.State != OpDone || sum.Done != 2 || sum.Failed != 0 {
		t.Fatalf("结算 = %+v，期望 done/2/0", sum)
	}
	m, err = LoadManifest(info.Manifest)
	if err != nil {
		t.Fatalf("LoadManifest: %v", err)
	}
	if m.FinishedAt == nil {
		t.Fatal("正常收尾后应当记录结束时间")
	}
	if m.Summary.Files != 2 {
		t.Fatalf("汇总文件数 = %d，期望 2", m.Summary.Files)
	}
}

// 镜像路径必须完整保留目录结构：这是"手工恢复时结构不乱"的前提。
func TestMovePreservesDirectoryTree(t *testing.T) {
	work, volumeRoot, trashRoot := newArea(t)

	deep := filepath.Join(work, "projects", "mytools", "tools", "saferm")
	mustMkdir(t, deep)
	mustWrite(t, filepath.Join(deep, "main.go"), "package main")

	sess, err := Begin(trashRoot, volumeRoot,
		[]Source{{Input: "saferm", Path: deep}}, testOptions())
	if err != nil {
		t.Fatalf("Begin: %v", err)
	}
	if err := sess.Move(0, Stats{}); err != nil {
		t.Fatalf("Move: %v", err)
	}

	dest := sess.Items()[0].Destination

	// 去掉卷根前缀后，剩下部分必须与源路径相对卷根的样子一致 —— 即镜像保留了 tree。
	// 注意卷根可能是 C:\ 或 /data，所以不能硬编码期望值。
	wantRel, err := filepath.Rel(volumeRoot, deep)
	if err != nil {
		t.Fatalf("rel(volumeRoot, deep): %v", err)
	}
	wantDest := filepath.Join(sess.Info().Dir, wantRel)
	if dest != wantDest {
		t.Fatalf("镜像落点 = %q，期望 %q", dest, wantDest)
	}
	// 目录结构本身要完整保留
	if !strings.HasSuffix(dest, filepath.Join("projects", "mytools", "tools", "saferm")) {
		t.Fatalf("镜像落点 = %q，目录结构未被保留", dest)
	}
	mustRead(t, filepath.Join(dest, "main.go"))

	// 操作目录内除了镜像内容不应有任何杂质文件（清单放在外面）
	rel, err := filepath.Rel(sess.Info().Dir, dest)
	if err != nil {
		t.Fatalf("rel: %v", err)
	}
	if rel != wantRel {
		t.Fatalf("镜像相对操作目录的位置 = %q，期望 %q", rel, wantRel)
	}
}

// 落点已存在时必须拒绝覆盖：可恢复的前提不能被"覆盖"破坏。
func TestMoveRefusesToOverwriteExistingDestination(t *testing.T) {
	work, volumeRoot, trashRoot := newArea(t)
	src := filepath.Join(work, "a.txt")
	mustWrite(t, src, "hello")

	sess, err := Begin(trashRoot, volumeRoot, []Source{{Input: "a.txt", Path: src}}, testOptions())
	if err != nil {
		t.Fatalf("Begin: %v", err)
	}
	dest := sess.Items()[0].Destination
	mustMkdir(t, filepath.Dir(dest))
	mustWrite(t, dest, "already here")

	// 根目录下确实已经有同路径内容 —— 但我们用的是新操作目录，
	// 这里手工构造"落点已存在"的情形来验证拒绝逻辑
	err = sess.Move(0, Stats{})
	if err == nil {
		t.Fatal("落点已存在时必须报错")
	}
	if !strings.Contains(err.Error(), "拒绝覆盖") {
		t.Fatalf("错误文案应说明拒绝覆盖，实际 = %v", err)
	}

	// 失败即"没删"：源文件原封不动，清单记为失败
	mustExist(t, src)
	if got := mustRead(t, src); got != "hello" {
		t.Fatalf("源文件内容被改动：%q", got)
	}
	if st := sess.Items()[0].State; st != StateFailed {
		t.Fatalf("条目状态 = %q，期望 failed", st)
	}
	if got := mustRead(t, dest); got != "already here" {
		t.Fatalf("原有落点内容被覆盖：%q", got)
	}
}

// -f 下"路径不存在"这类条目要留在清单里：清单是"用户要求了什么"的完整记录。
func TestSkippedSourcesAreRecordedButNotMoved(t *testing.T) {
	work, volumeRoot, trashRoot := newArea(t)
	src := filepath.Join(work, "a.txt")
	mustWrite(t, src, "hello")

	sources := []Source{
		{Input: "a.txt", Path: src},
		{Input: "gone.txt", SkipReason: "路径不存在"},
	}
	sess, err := Begin(trashRoot, volumeRoot, sources, testOptions())
	if err != nil {
		t.Fatalf("Begin: %v", err)
	}
	items := sess.Items()
	if items[1].State != StateSkipped {
		t.Fatalf("第二项状态 = %q，期望 skipped", items[1].State)
	}
	if items[1].Destination != "" {
		t.Fatalf("跳过项不应有落点，实际 %q", items[1].Destination)
	}

	if err := sess.Move(1, Stats{}); err != nil {
		t.Fatalf("跳过项上调用 Move 应当是无操作，实际 %v", err)
	}
	if err := sess.Move(0, Stats{Files: 1, Bytes: 5}); err != nil {
		t.Fatalf("Move(0): %v", err)
	}

	sum := sess.Finish()
	if sum.Done != 1 || sum.Skipped != 1 || sum.State != OpDone {
		t.Fatalf("结算 = %+v，期望 done/1/1", sum)
	}
}

// 同一秒同一进程内连续两次操作不能撞名（否则会互相覆盖清单）。
func TestBeginClaimsDistinctOpDirs(t *testing.T) {
	work, volumeRoot, trashRoot := newArea(t)
	src := filepath.Join(work, "a.txt")
	mustWrite(t, src, "x")

	s1, err := Begin(trashRoot, volumeRoot, []Source{{Input: "a.txt", Path: src}}, testOptions())
	if err != nil {
		t.Fatalf("Begin#1: %v", err)
	}
	s2, err := Begin(trashRoot, volumeRoot, []Source{{Input: "a.txt", Path: src}}, testOptions())
	if err != nil {
		t.Fatalf("Begin#2: %v", err)
	}
	if s1.Info().ID == s2.Info().ID {
		t.Fatalf("两次操作分配了同一个操作号 %q", s1.Info().ID)
	}
	mustExist(t, s1.Info().Manifest)
	mustExist(t, s2.Info().Manifest)
}

// 没有正常收尾的操作必须能被 where 发现（进程被杀/断电后对账用）。
func TestUnfinishedReportsInterruptedOp(t *testing.T) {
	work, volumeRoot, trashRoot := newArea(t)
	src := filepath.Join(work, "a.txt")
	mustWrite(t, src, "x")

	sess, err := Begin(trashRoot, volumeRoot, []Source{{Input: "a.txt", Path: src}}, testOptions())
	if err != nil {
		t.Fatalf("Begin: %v", err)
	}

	ops, bad, err := Unfinished(trashRoot)
	if err != nil {
		t.Fatalf("Unfinished: %v", err)
	}
	if bad != 0 {
		t.Fatalf("不应有损坏清单，实际 %d", bad)
	}
	if len(ops) != 1 || ops[0].OpID != sess.Info().ID {
		t.Fatalf("应报告 1 个未完成操作，实际 %+v", ops)
	}

	// 正常收尾后不再报告
	if err := sess.Move(0, Stats{}); err != nil {
		t.Fatalf("Move: %v", err)
	}
	sess.Finish()
	ops, _, err = Unfinished(trashRoot)
	if err != nil {
		t.Fatalf("Unfinished: %v", err)
	}
	if len(ops) != 0 {
		t.Fatalf("正常收尾后不应再报告未完成操作，实际 %+v", ops)
	}
}

func TestUnfinishedCountsCorruptManifestSeparately(t *testing.T) {
	_, _, trashRoot := newArea(t)
	mustWrite(t, filepath.Join(trashRoot, "broken.op.json"), "{ this is not json")

	ops, bad, err := Unfinished(trashRoot)
	if err != nil {
		t.Fatalf("Unfinished: %v", err)
	}
	if len(ops) != 0 {
		t.Fatalf("不该把损坏清单当未完成操作，实际 %+v", ops)
	}
	if bad != 1 {
		t.Fatalf("损坏清单计数 = %d，期望 1", bad)
	}
}

func TestSummarizeStates(t *testing.T) {
	cases := []struct {
		name  string
		items []Item
		want  string
	}{
		{"全部成功", []Item{{State: StateDone}, {State: StateDone}}, OpDone},
		{"全部跳过视为正常收尾", []Item{{State: StateSkipped}, {State: StateSkipped}}, OpDone},
		{"部分失败", []Item{{State: StateDone}, {State: StateFailed}}, OpPartial},
		{"全部失败", []Item{{State: StateFailed}, {State: StateFailed}}, OpFailed},
		{"有条目没走完", []Item{{State: StateDone}, {State: StatePending}}, OpPending},
		{"改名中途", []Item{{State: StateInProgress}, {State: StateDone}}, OpPending},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := summarize(tc.items).State; got != tc.want {
				t.Fatalf("summarize = %q，期望 %q", got, tc.want)
			}
		})
	}
}

// 卷根与卷外路径都不能算出镜像落点（护栏已经在前面拦过，这里是第二道防线）。
func TestMirrorPathRejectsVolumeRootAndOutside(t *testing.T) {
	_, volumeRoot, trashRoot := newArea(t)
	opDir := filepath.Join(trashRoot, "op")

	if _, err := MirrorPath(opDir, volumeRoot, volumeRoot); err == nil {
		t.Fatal("卷根不能作为删除目标")
	}

	// 找一个与本卷不同的卷上的路径；只有一个卷时跳过（跨卷能力另有专测）
	outside := otherVolumePath(t, volumeRoot)
	// MirrorPath 是纯词法检查（真实的跨卷防线在 volume.Resolve 的 SameVolume，
	// 有专测覆盖）。当卷根是 `/` 时，任何绝对路径在词法上都位于它之下，
	// "卷外路径算不出镜像落点"这一断言无从谈起，只能跳过。
	volClean := strings.TrimRight(filepath.Clean(volumeRoot), "/")
	lexicallyInside := volClean == "" || strings.HasPrefix(filepath.Clean(outside), volClean+"/")
	if outside != "" && !lexicallyInside {
		if _, err := MirrorPath(opDir, volumeRoot, outside); err == nil {
			t.Fatalf("卷外路径 %q 不能算出镜像落点", outside)
		}
	}
}

// otherVolumePath 返回另一个卷上的一个路径；找不到则返回空串。
func otherVolumePath(t *testing.T, excludeVolume string) string {
	t.Helper()
	vols, err := platform.ListVolumes()
	if err != nil {
		t.Fatalf("ListVolumes: %v", err)
	}
	for _, v := range vols {
		if v == excludeVolume {
			continue
		}
		return filepath.Join(v, "saferm-mirror-probe")
	}
	return ""
}

func TestRelativeToVolumeRejectsTooLongMirror(t *testing.T) {
	_, volumeRoot, trashRoot := newArea(t)
	opDir := filepath.Join(trashRoot, "op")

	// 造一个超长的相对路径（不落盘，只验证计算阶段就报错）
	longSeg := strings.Repeat("x", 300)
	parts := make([]string, 0, 120)
	for i := 0; i < 120; i++ {
		parts = append(parts, longSeg)
	}
	long := filepath.Join(append([]string{volumeRoot}, parts...)...)

	if _, err := MirrorPath(opDir, volumeRoot, long); err == nil {
		t.Fatal("镜像路径超长时必须报错而不是截断改名")
	}
}

func TestKindOf(t *testing.T) {
	work, _, _ := newArea(t)
	file := filepath.Join(work, "f.txt")
	mustWrite(t, file, "x")
	dir := filepath.Join(work, "d")
	mustMkdir(t, dir)

	if k, err := kindOf(file); err != nil || k != "file" {
		t.Fatalf("kindOf(file) = %q, %v", k, err)
	}
	if k, err := kindOf(dir); err != nil || k != "dir" {
		t.Fatalf("kindOf(dir) = %q, %v", k, err)
	}
}
