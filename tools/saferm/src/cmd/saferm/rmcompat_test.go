package main

import (
	"path/filepath"
	"strings"
	"testing"
)

// 本文件覆盖"从 rm 迁移过来"的那部分接口：
// 哪些 rm 参数被接受并忽略、哪些被拒绝、拒绝时有没有讲清楚原因与等价写法。

// --preserve-root 对 saferm 恒为真（卷根保护无法关闭），因此接受并忽略。
// GNU 9.x 的写法是 `--preserve-root[=all]`，两种形式都要能收。
func TestRMCompatPreserveRootAccepted(t *testing.T) {
	for _, form := range []string{"--preserve-root", "--preserve-root=all", "--preserve-root=true"} {
		t.Run(form, func(t *testing.T) {
			base := t.TempDir()
			target := filepath.Join(base, "a.txt")
			mustWrite(t, target, "x")
			trashRoot := filepath.Join(base, "trash")

			res := run(t, "", false, "-y", form, "--trash-root", trashRoot, target)
			if res.code != exitOK {
				t.Fatalf("`%s` 应被接受（退出码 0），实际 %d\nstdout:\n%s\nstderr:\n%s",
					form, res.code, res.stdout, res.stderr)
			}
			if !pathExists(filepath.Join(trashRoot, ".saferm-trash-root")) {
				t.Errorf("应照常完成移动：%s", trashRoot)
			}
			mustNotExist(t, target)
		})
	}
}

// --no-preserve-root 要求关闭卷根保护，saferm 必须拒绝，并且要说清为什么。
func TestRMCompatNoPreserveRootRejected(t *testing.T) {
	base := t.TempDir()
	target := filepath.Join(base, "a.txt")
	mustWrite(t, target, "x")

	res := run(t, "", false, "-y", "--no-preserve-root", target)
	if res.code != exitUsage {
		t.Fatalf("退出码 = %d，期望 1\nstderr:\n%s", res.code, res.stderr)
	}
	if !strings.Contains(res.stderr, "卷根保护") {
		t.Errorf("应说明是拒绝关闭卷根保护：\n%s", res.stderr)
	}
	if !pathExists(target) {
		t.Errorf("拒绝后目标必须原封不动：%s", target)
	}
}

// rm 有而 saferm 故意没有的参数：必须报错，且报错里要
// ① 点出到底是哪个参数（pflag 的原始错误，此前被整个吞掉了）
// ② 给出等价写法。
func TestRMCompatUnsupportedFlagsExplainThemselves(t *testing.T) {
	cases := []struct {
		flag     string
		mentions string // 报错里必须出现的参数名（pflag 原始错误里会带上它）
		wantHint string // 等价写法提示里的关键字
	}{
		// -d / -I / --one-file-system：完全没定义过 → pflag 说 unknown
		{"-d", "-d", "没有 -d"},
		{"--dir", "--dir", "没有 --dir"},
		{"-I", "-I", "没有 -I"},
		{"--one-file-system", "--one-file-system", "同卷"},
		// --interactive 我们是定义了的（布尔），所以 pflag 报的是"取值非法"而不是"未知参数"。
		// 两种都不能只丢一句用法提示了事，都得说清是哪个参数、并给出 saferm 的等价开关。
		{"--interactive=never", "--interactive", "确认开关是 -i"},
	}

	for _, tc := range cases {
		t.Run(tc.flag, func(t *testing.T) {
			res := run(t, "", false, tc.flag, filepath.Join(t.TempDir(), "x"))
			if res.code != exitUsage {
				t.Fatalf("`%s` 应报用法错误（退出码 1），实际 %d\nstderr:\n%s", tc.flag, res.code, res.stderr)
			}
			// ① 必须点出是哪个参数
			if !strings.Contains(res.stderr, tc.mentions) {
				t.Errorf("报错里应点出参数 %q：\n%s", tc.mentions, res.stderr)
			}
			// ② 必须给出等价写法
			if !strings.Contains(res.stderr, tc.wantHint) {
				t.Errorf("报错里应包含等价写法提示 %q：\n%s", tc.wantHint, res.stderr)
			}
		})
	}
}

// 对真正陌生的开关：给出原始原因，但**不该**硬套 rm 的等价写法提示。
func TestRMCompatNoHintForUnrelatedFlag(t *testing.T) {
	res := run(t, "", false, "--definitely-not-a-flag", filepath.Join(t.TempDir(), "x"))
	if res.code != exitUsage {
		t.Fatalf("退出码 = %d，期望 1", res.code)
	}
	if !strings.Contains(res.stderr, "unknown flag") {
		t.Errorf("应报出原始原因：\n%s", res.stderr)
	}
	if strings.Contains(res.stderr, "saferm 没有") {
		t.Errorf("不该给不相关的参数套 rm 等价写法：\n%s", res.stderr)
	}
}

// `--` 之后一律是路径，不该再被当成 rm 参数去套提示。
func TestRMCompatHintRespectsDoubleDash(t *testing.T) {
	base := t.TempDir()
	res := run(t, "", false, "-y", "--", filepath.Join(base, "-d"))
	if res.code != exitUsage {
		t.Fatalf("路径不存在应退出 1，实际 %d", res.code)
	}
	if strings.Contains(res.stderr, "没有 -d") {
		t.Errorf("`--` 之后的内容是路径，不该触发 rm 参数提示：\n%s", res.stderr)
	}
	if !strings.Contains(res.stderr, "路径不存在") {
		t.Errorf("应报路径不存在：\n%s", res.stderr)
	}
}

// where 的未知参数此前是**静默失败**：退出码 1 但一句话都不打印。
func TestWhereUnknownFlagReportsError(t *testing.T) {
	res := run(t, "", false, "where", "--definitely-not-a-flag")
	if res.code != exitUsage {
		t.Fatalf("退出码 = %d，期望 1", res.code)
	}
	if strings.TrimSpace(res.stderr) == "" {
		t.Fatal("where 的未知参数不能静默失败，必须给出原因")
	}
	if !strings.Contains(res.stderr, "unknown flag") {
		t.Errorf("应报出原始原因：\n%s", res.stderr)
	}
	if !strings.Contains(res.stderr, "用法") {
		t.Errorf("应给出用法提示：\n%s", res.stderr)
	}
}

// where 也要认 --preserve-root（同一个 rm 兼容面），不该报未知参数。
func TestWhereAcceptsPreserveRoot(t *testing.T) {
	res := run(t, "", false, "where", "--preserve-root")
	if res.code == exitUsage && strings.Contains(res.stderr, "unknown flag") {
		t.Fatalf("where 应接受 --preserve-root：\n%s", res.stderr)
	}
}

// rm 的两个高频组合必须照旧可用，不能被这次改动影响。
func TestRMCompatCommonCombinationsStillWork(t *testing.T) {
	for _, combo := range []string{"-rf", "-ry", "-r"} {
		t.Run(combo, func(t *testing.T) {
			base := t.TempDir()
			target := filepath.Join(base, "a.txt")
			mustWrite(t, target, "x")
			trashRoot := filepath.Join(base, "trash")

			res := run(t, "", false, combo, "--trash-root", trashRoot, target)
			if res.code != exitOK {
				t.Fatalf("`%s` 退出码 = %d，期望 0\nstderr:\n%s", combo, res.code, res.stderr)
			}
			mustNotExist(t, target)
		})
	}
}
