package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"saferm/internal/config"
	"saferm/internal/platform"
	"saferm/internal/volume"
)

// 本文件是"配置真的生效了吗"的端到端证据。
//
// 设计文档把"用户以为配置生效了、其实没读进去"列为最危险的失败模式，
// 所以这里不停留在"能解析"上：每一项都要证明命令行行为**因配置而改变**。

func runWithConfig(t *testing.T, cfg config.Config, cfgPath, stdin string, interactive bool, args ...string) runResult {
	t.Helper()
	a, out, errBuf := newTestAppWithConfig(t, cfg, cfgPath, stdin, interactive, args...)
	code := a.run()
	return runResult{code: code, stdout: out.String(), stderr: errBuf.String()}
}

func runWithRealConfig(t *testing.T, stdin string, interactive bool, args ...string) runResult {
	t.Helper()
	a, out, errBuf := newTestAppWithRealConfigLoader(t, stdin, interactive, args...)
	code := a.run()
	return runResult{code: code, stdout: out.String(), stderr: errBuf.String()}
}

// 配置文件写错 → 在任何文件系统动作之前硬失败，目标原封不动。
func TestEndToEndBadConfigFailsBeforeAnyChange(t *testing.T) {
	base := t.TempDir()
	target := filepath.Join(base, "a.txt")
	mustWrite(t, target, "precious")
	trashRoot := filepath.Join(base, "trash")

	cfgPath := filepath.Join(base, "config.toml")
	// default_rooot 是拼错的键名：必须报错，绝不静默当成默认值
	mustWrite(t, cfgPath, "[trash]\ndefault_rooot = \"auto\"\n")

	res := runWithRealConfig(t, "", false, "-y", "--config", cfgPath, "--trash-root", trashRoot, target)
	if res.code != exitUsage {
		t.Fatalf("配置写错应退出码 1，实际 %d\nstdout:\n%s\nstderr:\n%s", res.code, res.stdout, res.stderr)
	}
	// 报错要能定位：带文件路径 + 出错的键名
	for _, want := range []string{cfgPath, "default_rooot"} {
		if !strings.Contains(res.stderr, want) {
			t.Errorf("报错里应包含 %q：\n%s", want, res.stderr)
		}
	}
	// 原数据未动，且连回收目录都没创建
	if got := readFile(t, target); got != "precious" {
		t.Fatalf("配置报错时不该动目标，内容变成了 %q", got)
	}
	if pathExists(trashRoot) {
		t.Fatalf("配置报错时不该创建回收目录：%s", trashRoot)
	}
}

// 显式指定的配置文件不存在 → 报错（用户是明确指定的，不能装作没看见）。
func TestEndToEndMissingExplicitConfigFails(t *testing.T) {
	base := t.TempDir()
	target := filepath.Join(base, "a.txt")
	mustWrite(t, target, "x")
	missing := filepath.Join(base, "no-such-config.toml")

	res := runWithRealConfig(t, "", false, "-y", "--config", missing, target)
	if res.code != exitUsage {
		t.Fatalf("显式指定的配置不存在应退出码 1，实际 %d\nstderr:\n%s", res.code, res.stderr)
	}
	if !strings.Contains(res.stderr, missing) {
		t.Errorf("报错里应包含缺失的配置路径：\n%s", res.stderr)
	}
	if _, err := os.Stat(target); err != nil {
		t.Fatalf("目标必须原封不动：%v", err)
	}
}

// where 也要走同一套配置加载；配置写错时 where 同样硬失败，不装作没事。
func TestEndToEndWhereRejectsBadConfig(t *testing.T) {
	base := t.TempDir()
	cfgPath := filepath.Join(base, "config.toml")
	mustWrite(t, cfgPath, "[confirm]\nfile_threshhold = 1\n") // 拼错

	res := runWithRealConfig(t, "", false, "where", "--config", cfgPath)
	if res.code != exitUsage {
		t.Fatalf("where 遇到写错的配置应退出码 1，实际 %d\nstdout:\n%s\nstderr:\n%s",
			res.code, res.stdout, res.stderr)
	}
	if !strings.Contains(res.stderr, "file_threshhold") {
		t.Errorf("报错里应指出拼错的键：\n%s", res.stderr)
	}
}

// [confirm] 的阈值必须真的决定"要不要追问"：
// 同一个目标，只有 file_threshold 不同，一个直接移动、一个要求确认。
func TestEndToEndConfirmThresholdComesFromConfig(t *testing.T) {
	base := t.TempDir()
	dirTarget := filepath.Join(base, "payload")
	mustMkdir(t, dirTarget)
	mustWrite(t, filepath.Join(dirTarget, "a.txt"), "a")
	mustWrite(t, filepath.Join(dirTarget, "b.txt"), "b")

	// 阈值高 + 不因目录而确认 → 无需确认，非交互下直接移动
	permissive := config.Default()
	permissive.Confirm.FileThreshold = 100
	permissive.Confirm.AlwaysConfirmDir = false
	trashA := filepath.Join(base, "trashA")

	res := runWithConfig(t, permissive, "", "", false, "--trash-root", trashA, dirTarget)
	if res.code != exitOK {
		t.Fatalf("阈值放宽后应直接移动（退出码 0），实际 %d\nstdout:\n%s\nstderr:\n%s",
			res.code, res.stdout, res.stderr)
	}
	mustNotExist(t, dirTarget)

	// 换个目标，把阈值收到 1：同样的 2 个文件就超过阈值了 → 需要确认
	dirTarget2 := filepath.Join(base, "payload2")
	mustMkdir(t, dirTarget2)
	mustWrite(t, filepath.Join(dirTarget2, "a.txt"), "a")
	mustWrite(t, filepath.Join(dirTarget2, "b.txt"), "b")

	strictCfg := config.Default()
	strictCfg.Confirm.FileThreshold = 1
	strictCfg.Confirm.AlwaysConfirmDir = false
	trashB := filepath.Join(base, "trashB")

	res = runWithConfig(t, strictCfg, "", "", false, "--trash-root", trashB, dirTarget2)
	if res.code != exitUsage {
		t.Fatalf("阈值收紧后非交互应要求确认（退出码 1），实际 %d\nstdout:\n%s\nstderr:\n%s",
			res.code, res.stdout, res.stderr)
	}
	if !strings.Contains(res.stderr, "--yes") {
		t.Errorf("应提示加 --yes：\n%s", res.stderr)
	}
	if _, err := os.Stat(dirTarget2); err != nil {
		t.Fatalf("拒绝确认时目标必须原封不动：%v", err)
	}
	if pathExists(trashB) {
		t.Fatalf("拒绝确认时不该创建回收目录：%s", trashB)
	}
}

// [guard].protect_vcs_root = false 时，含 .git 的目录不再被第 8 条拒绝；
// 再把 git_detect 也关掉，这个仓库根就真的能被移动。
// 两个开关分别在护栏层与确认层生效，这里一次性钉住。
func TestEndToEndConfigCanRelaxGuard(t *testing.T) {
	base := t.TempDir()
	repo := filepath.Join(base, "myrepo")
	mustMkdir(t, filepath.Join(repo, ".git"))
	mustWrite(t, filepath.Join(repo, "file.txt"), "content")

	// 默认配置：仓库根被护栏第 8 条直接拒绝
	res := run(t, "", false, "-y", repo)
	if res.code != exitUsage {
		t.Fatalf("默认配置下仓库根应被拒绝（退出码 1），实际 %d\nstderr:\n%s", res.code, res.stderr)
	}
	if !strings.Contains(res.stderr, "版本库") {
		t.Errorf("默认拒绝理由应提到版本库：\n%s", res.stderr)
	}

	// 关掉两个开关：既不再拒绝，也不会因为"在仓库里"升为危险级 → 能移动
	relaxed := config.Default()
	relaxed.Guard.ProtectVCSRoot = false
	relaxed.Guard.GitDetect = false
	trashRoot := filepath.Join(base, "trash")

	res = runWithConfig(t, relaxed, "", "", false, "-y", "--trash-root", trashRoot, repo)
	if res.code != exitOK {
		t.Fatalf("关掉 guard 开关后应能移动（退出码 0），实际 %d\nstdout:\n%s\nstderr:\n%s",
			res.code, res.stdout, res.stderr)
	}
	mustNotExist(t, repo)
	if !pathExists(filepath.Join(trashRoot, ".saferm-trash-root")) {
		t.Fatalf("回收根应已创建并带标记：%s", trashRoot)
	}
}

// [guard].git_detect = false 时，位于仓库工作区内的目标不再升为危险级，
// 因此确认级输入 yes 就够（默认配置下这里必须是危险级、yes 无效）。
func TestEndToEndGitDetectCanBeDisabled(t *testing.T) {
	base := t.TempDir()
	repo := filepath.Join(base, "myrepo")
	sub := filepath.Join(repo, "src")
	mustMkdir(t, filepath.Join(repo, ".git"))
	mustWrite(t, filepath.Join(sub, "a.txt"), "x")

	// 默认：危险级，输入 yes 无效 → 取消
	res := run(t, "yes\n", true, "--trash-root", filepath.Join(base, "trashA"), sub)
	if res.code != exitCancelled {
		t.Fatalf("默认配置下此目标应为危险级（yes 无效，退出码 3），实际 %d\nstderr:\n%s",
			res.code, res.stderr)
	}

	// 关掉 git_detect：不再是危险级，输入 yes 即可放行
	noGit := config.Default()
	noGit.Guard.GitDetect = false
	trashB := filepath.Join(base, "trashB")

	res = runWithConfig(t, noGit, "", "yes\n", true, "--trash-root", trashB, sub)
	if res.code != exitOK {
		t.Fatalf("关掉 git_detect 后确认级输入 yes 应放行（退出码 0），实际 %d\nstdout:\n%s\nstderr:\n%s",
			res.code, res.stdout, res.stderr)
	}
	mustNotExist(t, sub)
}

// [guard].extra_protected 只能"追加"保护，且必须真的生效。
func TestEndToEndExtraProtectedIsHonored(t *testing.T) {
	base := t.TempDir()
	keepDir := filepath.Join(base, "keepme")
	target := filepath.Join(keepDir, "inner", "a.txt")
	mustWrite(t, target, "x")

	cfg := config.Default()
	cfg.Guard.ExtraProtected = []string{keepDir}

	res := runWithConfig(t, cfg, "", "", false, "-y", target)
	if res.code != exitUsage {
		t.Fatalf("额外保护目录内的目标应被拒绝（退出码 1），实际 %d\nstderr:\n%s", res.code, res.stderr)
	}
	if !strings.Contains(res.stderr, keepDir) {
		t.Errorf("拒绝理由里应点出受保护目录 %q：\n%s", keepDir, res.stderr)
	}
	if _, err := os.Stat(target); err != nil {
		t.Fatalf("目标必须原封不动：%v", err)
	}
}

// [trash].default_root 在没有 --trash-root 时要被真正使用。
func TestEndToEndTrashDefaultRootFromConfig(t *testing.T) {
	base := t.TempDir()
	target := filepath.Join(base, "a.txt")
	mustWrite(t, target, "content")
	cfgTrash := filepath.Join(base, "cfg-trash")

	cfg := config.Default()
	cfg.Trash.DefaultRoot = cfgTrash

	res := runWithConfig(t, cfg, "", "", false, "-y", target)
	if res.code != exitOK {
		t.Fatalf("退出码 = %d，期望 0\nstdout:\n%s\nstderr:\n%s", res.code, res.stdout, res.stderr)
	}
	mustNotExist(t, target)
	if !pathExists(filepath.Join(cfgTrash, ".saferm-trash-root")) {
		t.Fatalf("配置指定的回收根应被创建并带标记：%s", cfgTrash)
	}
	if got := readFile(t, findFileUnder(t, cfgTrash, "a.txt")); got != "content" {
		t.Fatalf("回收目录里的内容 = %q", got)
	}
}

// --trash-root 的优先级高于配置文件，而且按"覆盖回收根"的语义
// 连 [trash.roots] 一起忽略——否则"命令行覆盖"会被按卷配置架空。
func TestEndToEndTrashRootFlagOverridesConfig(t *testing.T) {
	base := t.TempDir()
	target := filepath.Join(base, "a.txt")
	mustWrite(t, target, "content")

	volRoot, err := platform.VolumeRoot(target)
	if err != nil {
		t.Fatalf("VolumeRoot: %v", err)
	}
	cfgTrash := filepath.Join(base, "cfg-trash")
	flagTrash := filepath.Join(base, "flag-trash")

	cfg := config.Default()
	cfg.Trash.DefaultRoot = cfgTrash
	// 故意把按卷覆盖也指到 cfgTrash：如果实现没有让命令行覆盖作废它，
	// 文件就会落进 cfgTrash，本用例会失败。
	cfg.Trash.Roots = map[string]string{volume.Key(volRoot): cfgTrash}

	res := runWithConfig(t, cfg, "", "", false, "-y", "--trash-root", flagTrash, target)
	if res.code != exitOK {
		t.Fatalf("退出码 = %d，期望 0\nstdout:\n%s\nstderr:\n%s", res.code, res.stdout, res.stderr)
	}
	if !pathExists(filepath.Join(flagTrash, ".saferm-trash-root")) {
		t.Fatalf("应使用 --trash-root 指定的回收根：%s", flagTrash)
	}
	if pathExists(cfgTrash) {
		t.Fatalf("--trash-root 已覆盖回收根，不该再碰配置里的 %s", cfgTrash)
	}
}

// where 要打印生效配置：这是"配置到底读进去没有"的可见证据（设计文档 §8.1）。
func TestEndToEndWhereShowsEffectiveConfig(t *testing.T) {
	cfg := config.Default()
	cfg.Confirm.FileThreshold = 10
	cfg.Confirm.DangerFileThreshold = 999
	cfg.Guard.GitDetect = false
	cfg.Trash.DefaultRoot = "/configured/trash"
	cfgPath := filepath.Join(t.TempDir(), "config.toml")

	res := runWithConfig(t, cfg, cfgPath, "", false, "where")
	if res.code != exitOK && res.code != exitUsage {
		t.Fatalf("where 退出码只应为 0 或 1，实际 %d\nstderr:\n%s", res.code, res.stderr)
	}
	for _, want := range []string{
		"生效配置",
		cfgPath,
		"10",
		"999",
		"/configured/trash",
		"git_detect",
	} {
		if !strings.Contains(res.stdout, want) {
			t.Errorf("where 输出里应包含 %q：\n%s", want, res.stdout)
		}
	}
}

// 没有配置文件时 where 要说明"用的是内置默认值"，不能让人误以为读到了配置。
func TestEndToEndWhereShowsDefaultsWhenNoConfigFile(t *testing.T) {
	res := run(t, "", false, "where")
	if !strings.Contains(res.stdout, "未加载") && !strings.Contains(res.stdout, "内置默认值") {
		t.Errorf("没有配置文件时应明确说明：\n%s", res.stdout)
	}
	if !strings.Contains(res.stdout, "生效配置") {
		t.Errorf("即使没有配置文件，也应展示生效（默认）配置：\n%s", res.stdout)
	}
}
