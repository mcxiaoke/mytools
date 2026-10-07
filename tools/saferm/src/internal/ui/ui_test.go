package ui

import (
	"bytes"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
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

func TestHumanBytes(t *testing.T) {
	cases := []struct {
		n         int64
		truncated bool
		want      string
	}{
		{0, false, "0 B"},
		{512, false, "512 B"},
		{1024, false, "1.0 KiB"},
		{1 << 30, false, "1.0 GiB"},
		{1 << 30, true, "≥ 1.0 GiB"},
		{3*(1<<30) + 500*(1<<20), false, "3.5 GiB"},
	}
	for _, tc := range cases {
		if got := HumanBytes(tc.n, tc.truncated); got != tc.want {
			t.Errorf("HumanBytes(%d, %v) = %q, 期望 %q", tc.n, tc.truncated, got, tc.want)
		}
	}
}

func TestHumanCount(t *testing.T) {
	cases := map[int64]string{0: "0", 7: "7", 999: "999", 1000: "1,000", 12483: "12,483", 1234567: "1,234,567"}
	for n, want := range cases {
		if got := HumanCount(n); got != want {
			t.Errorf("HumanCount(%d) = %q, 期望 %q", n, got, want)
		}
	}
}

// 确认级别必须只由"体量"和"是否在版本库工作区内"这类客观信号决定。
func TestDecide(t *testing.T) {
	cfg := DefaultConfirmConfig()

	cases := []struct {
		name  string
		risks []Risk
		want  Level
	}{
		{
			name:  "空批次",
			risks: nil,
			want:  LevelNone,
		},
		{
			name:  "一个小文件",
			risks: []Risk{{Target: "a.txt", Stats: ScanStats{Files: 1, Bytes: 10}}},
			want:  LevelNone,
		},
		{
			name:  "文件数超过确认阈值",
			risks: []Risk{{Target: "d", IsDir: true, Stats: ScanStats{Files: 60}}},
			want:  LevelConfirm,
		},
		{
			name:  "体积超过确认阈值",
			risks: []Risk{{Target: "d", IsDir: true, Stats: ScanStats{Files: 1, Bytes: 2 << 30}}},
			want:  LevelConfirm,
		},
		{
			name:  "是目录且始终确认目录",
			risks: []Risk{{Target: "d", IsDir: true, Stats: ScanStats{Files: 2}}},
			want:  LevelConfirm,
		},
		{
			name:  "统计不完整必须按最坏情况追问",
			risks: []Risk{{Target: "d", IsDir: true, Stats: ScanStats{Files: 1, Incomplete: true}}},
			want:  LevelConfirm,
		},
		{
			name:  "文件数超过危险阈值",
			risks: []Risk{{Target: "d", IsDir: true, Stats: ScanStats{Files: 6000}}},
			want:  LevelDanger,
		},
		{
			name:  "目标自身是版本库根即危险级",
			risks: []Risk{{Target: "/repo", IsDir: true, Stats: ScanStats{Files: 1}, Git: GitInfo{InRepo: true, RepoRoot: "/repo", IsRepoRoot: true}}},
			want:  LevelDanger,
		},
		{
			// 常开信号不能用来分级：仅仅"位于某个仓库内"不升危险级，
			// 否则开发者几乎所有文件都在仓库里，-y 就永远没用了（§14 D10 同理）
			name:  "仅位于版本库工作区内不升危险级",
			risks: []Risk{{Target: "/repo/src/a.txt", Stats: ScanStats{Files: 1}, Git: GitInfo{InRepo: true, RepoRoot: "/repo"}}},
			want:  LevelNone,
		},
		{
			name:  "仓库内的目录按体量走确认级",
			risks: []Risk{{Target: "/repo/src", IsDir: true, Stats: ScanStats{Files: 60}, Git: GitInfo{InRepo: true, RepoRoot: "/repo"}}},
			want:  LevelConfirm,
		},
		{
			name: "单个体量很小的文件也要确认（因为目录条件命中）",
			risks: []Risk{
				{Target: "a", Stats: ScanStats{Files: 1}},
				{Target: "d", IsDir: true, Stats: ScanStats{}},
			},
			want: LevelConfirm,
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := Decide(tc.risks, cfg); got != tc.want {
				t.Fatalf("Decide = %v，期望 %v", got, tc.want)
			}
		})
	}
}

// 关掉 always_confirm_dir 之后，普通小目录不该再追问（阈值仍然生效）。
func TestDecideRespectsDisabledDirConfirm(t *testing.T) {
	cfg := DefaultConfirmConfig()
	cfg.AlwaysConfirmDir = false
	risks := []Risk{{Target: "d", IsDir: true, Stats: ScanStats{Files: 2, Bytes: 100}}}
	if got := Decide(risks, cfg); got != LevelNone {
		t.Fatalf("关闭目录确认后应无需确认，实际 %v", got)
	}
}

func TestScanCountsFilesDirsAndBytes(t *testing.T) {
	root := t.TempDir()
	mustMkdir(t, filepath.Join(root, "a", "b"))
	mustWrite(t, filepath.Join(root, "f1.txt"), "12345")       // 5 B
	mustWrite(t, filepath.Join(root, "a", "f2.txt"), "123")    // 3 B
	mustWrite(t, filepath.Join(root, "a", "b", "f3.txt"), "1") // 1 B

	stats := Scan(root, ScanLimits{})
	if stats.Files != 3 {
		t.Fatalf("文件数 = %d，期望 3", stats.Files)
	}
	if stats.Dirs != 2 {
		t.Fatalf("子目录数 = %d，期望 2", stats.Dirs)
	}
	if stats.Bytes != 9 {
		t.Fatalf("字节数 = %d，期望 9", stats.Bytes)
	}
	if stats.Incomplete || stats.Truncated {
		t.Fatalf("不应有 incomplete/truncated：%+v", stats)
	}
}

func TestScanSingleFileAndMissing(t *testing.T) {
	root := t.TempDir()
	file := filepath.Join(root, "f.txt")
	mustWrite(t, file, "abc")

	stats := Scan(file, ScanLimits{})
	if stats.Files != 1 || stats.Bytes != 3 {
		t.Fatalf("单文件统计 = %+v", stats)
	}

	stats = Scan(filepath.Join(root, "nope"), ScanLimits{})
	if !stats.Incomplete {
		t.Fatalf("路径不存在时应标记统计不完整：%+v", stats)
	}
}

// 早退计数：达到上限就停，且必须把"数值只是下界"标出来。
func TestScanTruncatesEarly(t *testing.T) {
	root := t.TempDir()
	for i := 0; i < 200; i++ {
		mustWrite(t, filepath.Join(root, fmt.Sprintf("f%03d.txt", i)), "x")
	}

	stats := Scan(root, ScanLimits{MaxFiles: 10})
	if !stats.Truncated {
		t.Fatalf("达到上限后应标记 truncated：%+v", stats)
	}
	if stats.Files+stats.Dirs < 10 {
		t.Fatalf("早退时至少应数到上限，实际 %d", stats.Files+stats.Dirs)
	}
	if stats.Files+stats.Dirs > 30 {
		t.Fatalf("早退应当很快停下，实际数了 %d 个", stats.Files+stats.Dirs)
	}
}

// 链接本身只算 1 个条目，绝不递归进链接指向的目录。
func TestScanDoesNotFollowLinks(t *testing.T) {
	base := t.TempDir()
	outside := filepath.Join(base, "outside")
	mustMkdir(t, outside)
	for i := 0; i < 5; i++ {
		mustWrite(t, filepath.Join(outside, "f"+string(rune('a'+i))), "data")
	}

	root := filepath.Join(base, "root")
	mustMkdir(t, root)
	link := filepath.Join(root, "link-to-outside")
	if err := os.Symlink(outside, link); err != nil {
		t.Skipf("当前环境无法创建符号链接（Windows 需要开发者模式或管理员）：%v", err)
	}

	stats := Scan(root, ScanLimits{})
	if stats.Files != 1 {
		t.Fatalf("链接应只算 1 个条目，实际文件数 = %d（说明递归进去了）", stats.Files)
	}
	if stats.Bytes != 0 {
		t.Fatalf("链接自身不该累加体积，实际 %d", stats.Bytes)
	}
}

// .git 是目录（普通仓库）与是文件（worktree / submodule）两种情况都要认出来。
func TestDetectGitBothForms(t *testing.T) {
	base := t.TempDir()

	t.Run("普通仓库：.git 是目录", func(t *testing.T) {
		repo := filepath.Join(base, "repo-dir")
		mustMkdir(t, filepath.Join(repo, ".git"))
		deep := filepath.Join(repo, "src", "pkg")
		mustMkdir(t, deep)
		mustWrite(t, filepath.Join(deep, "a.go"), "x")

		got := DetectGit(deep)
		if !got.InRepo || got.RepoRoot != repo {
			t.Fatalf("DetectGit(%q) = %+v，期望 root=%q", deep, got, repo)
		}
		if got := DetectGit(filepath.Join(deep, "a.go")); !got.InRepo || got.RepoRoot != repo {
			t.Fatalf("对文件也应能向上找到仓库：%+v", got)
		}
	})

	t.Run("worktree/submodule：.git 是文件", func(t *testing.T) {
		sub := filepath.Join(base, "submodule")
		mustMkdir(t, sub)
		mustWrite(t, filepath.Join(sub, ".git"), "gitdir: ../.git/modules/submodule\n")
		inner := filepath.Join(sub, "src")
		mustMkdir(t, inner)

		got := DetectGit(inner)
		if !got.InRepo || got.RepoRoot != sub {
			t.Fatalf("DetectGit(%q) = %+v，期望 root=%q（.git 为文件时必须识别）", inner, got, sub)
		}
	})

	t.Run("不在仓库内", func(t *testing.T) {
		plain := filepath.Join(base, "plain")
		mustMkdir(t, plain)

		got := DetectGit(plain)
		// 临时目录的上级可能是某个仓库（例如在源码树里跑测试）：
		// 那样只要求"找到的根在临时目录之外"，否则才要求不在仓库内。
		if got.InRepo && strings.HasPrefix(got.RepoRoot, base) {
			t.Fatalf("临时目录内没有 .git，不该把仓库根认到这里：%+v", got)
		}
	})
}

// 非交互环境：只有显式开关才能放行，且危险级必须用那个长名字的开关。
func TestAskNonInteractivePolicy(t *testing.T) {
	req := func(level Level) Request {
		return Request{
			Level: level,
			Risks: []Risk{{Target: "/tmp/proj", IsDir: true, Stats: ScanStats{Files: 3}}},
			Dest:  []string{"/trash/op/proj"},
		}
	}

	cases := []struct {
		name      string
		level     Level
		approval  Approval
		wantOK    bool
		wantErrIs bool
	}{
		{"无需确认", LevelNone, Approval{}, true, false},
		{"确认级且未授权", LevelConfirm, Approval{}, false, true},
		{"确认级 + -y", LevelConfirm, Approval{SkipConfirm: true}, true, false},
		{"危险级 + -y 不足以放行", LevelDanger, Approval{SkipConfirm: true}, false, true},
		{"危险级 + --yes-i-am-sure", LevelDanger, Approval{YesIAmSure: true}, true, false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			p := &Prompter{In: strings.NewReader(""), Out: &bytes.Buffer{}, IsTTY: func() bool { return false }}
			ok, err := p.Ask(req(tc.level), tc.approval)
			if ok != tc.wantOK {
				t.Fatalf("Ask = %v，期望 %v（err=%v）", ok, tc.wantOK, err)
			}
			if tc.wantErrIs && !errors.Is(err, ErrNonInteractive) {
				t.Fatalf("期望 ErrNonInteractive，实际 %v", err)
			}
			if !tc.wantErrIs && err != nil {
				t.Fatalf("不该报错，实际 %v", err)
			}
			if tc.wantErrIs && err != nil && !strings.Contains(err.Error(), "--yes") {
				t.Fatalf("错误里应告诉用户该加什么开关，实际 %v", err)
			}
		})
	}
}

// 交互环境：确认级输入 yes 才放行；危险级必须手敲目标名称。
func TestAskInteractive(t *testing.T) {
	dangerReq := Request{
		Level: LevelDanger,
		Risks: []Risk{{Target: filepath.Join("/tmp", "saferm"), IsDir: true, Stats: ScanStats{Files: 6000}}},
		Dest:  []string{"/trash/op/tmp/saferm"},
	}
	confirmReq := Request{
		Level: LevelConfirm,
		Risks: []Risk{{Target: "/tmp/proj", IsDir: true, Stats: ScanStats{Files: 60}}},
		Dest:  []string{"/trash/op/tmp/proj"},
	}

	cases := []struct {
		name   string
		req    Request
		input  string
		wantOK bool
	}{
		{"确认级输入 yes", confirmReq, "yes\n", true},
		{"确认级输入 YES 大小写不敏感", confirmReq, "YES\n", true},
		{"确认级输入 y 不算", confirmReq, "y\n", false},
		{"确认级直接回车取消", confirmReq, "\n", false},
		{"确认级 EOF 视作取消", confirmReq, "", false},
		{"危险级输入 yes 不算", dangerReq, "yes\n", false},
		{"危险级手敲目标名", dangerReq, "saferm\n", true},
		{"危险级敲错名字", dangerReq, "safem\n", false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			var out bytes.Buffer
			p := &Prompter{In: strings.NewReader(tc.input), Out: &out, IsTTY: func() bool { return true }}
			ok, err := p.Ask(tc.req, Approval{})
			if err != nil {
				t.Fatalf("Ask: %v", err)
			}
			if ok != tc.wantOK {
				t.Fatalf("Ask = %v，期望 %v", ok, tc.wantOK)
			}
			if !strings.Contains(out.String(), "将移动到回收目录") {
				t.Fatalf("摘要应说明要做什么，实际输出：\n%s", out.String())
			}
		})
	}
}

// 摘要必须让用户看到"放到哪"和"在哪个仓库里"（后者正是本次事故的关键信息）。
func TestAskSummaryShowsDestinationAndRepo(t *testing.T) {
	req := Request{
		Level: LevelConfirm,
		Risks: []Risk{{
			Target: "/work/my-repo/src",
			IsDir:  true,
			Stats:  ScanStats{Files: 12483, Dirs: 1200, Bytes: 2 << 30},
			Git:    GitInfo{InRepo: true, RepoRoot: "/work/my-repo"},
		}},
		Dest: []string{"/.saferm-trash/20261006-213000-1/work/my-repo/src"},
	}

	var out bytes.Buffer
	p := &Prompter{In: strings.NewReader("no\n"), Out: &out, IsTTY: func() bool { return true }}
	if _, err := p.Ask(req, Approval{}); err != nil {
		t.Fatalf("Ask: %v", err)
	}

	text := out.String()
	for _, want := range []string{
		"/work/my-repo/src",
		"12,483",
		"2.0 GiB",
		"/work/my-repo",
		"未提交",
		"/.saferm-trash/20261006-213000-1/work/my-repo/src",
	} {
		if !strings.Contains(text, want) {
			t.Errorf("摘要里缺少 %q：\n%s", want, text)
		}
	}
}

// 统计不完整时必须在摘要里说清楚，不能让人以为"就这么点"。
func TestAskSummaryFlagsIncompleteScan(t *testing.T) {
	req := Request{
		Level: LevelConfirm,
		Risks: []Risk{{
			Target: "/work/d",
			IsDir:  true,
			Stats:  ScanStats{Files: 3, Bytes: 10, Incomplete: true, Truncated: true},
		}},
	}

	var out bytes.Buffer
	p := &Prompter{In: strings.NewReader("no\n"), Out: &out, IsTTY: func() bool { return true }}
	if _, err := p.Ask(req, Approval{}); err != nil {
		t.Fatalf("Ask: %v", err)
	}
	if !strings.Contains(out.String(), "统计不完整") {
		t.Fatalf("应提示统计不完整：\n%s", out.String())
	}
	if !strings.Contains(out.String(), "≥") {
		t.Fatalf("截断的数值应标为下界：\n%s", out.String())
	}
}
