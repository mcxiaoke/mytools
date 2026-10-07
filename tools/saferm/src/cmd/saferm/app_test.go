package main

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"saferm/internal/config"
	"saferm/internal/platform"
	"saferm/internal/volume"
)

// 端到端测试直接驱动 app，不经过真实终端：把 stdin 换成可注入的 Reader，
// 用 isTTY 控制"是不是交互环境"，用 --trash-root 把回收目录限制在临时目录里。
// 绝不触碰真实卷根，避免测试污染。

type runResult struct {
	code   int
	stdout string
	stderr string
}

func newTestApp(t *testing.T, stdin string, interactive bool, args ...string) (*app, *bytes.Buffer, *bytes.Buffer) {
	t.Helper()
	out, errBuf := &bytes.Buffer{}, &bytes.Buffer{}
	fixed := time.Date(2026, 10, 6, 21, 40, 0, 0, time.Local)
	a := &app{
		stdin:  strings.NewReader(stdin),
		stdout: out,
		stderr: errBuf,
		args:   args,
		isTTY:  func() bool { return interactive },
		now:    func() time.Time { return fixed },
		pid:    1234,
		// 默认注入"没有配置文件"：测试不能被开发机上真实存在的
		// %APPDATA%\saferm\config.toml 影响。要测配置时用 newTestAppWithConfig。
		loadConfig: func(string) (config.Config, string, error) { return config.Default(), "", nil },
	}
	return a, out, errBuf
}

// newTestAppWithConfig 让用例注入一份生效配置，用来验证配置确实被用上了。
func newTestAppWithConfig(t *testing.T, cfg config.Config, cfgPath string, stdin string, interactive bool, args ...string) (*app, *bytes.Buffer, *bytes.Buffer) {
	t.Helper()
	a, out, errBuf := newTestApp(t, stdin, interactive, args...)
	a.loadConfig = func(string) (config.Config, string, error) { return cfg, cfgPath, nil }
	return a, out, errBuf
}

// newTestAppWithRealConfigLoader 走真实的配置加载（只读用例显式给出的 --config 路径）。
func newTestAppWithRealConfigLoader(t *testing.T, stdin string, interactive bool, args ...string) (*app, *bytes.Buffer, *bytes.Buffer) {
	t.Helper()
	a, out, errBuf := newTestApp(t, stdin, interactive, args...)
	a.loadConfig = config.Load
	return a, out, errBuf
}

func run(t *testing.T, stdin string, interactive bool, args ...string) runResult {
	t.Helper()
	a, out, errBuf := newTestApp(t, stdin, interactive, args...)
	code := a.run()
	return runResult{code: code, stdout: out.String(), stderr: errBuf.String()}
}

func mustMkdir(t *testing.T, dir string) {
	t.Helper()
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatalf("mkdir %q: %v", dir, err)
	}
}

func mustWrite(t *testing.T, path, content string) {
	t.Helper()
	mustMkdir(t, filepath.Dir(path))
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatalf("write %q: %v", path, err)
	}
}

func mustNotExist(t *testing.T, path string) {
	t.Helper()
	if _, err := os.Lstat(path); !os.IsNotExist(err) {
		t.Fatalf("路径 %q 本应不存在，实际 err=%v", path, err)
	}
}

// findFileUnder 在目录树里按文件名找文件，返回其绝对路径。
func findFileUnder(t *testing.T, root, name string) string {
	t.Helper()
	var found string
	err := filepath.Walk(root, func(p string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if !info.IsDir() && info.Name() == name {
			found = p
		}
		return nil
	})
	if err != nil {
		t.Fatalf("walk %q: %v", root, err)
	}
	if found == "" {
		t.Fatalf("在 %q 下找不到 %q", root, name)
	}
	return found
}

// 最小可用路径：-y 之后再无确认，文件被搬进指定的回收目录，内容原样保留。
func TestEndToEndMovesFileIntoTrash(t *testing.T) {
	base := t.TempDir()
	work := filepath.Join(base, "work")
	trashRoot := filepath.Join(base, "trash")
	target := filepath.Join(work, "a.txt")
	mustWrite(t, target, "hello safe rm")

	res := run(t, "", false, "-y", "--trash-root", trashRoot, target)
	if res.code != exitOK {
		t.Fatalf("退出码 = %d，期望 0\nstdout:\n%s\nstderr:\n%s", res.code, res.stdout, res.stderr)
	}

	// 原路径消失、内容出现在回收目录里
	mustNotExist(t, target)
	moved := findFileUnder(t, trashRoot, "a.txt")
	if got := readFile(t, moved); got != "hello safe rm" {
		t.Fatalf("回收目录里的内容 = %q，期望原内容", got)
	}

	// 回收根必须带身份标记
	if _, err := os.Stat(filepath.Join(trashRoot, ".saferm-trash-root")); err != nil {
		t.Fatalf("回收根缺少标记文件：%v", err)
	}

	// 镜像路径必须保留去掉卷根后的目录结构
	if !strings.Contains(moved, filepath.Join("work")) {
		t.Fatalf("镜像路径 %q 未保留目录结构", moved)
	}

	// 输出要给出操作号、清单路径与"原数据未被永久删除"的说明
	for _, want := range []string{"操作号", "清单", "原数据没有被永久删除", "手工恢复"} {
		if !strings.Contains(res.stdout, want) {
			t.Errorf("输出里缺少 %q：\n%s", want, res.stdout)
		}
	}
}

func readFile(t *testing.T, path string) string {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read %q: %v", path, err)
	}
	return string(data)
}

// dry-run 必须零副作用：不创建回收目录、不移动文件。
func TestEndToEndDryRunCreatesNothing(t *testing.T) {
	base := t.TempDir()
	work := filepath.Join(base, "work")
	trashRoot := filepath.Join(base, "trash")
	target := filepath.Join(work, "a.txt")
	mustWrite(t, target, "x")

	res := run(t, "", false, "-n", "--trash-root", trashRoot, target)
	if res.code != exitOK {
		t.Fatalf("退出码 = %d，期望 0\n%s", res.code, res.stderr)
	}
	mustNotExist(t, trashRoot)
	if got := readFile(t, target); got != "x" {
		t.Fatalf("dry-run 不应改动文件，实际内容 %q", got)
	}
	if !strings.Contains(res.stdout, "不会创建任何目录") {
		t.Fatalf("dry-run 输出应说明零副作用：\n%s", res.stdout)
	}
}

// 护栏在任何写操作之前生效：这些目标一个都不许动。
func TestEndToEndRefusesDangerousTargets(t *testing.T) {
	base := t.TempDir()
	work := filepath.Join(base, "work")
	mustMkdir(t, work)
	trashRoot := filepath.Join(base, "trash")

	// 当前工作目录本身（用进程 cwd：测试跑在包目录下）
	cwd, err := os.Getwd()
	if err != nil {
		t.Fatalf("getwd: %v", err)
	}

	cases := []struct {
		name string
		args []string
	}{
		{"没有参数", nil},
		{"空字符串参数", []string{""}},
		{"当前工作目录", []string{cwd}},
		{"当前工作目录的上级", []string{filepath.Dir(cwd)}},
		{"批次内上下级关系", []string{work, filepath.Join(work, "sub")}},
	}
	mustMkdir(t, filepath.Join(work, "sub"))

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			args := append([]string{"-y", "--trash-root", trashRoot}, tc.args...)
			res := run(t, "", false, args...)
			if res.code != exitUsage {
				t.Fatalf("退出码 = %d，期望 1（拒绝）\nstdout:\n%s\nstderr:\n%s", res.code, res.stdout, res.stderr)
			}
			mustNotExist(t, trashRoot)
			if !strings.Contains(res.stderr, "原数据未改动") {
				t.Errorf("拒绝时必须说明原数据未改动，实际：\n%s", res.stderr)
			}
		})
	}
}

// 非交互环境：需要确认时必须显式授权，否则拒绝且不创建任何东西。
func TestEndToEndNonInteractiveNeedsExplicitFlag(t *testing.T) {
	base := t.TempDir()
	dirTarget := filepath.Join(base, "somedir")
	mustMkdir(t, dirTarget)
	mustWrite(t, filepath.Join(dirTarget, "f.txt"), "x")
	trashRoot := filepath.Join(base, "trash")

	// 目录默认触发"确认级"，非交互 + 没有 -y → 拒绝
	res := run(t, "", false, "--trash-root", trashRoot, dirTarget)
	if res.code != exitUsage {
		t.Fatalf("退出码 = %d，期望 1\nstderr:\n%s", res.code, res.stderr)
	}
	if !strings.Contains(res.stderr, "--yes") {
		t.Errorf("错误里应告诉用户该加什么开关：\n%s", res.stderr)
	}
	mustNotExist(t, trashRoot)
	if _, err := os.Stat(dirTarget); err != nil {
		t.Fatalf("拒绝后目标必须原封不动：%v", err)
	}

	// 加上 -y 之后放行
	res = run(t, "", false, "-y", "--trash-root", trashRoot, dirTarget)
	if res.code != exitOK {
		t.Fatalf("加 -y 后退出码 = %d，期望 0\nstderr:\n%s", res.code, res.stderr)
	}
	if _, err := os.Stat(dirTarget); !os.IsNotExist(err) {
		t.Fatalf("目标应已被移动，实际 err=%v", err)
	}
}

// 危险级：输入 yes 不算，必须手敲目标名称。
//
// 目标取**仓库根本身**（直接含 .git），这正是唯一会升危险级的 Git 信号。
// 但仓库根本来会被护栏第 8 条拒绝，所以这里配 `protect_vcs_root = false`
// 先把它放进来——这样测的正是"护栏被绕过之后，危险级作为第二道防线"这条路径。
func TestEndToEndDangerLevelRequiresTypedName(t *testing.T) {
	base := t.TempDir()
	repo := filepath.Join(base, "myrepo")
	sub := filepath.Join(repo, "src")
	mustMkdir(t, filepath.Join(repo, ".git")) // 造出一个"版本库工作区"
	mustWrite(t, filepath.Join(sub, "a.txt"), "x")
	trashRoot := filepath.Join(base, "trash")

	// 放开护栏第 8 条，让仓库根能走到确认这一步（危险级仍应拦住它）
	repoRootAllowed := config.Default()
	repoRootAllowed.Guard.ProtectVCSRoot = false

	// 输入 yes：危险级不接受
	res := runWithConfig(t, repoRootAllowed, "", "yes\n", true, "--trash-root", trashRoot, repo)
	if res.code != exitCancelled {
		t.Fatalf("危险级输入 yes 应被取消（退出码 3），实际 %d\nstdout:\n%s\nstderr:\n%s",
			res.code, res.stdout, res.stderr)
	}
	if _, err := os.Stat(repo); err != nil {
		t.Fatalf("取消后目标必须原封不动：%v", err)
	}
	mustNotExist(t, trashRoot)

	// 摘要里必须点出"在版本库工作区内"，这是本次事故最关键的信息
	if !strings.Contains(res.stdout, repo) || !strings.Contains(res.stdout, "版本库") {
		t.Errorf("危险级摘要应指出所在仓库：\n%s", res.stdout)
	}

	// 输入目标名称：放行
	res = runWithConfig(t, repoRootAllowed, "", "myrepo\n", true, "--trash-root", trashRoot, repo)
	if res.code != exitOK {
		t.Fatalf("手敲目标名后退出码 = %d，期望 0\nstdout:\n%s\nstderr:\n%s", res.code, res.stdout, res.stderr)
	}
	if _, err := os.Stat(repo); !os.IsNotExist(err) {
		t.Fatalf("目标应已被移动，实际 err=%v", err)
	}
}

// 非交互下危险级只有 --yes-i-am-sure 能放行，-y 不够。
func TestEndToEndDangerLevelNeedsLongFlagWhenNonInteractive(t *testing.T) {
	base := t.TempDir()
	repo := filepath.Join(base, "repo")
	mustMkdir(t, filepath.Join(repo, ".git"))
	mustWrite(t, filepath.Join(repo, "a.txt"), "x")
	trashRoot := filepath.Join(base, "trash")

	// 目标自身就是仓库根 → 危险级。用 protect_vcs_root=false 先放过护栏第 8 条，
	// 测的正是"护栏被绕过/关闭之后危险级仍然拦得住"。
	cfg := config.Default()
	cfg.Guard.ProtectVCSRoot = false

	res := runWithConfig(t, cfg, "", "", false, "-y", "--trash-root", trashRoot, repo)
	if res.code != exitUsage {
		t.Fatalf("-y 不应放行危险级，实际退出码 %d\nstderr:\n%s", res.code, res.stderr)
	}
	if !strings.Contains(res.stderr, "--yes-i-am-sure") {
		t.Errorf("应提示危险级的放行开关：\n%s", res.stderr)
	}
	mustNotExist(t, trashRoot)

	// 加上长开关后放行
	res = runWithConfig(t, cfg, "", "", false, "-y", "--yes-i-am-sure", "--trash-root", trashRoot, repo)
	if res.code != exitOK {
		t.Fatalf("加 --yes-i-am-sure 后退出码 = %d，期望 0\nstderr:\n%s", res.code, res.stderr)
	}
}

// 仓库里的**普通目标**不再因为"在版本库工作区内"而升危险级：-y 就该够用。
//
// 这是本次语义调整的核心回归测试。"在仓库内"是常开信号（开发者几乎所有文件都在
// 某个仓库里），拿它触发危险级会让 -y 在任何真实项目里都失效。
func TestEndToEndInRepoTargetNoLongerNeedsDangerFlag(t *testing.T) {
	base := t.TempDir()
	repo := filepath.Join(base, "myrepo")
	sub := filepath.Join(repo, "src")
	mustMkdir(t, filepath.Join(repo, ".git"))
	mustWrite(t, filepath.Join(sub, "a.txt"), "x")
	trashRoot := filepath.Join(base, "trash")

	res := run(t, "", false, "-y", "--trash-root", trashRoot, sub)
	if res.code != exitOK {
		t.Fatalf("仓库内的普通目标应该 -y 就能移动（退出码 0），实际 %d\nstdout:\n%s\nstderr:\n%s",
			res.code, res.stdout, res.stderr)
	}
	mustNotExist(t, sub)
}

// 通配符：字面优先，展开为空必须报错且不移动任何东西。
func TestEndToEndGlobExpansion(t *testing.T) {
	base := t.TempDir()
	work := filepath.Join(base, "work")
	trashRoot := filepath.Join(base, "trash")
	mustWrite(t, filepath.Join(work, "a.log"), "1")
	mustWrite(t, filepath.Join(work, "b.log"), "2")
	mustWrite(t, filepath.Join(work, "c.txt"), "3")

	// 展开为空 → 报错
	res := run(t, "", false, "-y", "--trash-root", trashRoot, filepath.Join(work, "*.nomatch"))
	if res.code != exitUsage {
		t.Fatalf("展开为空应报错，实际退出码 %d\nstderr:\n%s", res.code, res.stderr)
	}
	if !strings.Contains(res.stderr, "没有匹配到任何路径") {
		t.Errorf("错误文案应说明原因：\n%s", res.stderr)
	}
	mustNotExist(t, trashRoot)

	// 正常展开 → 两个 .log 被移动，.txt 不动
	res = run(t, "", false, "-y", "--trash-root", trashRoot, filepath.Join(work, "*.log"))
	if res.code != exitOK {
		t.Fatalf("展开后退出码 = %d，期望 0\nstderr:\n%s", res.code, res.stderr)
	}
	mustNotExist(t, filepath.Join(work, "a.log"))
	mustNotExist(t, filepath.Join(work, "b.log"))
	if _, err := os.Stat(filepath.Join(work, "c.txt")); err != nil {
		t.Fatalf("未被通配符命中的文件不该被动：%v", err)
	}
}

// 字面优先：磁盘上真有一个名字带 * 的文件时，要删的是它本身，而不是展开。
// Windows 不允许文件名含 *，所以只在非 Windows 上验证。
func TestEndToEndLiteralFirstForWeirdNames(t *testing.T) {
	base := t.TempDir()
	work := filepath.Join(base, "work")
	trashRoot := filepath.Join(base, "trash")
	mustMkdir(t, work)
	weird := filepath.Join(work, "a*b")
	// Windows 的文件系统不允许 * 出现在名字里，此时直接跳过
	if err := os.WriteFile(weird, []byte("literal"), 0o644); err != nil {
		t.Skipf("当前平台不允许这种文件名：%v", err)
	}

	res := run(t, "", false, "-y", "--trash-root", trashRoot, weird)
	if res.code != exitOK {
		t.Fatalf("退出码 = %d，期望 0\nstderr:\n%s", res.code, res.stderr)
	}
	mustNotExist(t, weird)
	if got := readFile(t, findFileUnder(t, trashRoot, "a*b")); got != "literal" {
		t.Fatalf("搬走的内容 = %q", got)
	}
}

// -f：路径不存在不报错；同时它还会跳过确认级。
func TestEndToEndForceIgnoresMissing(t *testing.T) {
	base := t.TempDir()
	work := filepath.Join(base, "work")
	trashRoot := filepath.Join(base, "trash")
	mustMkdir(t, work)

	missing := filepath.Join(work, "gone.txt")
	res := run(t, "", false, "-f", "--trash-root", trashRoot, missing)
	if res.code != exitOK {
		t.Fatalf("带 -f 时路径不存在不该报错，实际退出码 %d\nstderr:\n%s", res.code, res.stderr)
	}
	if !strings.Contains(res.stderr, "没有任何可移动的目标") {
		t.Errorf("应说明本次没有可移动目标：\n%s", res.stderr)
	}
	mustNotExist(t, trashRoot)

	// 不带 -f 时同样的输入必须报错
	res = run(t, "", false, "-y", "--trash-root", trashRoot, missing)
	if res.code != exitUsage {
		t.Fatalf("不带 -f 时应报错，实际退出码 %d", res.code)
	}
}

// rm 兼容：-rf 这种组合短选项要能解析，且拆不掉护栏。
func TestEndToEndRmCompatibleCombinedShortFlags(t *testing.T) {
	base := t.TempDir()
	work := filepath.Join(base, "work")
	trashRoot := filepath.Join(base, "trash")
	target := filepath.Join(work, "a.txt")
	mustWrite(t, target, "x")

	res := run(t, "", false, "-rf", "--trash-root", trashRoot, target)
	if res.code != exitOK {
		t.Fatalf("-rf 应能解析，实际退出码 %d\nstderr:\n%s", res.code, res.stderr)
	}
	mustNotExist(t, target)

	// -rf 组合不能绕过护栏：拿当前工作目录试
	cwd, err := os.Getwd()
	if err != nil {
		t.Fatalf("getwd: %v", err)
	}
	res = run(t, "", false, "-rf", "--trash-root", trashRoot, cwd)
	if res.code != exitUsage {
		t.Fatalf("-rf 不得绕过护栏，实际退出码 %d\nstderr:\n%s", res.code, res.stderr)
	}
}

// -- 之后的内容必须当路径处理（用来删以 - 开头的文件名）。
func TestEndToEndDoubleDashTerminator(t *testing.T) {
	base := t.TempDir()
	work := filepath.Join(base, "work")
	trashRoot := filepath.Join(base, "trash")
	target := filepath.Join(work, "-weird-name")
	mustWrite(t, target, "x")

	res := run(t, "", false, "-y", "--trash-root", trashRoot, "--", target)
	if res.code != exitOK {
		t.Fatalf("退出码 = %d，期望 0\nstderr:\n%s", res.code, res.stderr)
	}
	mustNotExist(t, target)
}

// 一批目标里含上下级时整批拒绝（预检失败 → 整批不执行）。
func TestEndToEndBatchPreflightIsAllOrNothing(t *testing.T) {
	base := t.TempDir()
	work := filepath.Join(base, "work")
	parent := filepath.Join(work, "parent")
	child := filepath.Join(parent, "child")
	mustMkdir(t, child)
	mustWrite(t, filepath.Join(parent, "p.txt"), "p")
	mustWrite(t, filepath.Join(child, "c.txt"), "c")
	trashRoot := filepath.Join(base, "trash")

	res := run(t, "", false, "-y", "--trash-root", trashRoot, parent, child)
	if res.code != exitUsage {
		t.Fatalf("上下级关系应整批拒绝，实际退出码 %d", res.code)
	}
	// 两个目标都必须原封不动
	if _, err := os.Stat(filepath.Join(parent, "p.txt")); err != nil {
		t.Fatalf("整批拒绝时不该动任何文件：%v", err)
	}
	if _, err := os.Stat(filepath.Join(child, "c.txt")); err != nil {
		t.Fatalf("整批拒绝时不该动任何文件：%v", err)
	}
	mustNotExist(t, trashRoot)
}

// where 是只读命令：不创建任何目录，且输出里要有各卷清单。
func TestEndToEndWhereIsReadOnly(t *testing.T) {
	root := defaultTrashRoot(t)
	before := pathExists(root)

	res := run(t, "", false, "where")
	if !strings.Contains(res.stdout, "各卷的回收目录") {
		t.Fatalf("where 输出缺少卷清单：\n%s", res.stdout)
	}
	if res.code != exitOK && res.code != exitUsage {
		t.Fatalf("where 退出码 = %d，只应为 0 或 1", res.code)
	}
	if after := pathExists(root); after != before {
		t.Fatalf("where 不该创建任何目录：%v → %v（%s）", before, after, root)
	}
}

// defaultTrashRoot 用与 where 相同的规则算出"第一个卷的默认回收根"。
func defaultTrashRoot(t *testing.T) string {
	t.Helper()
	vols, err := platform.ListVolumes()
	if err != nil {
		t.Fatalf("ListVolumes: %v", err)
	}
	if len(vols) == 0 {
		t.Skip("本机没有可用卷")
	}
	return filepath.Join(vols[0], volume.DefaultTrashDirName)
}

func pathExists(p string) bool {
	_, err := os.Lstat(p)
	return err == nil
}

// 版本与帮助必须能独立工作（不碰文件系统）。
func TestEndToEndVersionAndHelp(t *testing.T) {
	res := run(t, "", false, "-V")
	if res.code != exitOK || !strings.Contains(res.stdout, "saferm") {
		t.Fatalf("-V 输出异常：code=%d stdout=%q", res.code, res.stdout)
	}

	res = run(t, "", false, "--help")
	if res.code != exitOK || !strings.Contains(res.stdout, "安全边界") {
		t.Fatalf("--help 输出异常：code=%d\n%s", res.code, res.stdout)
	}

	res = run(t, "", false, "version")
	if res.code != exitOK || !strings.Contains(res.stdout, "saferm") {
		t.Fatalf("version 子命令输出异常：code=%d stdout=%q", res.code, res.stdout)
	}
}

// 未知开关必须报错并给用法提示，绝不静默忽略。
func TestEndToEndUnknownFlagFails(t *testing.T) {
	res := run(t, "", false, "--definitely-not-a-flag", "/tmp/x")
	if res.code != exitUsage {
		t.Fatalf("未知开关应报错，实际退出码 %d", res.code)
	}
	if !strings.Contains(res.stderr, "用法") {
		t.Errorf("应给出用法提示：\n%s", res.stderr)
	}
}
