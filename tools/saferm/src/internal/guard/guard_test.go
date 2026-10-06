package guard

import (
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"saferm/internal/platform"
)

// 本文件即设计文档 §5.1 那张"硬拦截清单"的测试化版本。
// 每条规则都要有正例（被拒绝）与反例（被放行），否则护栏形同虚设。

// sandbox 搭一个可控的小树：
//
//	<tmp>/work/            工作目录（cwd）
//	<tmp>/work/plain.txt   正常可删的文件
//	<tmp>/work/repo/       普通 Git 仓库根（含 .git 目录）
//	<tmp>/work/sub/        正常可删的目录
//	<tmp>/other/           cwd 之外
func sandbox(t *testing.T) (tmp, cwd string) {
	t.Helper()
	tmp = t.TempDir()
	cwd = filepath.Join(tmp, "work")
	mustMkdir(t, cwd)
	mustMkdir(t, filepath.Join(tmp, "other"))
	mustMkdir(t, filepath.Join(cwd, "sub"))
	mustMkdir(t, filepath.Join(cwd, "sub", "deeper"))
	mustMkdir(t, filepath.Join(cwd, "repo", ".git"))
	mustWrite(t, filepath.Join(cwd, "plain.txt"), "hello")
	return tmp, cwd
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

// canon 把测试期望值也走一遍与 guard 相同的规范化，避免 Windows 短名导致误报。
func canon(t *testing.T, p string) string {
	t.Helper()
	abs, err := platform.AbsClean(p)
	if err != nil {
		t.Fatalf("AbsClean(%q): %v", p, err)
	}
	expanded, err := platform.ExpandShortName(abs)
	if err != nil {
		t.Fatalf("ExpandShortName(%q): %v", abs, err)
	}
	return expanded
}

// reasonsOf 把拒绝项压成"原因 + 路径"的可比较形式。
func reasonsOf(vs Violations) []Reason {
	out := make([]Reason, 0, len(vs))
	for _, v := range vs {
		out = append(out, v.Reason)
	}
	return out
}

func hasReason(vs Violations, want Reason) bool {
	for _, v := range vs {
		if v.Reason == want {
			return true
		}
	}
	return false
}

func TestCheckRefusesEachRule(t *testing.T) {
	tmp, cwd := sandbox(t)
	volRoot := mustVolumeRoot(t, tmp)

	tests := []struct {
		name     string
		operands []string
		opts     Options
		want     Reason
	}{
		{
			name:     "第1条 空字符串",
			operands: []string{""},
			want:     ReasonEmptyOperand,
		},
		{
			name:     "第1条 纯空白",
			operands: []string{"   "},
			want:     ReasonEmptyOperand,
		},
		{
			name:     "第2条 一个参数都没有",
			operands: nil,
			want:     ReasonNoOperands,
		},
		{
			name:     "第3条 路径不存在",
			operands: []string{filepath.Join(cwd, "nope-does-not-exist")},
			want:     ReasonNotExist,
		},
		{
			name:     "第4条 目标是 cwd 本身",
			operands: []string{cwd},
			want:     ReasonIsCwd,
		},
		{
			name:     "第5条 目标是 cwd 的上级",
			operands: []string{tmp},
			want:     ReasonAncestorOfCwd,
		},
		{
			name:     "第5条 目标是 cwd 上级的上级",
			operands: []string{filepath.Dir(tmp)},
			want:     ReasonAncestorOfCwd,
		},
		{
			name:     "第6条 卷根",
			operands: []string{volRoot},
			want:     ReasonVolumeRoot,
		},
		{
			name:     "第7条 系统关键路径",
			operands: []string{protectedSample(t)},
			want:     ReasonProtectedPath,
		},
		{
			name:     "第8条 VCS 仓库根（.git 是目录）",
			operands: []string{filepath.Join(cwd, "repo")},
			want:     ReasonVCSRoot,
		},
		{
			name:     "第11条 重复参数",
			operands: []string{filepath.Join(cwd, "plain.txt"), filepath.Join(cwd, "plain.txt")},
			want:     ReasonDuplicateOperand,
		},
		{
			name:     "第12条 批次内上下级关系",
			operands: []string{filepath.Join(cwd, "sub"), filepath.Join(cwd, "sub", "deeper")},
			want:     ReasonNestedOperand,
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			opts := tc.opts
			opts.Cwd = cwd
			rep, err := Check(tc.operands, opts)
			if err != nil {
				t.Fatalf("Check 返回内部错误: %v", err)
			}
			if !hasReason(rep.Violations, tc.want) {
				t.Fatalf("期望拒绝原因 %q，实际 = %v（消息：%v）", tc.want, reasonsOf(rep.Violations), rep.Violations.Error())
			}
		})
	}
}

func TestCheckAcceptsLegitimateTargets(t *testing.T) {
	_, cwd := sandbox(t)

	tests := []struct {
		name     string
		operands []string
	}{
		{"普通文件", []string{filepath.Join(cwd, "plain.txt")}},
		{"cwd 下的子目录", []string{filepath.Join(cwd, "sub")}},
		{"cwd 之外的兄弟目录", []string{filepath.Join(filepath.Dir(cwd), "other")}},
		{"同一批里的两个不同目标", []string{filepath.Join(cwd, "plain.txt"), filepath.Join(cwd, "sub")}},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			rep, err := Check(tc.operands, Options{Cwd: cwd})
			if err != nil {
				t.Fatalf("Check 返回内部错误: %v", err)
			}
			if rep.Failed() {
				t.Fatalf("不应被拒绝，实际 = %v", rep.Violations.Error())
			}
			if len(rep.Targets) != len(tc.operands) {
				t.Fatalf("期望 %d 个目标，实际 %d 个", len(tc.operands), len(rep.Targets))
			}
			if len(rep.Warnings) != 0 {
				t.Fatalf("不应产生警告，实际 = %v", rep.Warnings)
			}
		})
	}
}

// 相对路径按**进程的工作目录**解析，不是按 Options.Cwd（后者只用于比较）。
// 用 t.Chdir 真实地换一次目录来验证。
func TestCheckResolvesRelativeOperandsAgainstProcessCwd(t *testing.T) {
	_, cwd := sandbox(t)
	t.Chdir(cwd)

	rep, err := Check([]string{"sub"}, Options{})
	if err != nil {
		t.Fatalf("Check: %v", err)
	}
	if rep.Failed() {
		t.Fatalf("相对路径 sub 应被接受，实际 = %v", rep.Violations.Error())
	}
	if len(rep.Targets) != 1 {
		t.Fatalf("期望 1 个目标，实际 %d", len(rep.Targets))
	}
	if rep.Targets[0].Path != canon(t, filepath.Join(cwd, "sub")) {
		t.Fatalf("相对路径解析结果 %q，期望 %q", rep.Targets[0].Path, canon(t, filepath.Join(cwd, "sub")))
	}

	// 换到 cwd 里之后，"。" 就是 cwd 本身，必须被第 4 条拦住
	rep, err = Check([]string{"."}, Options{})
	if err != nil {
		t.Fatalf("Check: %v", err)
	}
	if !hasReason(rep.Violations, ReasonIsCwd) {
		t.Fatalf("cwd 里删 . 必须被拒，实际 = %v", reasonsOf(rep.Violations))
	}
}

// 第 3 条与 -f 的关系：默认报错，带 -f 静默跳过。
func TestCheckIgnoreMissingOnlyWithForce(t *testing.T) {
	_, cwd := sandbox(t)
	missing := filepath.Join(cwd, "nope")

	rep, err := Check([]string{missing}, Options{Cwd: cwd})
	if err != nil {
		t.Fatalf("Check: %v", err)
	}
	if !hasReason(rep.Violations, ReasonNotExist) {
		t.Fatalf("不带 -f 时路径不存在应报错，实际 = %v", reasonsOf(rep.Violations))
	}

	rep, err = Check([]string{missing}, Options{Cwd: cwd, IgnoreMissing: true})
	if err != nil {
		t.Fatalf("Check: %v", err)
	}
	if rep.Failed() {
		t.Fatalf("带 -f 时不应报错，实际 = %v", rep.Violations.Error())
	}
	if len(rep.Skipped) != 1 {
		t.Fatalf("带 -f 时应记录 1 条跳过，实际 = %d", len(rep.Skipped))
	}
	if len(rep.Targets) != 0 {
		t.Fatalf("跳过的路径不应进入目标集合，实际 = %d", len(rep.Targets))
	}
}

// 第 8 条必须覆盖 .git 的两种形态：目录（普通仓库）与文件（worktree / submodule）。
func TestCheckVCSRootCoversGitFileForm(t *testing.T) {
	_, cwd := sandbox(t)

	submodule := filepath.Join(cwd, "submod")
	mustMkdir(t, submodule)
	mustWrite(t, filepath.Join(submodule, ".git"), "gitdir: ../../.git/modules/submod\n")

	rep, err := Check([]string{submodule}, Options{Cwd: cwd})
	if err != nil {
		t.Fatalf("Check: %v", err)
	}
	if !hasReason(rep.Violations, ReasonVCSRoot) {
		t.Fatalf(".git 为文件形态时也必须判为仓库根，实际 = %v", reasonsOf(rep.Violations))
	}
	if !strings.Contains(rep.Violations[0].Detail, ".git") {
		t.Fatalf("拒绝说明里应指出命中的标记，实际 = %q", rep.Violations[0].Detail)
	}
}

func TestCheckVCSRootCanBeDisabledByOption(t *testing.T) {
	_, cwd := sandbox(t)
	rep, err := Check([]string{filepath.Join(cwd, "repo")},
		Options{Cwd: cwd, DisableVCSRootCheck: true})
	if err != nil {
		t.Fatalf("Check: %v", err)
	}
	if rep.Failed() {
		t.Fatalf("关闭该检查后不应拒绝，实际 = %v", rep.Violations.Error())
	}
}

// §5.2：参数错误类任何开关都不能绕过；危险路径类可被 --allow-dangerous 降级为警告。
func TestBypassPolicy(t *testing.T) {
	_, cwd := sandbox(t)

	t.Run("危险路径类被降级为警告而不是拒绝", func(t *testing.T) {
		rep, err := Check([]string{cwd}, Options{Cwd: cwd, AllowDangerous: true})
		if err != nil {
			t.Fatalf("Check: %v", err)
		}
		if rep.Failed() {
			t.Fatalf("--allow-dangerous 下不应拒绝，实际 = %v", rep.Violations.Error())
		}
		if len(rep.Warnings) == 0 {
			t.Fatal("必须留下警告，否则用户不知道护栏被关掉了")
		}
		if !strings.Contains(rep.Warnings[0], "--allow-dangerous") {
			t.Fatalf("警告文案应点明开关名，实际 = %q", rep.Warnings[0])
		}
	})

	t.Run("参数错误类不可绕过", func(t *testing.T) {
		cases := []struct {
			name     string
			operands []string
			want     Reason
		}{
			{"空参数", []string{""}, ReasonEmptyOperand},
			{"无参数", nil, ReasonNoOperands},
			{"重复项", []string{filepath.Join(cwd, "plain.txt"), filepath.Join(cwd, "plain.txt")}, ReasonDuplicateOperand},
			{"上下级", []string{filepath.Join(cwd, "sub"), filepath.Join(cwd, "sub", "deeper")}, ReasonNestedOperand},
		}
		for _, tc := range cases {
			t.Run(tc.name, func(t *testing.T) {
				rep, err := Check(tc.operands, Options{Cwd: cwd, AllowDangerous: true})
				if err != nil {
					t.Fatalf("Check: %v", err)
				}
				if !hasReason(rep.Violations, tc.want) {
					t.Fatalf("--allow-dangerous 不应绕过 %q，实际 = %v", tc.want, reasonsOf(rep.Violations))
				}
			})
		}
	})

	t.Run("参数错误类不可绕过：路径不存在", func(t *testing.T) {
		rep, err := Check([]string{filepath.Join(cwd, "nope")},
			Options{Cwd: cwd, AllowDangerous: true})
		if err != nil {
			t.Fatalf("Check: %v", err)
		}
		if !hasReason(rep.Violations, ReasonNotExist) {
			t.Fatalf("--allow-dangerous 不应绕过“路径不存在”，实际 = %v", reasonsOf(rep.Violations))
		}
	})
}

// 这条测试把"哪些原因可被绕过"钉死，防止以后有人误把参数错误类标成可绕过。
func TestBypassableClassification(t *testing.T) {
	bypassable := map[Reason]bool{
		ReasonIsCwd:           true,
		ReasonAncestorOfCwd:   true,
		ReasonVolumeRoot:      true,
		ReasonProtectedPath:   true,
		ReasonVCSRoot:         true,
		ReasonInsideTrash:     true,
		ReasonAncestorOfTrash: true,

		ReasonEmptyOperand:     false,
		ReasonNoOperands:       false,
		ReasonNotExist:         false,
		ReasonUnreadable:       false,
		ReasonUnresolvedPath:   false,
		ReasonDuplicateOperand: false,
		ReasonNestedOperand:    false,
	}
	for reason, want := range bypassable {
		if got := reason.Bypassable(); got != want {
			t.Errorf("Reason(%q).Bypassable() = %v, 期望 %v", reason, got, want)
		}
	}
}

// 第 9、10 条：与回收目录的关系。注意这一步必须在**最终**回收根上做。
func TestCheckTrashRelation(t *testing.T) {
	tmp, _ := sandbox(t)
	trashRoot := filepath.Join(tmp, "trash")

	tests := []struct {
		name   string
		target string
		want   Reason
		ok     bool
	}{
		{"目标是回收根本身", trashRoot, ReasonInsideTrash, true},
		{"目标在回收根之内", filepath.Join(trashRoot, "20261006-1", "a"), ReasonInsideTrash, true},
		{"目标包含回收根（第10条）", tmp, ReasonAncestorOfTrash, true},
		{"目标与回收根无关", filepath.Join(tmp, "other"), "", false},
		{"回收根为空表示未配置", filepath.Join(tmp, "other"), "", false},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			root := trashRoot
			if strings.Contains(tc.name, "未配置") {
				root = ""
			}
			v, ok := CheckTrashRelation(tc.target, root)
			if ok != tc.ok {
				t.Fatalf("ok = %v, 期望 %v", ok, tc.ok)
			}
			if ok && v.Reason != tc.want {
				t.Fatalf("Reason = %q, 期望 %q", v.Reason, tc.want)
			}
		})
	}
}

// 配置里的临时回收根（位于目标内部）必须被第 10 条拦住 —— 否则会把 trash 搬进它自己内部。
func TestTrashRootInsideTargetIsRefused(t *testing.T) {
	tmp, cwd := sandbox(t)
	target := filepath.Join(tmp, "projects")
	rootFromConfig := filepath.Join(target, ".saferm-trash")

	v, ok := CheckTrashRelation(target, rootFromConfig)
	if !ok || v.Reason != ReasonAncestorOfTrash {
		t.Fatalf("目标包含回收根时必须拒绝，实际 ok=%v reason=%q", ok, v.Reason)
	}

	// 顺带确认：这个违规确实是危险路径类（可被 --allow-dangerous 放行）
	if !v.Reason.Bypassable() {
		t.Fatal("目标是回收根的祖先应属于危险路径类")
	}
	_ = cwd
}

func TestCheckTrashRelationViolationIsBypassable(t *testing.T) {
	tmp, _ := sandbox(t)
	rep := &Report{}
	v, ok := CheckTrashRelation(filepath.Join(tmp, "other"), tmp)
	if !ok {
		t.Fatal("期望产生违规")
	}
	rep.Add(v, true)
	if rep.Failed() {
		t.Fatalf("--allow-dangerous 下应降级为警告，实际拒绝 = %v", rep.Violations.Error())
	}
	if len(rep.Warnings) != 1 {
		t.Fatalf("应记录 1 条警告，实际 = %d", len(rep.Warnings))
	}
}

func TestPathKeyBoundaries(t *testing.T) {
	if runtime.GOOS == "windows" {
		// 尾随点/空格：Win32 解析时本来就会去掉，只比字符串会绕过 "== cwd"
		if pathKey(`D:\repo `) != pathKey(`D:\repo`) {
			t.Errorf("尾随空格未被折叠: %q vs %q", pathKey(`D:\repo `), pathKey(`D:\repo`))
		}
		if pathKey(`D:\repo.`) != pathKey(`D:\repo`) {
			t.Errorf("尾随点未被折叠: %q vs %q", pathKey(`D:\repo.`), pathKey(`D:\repo`))
		}
		if pathKey(`D:\REPO`) != pathKey(`d:\repo`) {
			t.Errorf("大小写未被折叠: %q vs %q", pathKey(`D:\REPO`), pathKey(`d:\repo`))
		}
		if pathKey(`D:/repo`) != pathKey(`D:\repo`) {
			t.Errorf("分隔符未被统一: %q vs %q", pathKey(`D:/repo`), pathKey(`D:\repo`))
		}
	}

	// 分量边界：/data 不能命中 /data2
	if isAncestor("/data", "/data2/x") {
		t.Error("/data 不应被视为 /data2/x 的上级")
	}
	if !isAncestor("/data", "/data/x") {
		t.Error("/data 应被视为 /data/x 的上级")
	}
	if isAncestor("/data", "/data") {
		t.Error("自身不算自己的严格上级")
	}
	if !isInsideInclusive("/data", "/data") {
		t.Error("isInsideInclusive 应包含 root 自身")
	}
	if !isAncestor("/", "/anything") {
		t.Error("根应是所有绝对路径的上级")
	}
}

func TestViolationsErrorMessageIsActionable(t *testing.T) {
	rep, err := Check([]string{""}, Options{Cwd: t.TempDir()})
	if err != nil {
		t.Fatalf("Check: %v", err)
	}
	msg := rep.Violations.Error()
	// 空参数这条必须提示引号写法，这是本次事故的直接教训
	if !strings.Contains(msg, "$null") || !strings.Contains(msg, "单引号") {
		t.Fatalf("空参数提示应包含 $null 与单引号写法，实际 = %q", msg)
	}
	if !strings.Contains(msg, "原数据未改动") {
		t.Fatalf("拒绝时应明确告诉用户原数据未被改动，实际 = %q", msg)
	}
}

func mustVolumeRoot(t *testing.T, p string) string {
	t.Helper()
	root, err := platform.VolumeRoot(p)
	if err != nil {
		t.Fatalf("VolumeRoot(%q): %v", p, err)
	}
	return root
}

func protectedSample(t *testing.T) string {
	t.Helper()
	if runtime.GOOS == "windows" {
		sr := os.Getenv("SystemRoot")
		if sr == "" {
			t.Skip("没有 SystemRoot 环境变量")
		}
		return sr
	}
	if _, err := os.Stat("/etc"); err != nil {
		t.Skip("没有 /etc")
	}
	return "/etc"
}

func TestCanonHelperMatchesGuardNormalization(t *testing.T) {
	_, cwd := sandbox(t)
	rep, err := Check([]string{filepath.Join(cwd, "plain.txt")}, Options{Cwd: cwd})
	if err != nil {
		t.Fatalf("Check: %v", err)
	}
	if len(rep.Targets) != 1 {
		t.Fatalf("期望 1 个目标，实际 %d", len(rep.Targets))
	}
	if rep.Targets[0].Path != canon(t, filepath.Join(cwd, "plain.txt")) {
		t.Fatalf("guard 的规范化结果 %q 与测试期望 %q 不一致",
			rep.Targets[0].Path, canon(t, filepath.Join(cwd, "plain.txt")))
	}
	if rep.Targets[0].Info == nil || rep.Targets[0].Info.IsDir() {
		t.Fatal("Target.Info 应为普通文件")
	}
}
