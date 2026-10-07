package config

import (
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"strings"
	"testing"
)

// assertDefault 断言出错时返回的是 Default()。
// Config 含 map，不能用 == 比较，所以走 reflect.DeepEqual。
func assertDefault(t *testing.T, cfg Config) {
	t.Helper()
	if !reflect.DeepEqual(cfg, Default()) {
		t.Fatalf("出错时应返回 Default()，实际 %+v", cfg)
	}
}

// 设计文档 §11 的 config 单测要求：正常配置、缺失文件、字段缺省、
// [trash.roots] 嵌套表与带引号的键，以及**类型不符 / 语法错误 / 键名写错必须硬失败**
// 的分支逐个覆盖。文档点名"配置读不进去却看起来照常工作"是最危险的失败模式，
// 所以这部分测试要配得上那句话——每一条都断言 error 非空且信息可定位。

func TestParseFullConfig(t *testing.T) {
	src := `
[trash]
default_root = "auto"

[trash.roots]
C = "C:\\Users\\me\\AppData\\Local\\saferm\\trash\\C"
D = "D:\\.saferm-trash"
"/home" = "/data/.saferm-trash"

[confirm]
file_threshold        = 10
danger_file_threshold = 100
bytes_threshold       = 2048
always_confirm_dir    = false

[guard]
protect_vcs_root = false
git_detect       = false
extra_protected  = ["C:\\Windows", "/etc"]
`
	cfg, err := Parse(strings.NewReader(src))
	if err != nil {
		t.Fatalf("正常配置不该报错：%v", err)
	}

	if cfg.Trash.DefaultRoot != "auto" {
		t.Errorf("trash.default_root = %q，期望 auto", cfg.Trash.DefaultRoot)
	}
	// 带引号的键（Linux 挂载点）与普通键都要能读出来
	wantRoots := map[string]string{
		"C":     `C:\Users\me\AppData\Local\saferm\trash\C`,
		"D":     `D:\.saferm-trash`,
		"/home": "/data/.saferm-trash",
	}
	for k, want := range wantRoots {
		if got := cfg.Trash.Roots[k]; got != want {
			t.Errorf("trash.roots[%q] = %q，期望 %q", k, got, want)
		}
	}

	if cfg.Confirm.FileThreshold != 10 || cfg.Confirm.DangerFileThreshold != 100 ||
		cfg.Confirm.BytesThreshold != 2048 || cfg.Confirm.AlwaysConfirmDir {
		t.Errorf("[confirm] 解析结果不对：%+v", cfg.Confirm)
	}
	if cfg.Guard.ProtectVCSRoot || cfg.Guard.GitDetect {
		t.Errorf("[guard] 的布尔值应为 false（显式写 false 必须覆盖默认 true）：%+v", cfg.Guard)
	}
	if len(cfg.Guard.ExtraProtected) != 2 ||
		cfg.Guard.ExtraProtected[0] != `C:\Windows` ||
		cfg.Guard.ExtraProtected[1] != "/etc" {
		t.Errorf("extra_protected = %#v", cfg.Guard.ExtraProtected)
	}
}

// 缺省：未出现的项保留内置默认值，不能被清零。
func TestParseKeepsDefaultsForAbsentFields(t *testing.T) {
	cases := map[string]string{
		"空文档":      "",
		"只有注释":     "# 什么都没有\n",
		"只给一个空表头":  "[guard]\n",
		"给一个空表当作值": "[trash.roots]\n",
	}
	want := Default()
	for name, src := range cases {
		t.Run(name, func(t *testing.T) {
			cfg, err := Parse(strings.NewReader(src))
			if err != nil {
				t.Fatalf("Parse: %v", err)
			}
			// 逐字段比对，而不是整体 DeepEqual：一眼能看出是哪个字段被清零了
			if cfg.Trash.DefaultRoot != want.Trash.DefaultRoot {
				t.Errorf("default_root = %q，期望 %q", cfg.Trash.DefaultRoot, want.Trash.DefaultRoot)
			}
			if len(cfg.Trash.Roots) != 0 {
				t.Errorf("没写按卷覆盖时不该有内容：%#v", cfg.Trash.Roots)
			}
			if cfg.Confirm.FileThreshold != want.Confirm.FileThreshold {
				t.Errorf("file_threshold = %d，期望默认 %d", cfg.Confirm.FileThreshold, want.Confirm.FileThreshold)
			}
			if cfg.Confirm.DangerFileThreshold != want.Confirm.DangerFileThreshold {
				t.Errorf("danger_file_threshold = %d，期望默认 %d", cfg.Confirm.DangerFileThreshold, want.Confirm.DangerFileThreshold)
			}
			if cfg.Confirm.BytesThreshold != want.Confirm.BytesThreshold {
				t.Errorf("bytes_threshold = %d，期望默认 %d", cfg.Confirm.BytesThreshold, want.Confirm.BytesThreshold)
			}
			if cfg.Confirm.AlwaysConfirmDir != want.Confirm.AlwaysConfirmDir {
				t.Errorf("always_confirm_dir = %v，期望默认 %v", cfg.Confirm.AlwaysConfirmDir, want.Confirm.AlwaysConfirmDir)
			}
			if cfg.Guard.ProtectVCSRoot != want.Guard.ProtectVCSRoot {
				t.Errorf("protect_vcs_root = %v，期望默认 %v", cfg.Guard.ProtectVCSRoot, want.Guard.ProtectVCSRoot)
			}
			if cfg.Guard.GitDetect != want.Guard.GitDetect {
				t.Errorf("git_detect = %v，期望默认 %v", cfg.Guard.GitDetect, want.Guard.GitDetect)
			}
		})
	}
}

// 只写了同一段里的一部分字段时，同段的其它字段必须保持默认，
// 不能因为"这段出现过"就把整段清零。
func TestParsePartialSectionKeepsSiblingDefaults(t *testing.T) {
	cfg, err := Parse(strings.NewReader("[confirm]\nfile_threshold = 7\n"))
	if err != nil {
		t.Fatalf("Parse: %v", err)
	}
	want := Default()
	if cfg.Confirm.FileThreshold != 7 {
		t.Errorf("file_threshold = %d，期望 7（文件里写了）", cfg.Confirm.FileThreshold)
	}
	if cfg.Confirm.DangerFileThreshold != want.Confirm.DangerFileThreshold {
		t.Errorf("danger_file_threshold = %d，期望默认 %d",
			cfg.Confirm.DangerFileThreshold, want.Confirm.DangerFileThreshold)
	}
	if cfg.Confirm.BytesThreshold != want.Confirm.BytesThreshold {
		t.Errorf("bytes_threshold = %d，期望默认 %d", cfg.Confirm.BytesThreshold, want.Confirm.BytesThreshold)
	}
	if cfg.Confirm.AlwaysConfirmDir != want.Confirm.AlwaysConfirmDir {
		t.Errorf("always_confirm_dir = %v，期望默认 %v",
			cfg.Confirm.AlwaysConfirmDir, want.Confirm.AlwaysConfirmDir)
	}
}

// 显式 false 必须覆盖默认 true —— 这是"配置看起来写了其实没生效"的典型变体。
func TestParseExplicitFalseOverridesDefault(t *testing.T) {
	cfg, err := Parse(strings.NewReader("[confirm]\nalways_confirm_dir = false\n"))
	if err != nil {
		t.Fatalf("Parse: %v", err)
	}
	if cfg.Confirm.AlwaysConfirmDir {
		t.Fatal("always_confirm_dir 显式写成 false，却被默认 true 覆盖了")
	}
	// 同一次解析里，没写的项仍应是默认 true
	if !cfg.Guard.ProtectVCSRoot {
		t.Error("未出现在文件里的 protect_vcs_root 不该被改动")
	}
}

// 硬失败清单：每一条都必须在**解析阶段**就报错，绝不静默回退默认值。
func TestParseHardFails(t *testing.T) {
	cases := []struct {
		name string
		src  string
		// 报错信息里必须出现的关键词，用来证明"能定位到问题"
		wantHit []string
	}{
		{
			name:    "顶层键名写错",
			src:     "tras = 1\n",
			wantHit: []string{"无法识别"},
		},
		{
			name:    "表名写错",
			src:     "[tras]\ndefault_root = \"auto\"\n",
			wantHit: []string{"无法识别"},
		},
		{
			name:    "段内键名写错",
			src:     "[trash]\ndefault_rooot = \"auto\"\n",
			wantHit: []string{"无法识别", "default_rooot"},
		},
		{
			name:    "整型位给了字符串",
			src:     "[confirm]\nfile_threshold = \"五十\"\n",
			wantHit: []string{"类型不符", "file_threshold"},
		},
		{
			name:    "布尔位给了字符串",
			src:     "[guard]\nprotect_vcs_root = \"yes\"\n",
			wantHit: []string{"类型不符", "protect_vcs_root"},
		},
		{
			name:    "字符串位给了整型",
			src:     "[trash]\ndefault_root = 123\n",
			wantHit: []string{"类型不符", "default_root"},
		},
		{
			name:    "数组位给了字符串",
			src:     "[guard]\nextra_protected = \"C:\\\\Windows\"\n",
			wantHit: []string{"类型不符", "extra_protected"},
		},
		{
			name:    "按卷表的值给了子表",
			src:     "[trash.roots.sub]\nx = 1\n",
			wantHit: []string{"类型不符", "roots"},
		},
		{
			name:    "语法错误：表头没闭合",
			src:     "[trash\ndefault_root = \"auto\"\n",
			wantHit: []string{"语法"},
		},
		{
			name:    "语法错误：等号写重了",
			src:     "[confirm]\nfile_threshold = = 5\n",
			wantHit: []string{"语法"},
		},
		{
			name:    "语法错误：键重复定义",
			src:     "[trash]\ndefault_root = \"a\"\ndefault_root = \"b\"\n",
			wantHit: []string{"already defined", "3|"},
		},
		{
			name:    "语法错误：多行字符串没闭合",
			src:     "[trash]\ndefault_root = \"\"\"abc\n",
			wantHit: []string{"语法"},
		},
		{
			name:    "非法 UTF-8",
			src:     "[trash]\ndefault_root = \"\xff\xfe\"\n",
			wantHit: []string{"语法"},
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			cfg, err := Parse(strings.NewReader(tc.src))
			if err == nil {
				t.Fatalf("这份配置必须硬失败，却解析成功了：%q\n结果=%+v", tc.src, cfg)
			}
			msg := err.Error()
			for _, hit := range tc.wantHit {
				if !strings.Contains(msg, hit) {
					t.Errorf("报错信息里应包含 %q，实际：\n%s", hit, msg)
				}
			}
		})
	}
}

// 报错必须带行号与原文片段（设计文档 §7.1），否则用户拿到一句话不知道去哪改。
func TestParseErrorIncludesLineAndSnippet(t *testing.T) {
	src := "# 第一行注释\n[trash]\ndefault_rooot = \"auto\"\n"
	_, err := Parse(strings.NewReader(src))
	if err == nil {
		t.Fatal("键名写错必须报错")
	}
	msg := err.Error()
	// 出错行是第 3 行，片段要能看出行号与原文
	for _, want := range []string{"3|", "default_rooot"} {
		if !strings.Contains(msg, want) {
			t.Errorf("报错信息里应包含 %q（行号或原文片段），实际：\n%s", want, msg)
		}
	}
}

// 出错时返回的应当是 Default()，而不是零值配置。
// 零值的阈值全为 0 且 always_confirm_dir 为 false，等于"不再追问"；
// 万一哪个调用方漏检 error，宁可它退化成默认行为，也不能退化成最不设防的配置。
func TestParseReturnsSafeDefaultsOnError(t *testing.T) {
	cfg, err := Parse(strings.NewReader("[confirm]\nfile_threshold = \"五十\"\n"))
	if err == nil {
		t.Fatal("类型不符必须报错")
	}
	assertDefault(t, cfg)
	if cfg.Confirm.FileThreshold == 0 || !cfg.Confirm.AlwaysConfirmDir {
		t.Fatal("返回值不能是零值配置（零值等于关掉确认）")
	}
}

// ---- DefaultPath ----

// 两个平台的分支都在这里覆盖，不依赖宿主 OS：defaultPath 把 goos 当参数收。
// 期望值用 filepath.Join 拼出来，避免写死分隔符导致在另一平台上误判。
func TestDefaultPathPerPlatform(t *testing.T) {
	cases := []struct {
		name                           string
		goos, appData, xdg, home, want string
	}{
		{
			name: "windows_用 APPDATA", goos: "windows",
			appData: `D:\AppData\Roaming`, xdg: "/should/be/ignored", home: "/should/be/ignored",
			want: filepath.Join(`D:\AppData\Roaming`, "saferm", "config.toml"),
		},
		{
			name: "windows_没有 APPDATA 就返回空", goos: "windows",
			xdg: "/xdg", home: "/home/me",
			want: "",
		},
		{
			name: "linux_优先 XDG_CONFIG_HOME", goos: "linux",
			xdg: "/xdg", home: "/home/me",
			want: filepath.Join("/xdg", "saferm", "config.toml"),
		},
		{
			name: "linux_回退 HOME", goos: "linux",
			home: "/home/me",
			want: filepath.Join("/home/me", ".config", "saferm", "config.toml"),
		},
		{
			name: "linux_两个都没有就返回空", goos: "linux",
			want: "",
		},
		{
			// 非 windows 一律按 Unix 规则走（darwin 等），别把它漏到空分支
			name: "darwin_按 Unix 规则", goos: "darwin",
			home: "/Users/me",
			want: filepath.Join("/Users/me", ".config", "saferm", "config.toml"),
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := defaultPath(tc.goos, tc.appData, tc.xdg, tc.home); got != tc.want {
				t.Errorf("defaultPath(%q, %q, %q, %q) = %q，期望 %q",
					tc.goos, tc.appData, tc.xdg, tc.home, got, tc.want)
			}
		})
	}
}

// DefaultPath 这个壳只负责"把本机环境读出来交给纯函数"，这里钉住这层接线。
func TestDefaultPathReadsHostEnvironment(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Setenv("APPDATA", `D:\AppData\Roaming`)
		want := filepath.Join(`D:\AppData\Roaming`, "saferm", "config.toml")
		if got := DefaultPath(); got != want {
			t.Errorf("DefaultPath() = %q，期望 %q", got, want)
		}
		t.Setenv("APPDATA", "")
		if got := DefaultPath(); got != "" {
			t.Errorf("没有 APPDATA 时应返回空，实际 %q", got)
		}
		return
	}
	t.Setenv("XDG_CONFIG_HOME", "/xdg")
	if got, want := DefaultPath(), "/xdg/saferm/config.toml"; got != want {
		t.Errorf("DefaultPath() = %q，期望 %q", got, want)
	}
	t.Setenv("XDG_CONFIG_HOME", "")
	t.Setenv("HOME", "/home/someone")
	if got, want := DefaultPath(), "/home/someone/.config/saferm/config.toml"; got != want {
		t.Errorf("DefaultPath() = %q，期望 %q", got, want)
	}
	t.Setenv("HOME", "")
	if got := DefaultPath(); got != "" {
		t.Errorf("没有任何环境变量时应返回空，实际 %q", got)
	}
}

// ---- Load ----

func TestLoadExplicitMissingFileIsAnError(t *testing.T) {
	missing := filepath.Join(t.TempDir(), "nope", "config.toml")
	cfg, used, err := Load(missing)
	if err == nil {
		t.Fatal("显式指定的文件不存在时必须报错（用户是明确指定的，不能装作没看见）")
	}
	if !strings.Contains(err.Error(), "不存在") {
		t.Errorf("报错应说明文件不存在：%v", err)
	}
	if used != "" {
		t.Errorf("出错时 used 应为空，实际 %q", used)
	}
	assertDefault(t, cfg)
}

func TestLoadExplicitFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.toml")
	if err := os.WriteFile(path, []byte("[confirm]\nfile_threshold = 3\n"), 0o644); err != nil {
		t.Fatalf("write: %v", err)
	}
	cfg, used, err := Load(path)
	if err != nil {
		t.Fatalf("Load: %v", err)
	}
	if used != path {
		t.Errorf("used = %q，期望 %q", used, path)
	}
	if cfg.Confirm.FileThreshold != 3 {
		t.Errorf("file_threshold = %d，期望 3", cfg.Confirm.FileThreshold)
	}
}

func TestLoadExplicitFileRejectsBadConfig(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.toml")
	if err := os.WriteFile(path, []byte("[trash]\ndefault_rooot = \"x\"\n"), 0o644); err != nil {
		t.Fatalf("write: %v", err)
	}
	_, _, err := Load(path)
	if err == nil {
		t.Fatal("配置有错时必须报错")
	}
	// 报错要带上文件路径，用户才知道是哪个文件出的问题
	if !strings.Contains(err.Error(), path) {
		t.Errorf("报错里应包含文件路径 %q：%v", path, err)
	}
}

// 默认位置没有文件 → 用默认值、used 为空，这**不是**错误（设计文档 §7.1）。
func TestLoadDefaultPathMissingIsNotAnError(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Setenv("APPDATA", filepath.Join(t.TempDir(), "appdata-none"))
	} else {
		t.Setenv("XDG_CONFIG_HOME", filepath.Join(t.TempDir(), "xdg-none"))
	}
	cfg, used, err := Load("")
	if err != nil {
		t.Fatalf("默认位置没有配置文件时不该报错：%v", err)
	}
	if used != "" {
		t.Errorf("没有读到配置文件时 used 应为空，实际 %q", used)
	}
	assertDefault(t, cfg)
}

// 默认位置**有**文件时，used 要报出它，并且内容真的被读进来。
func TestLoadDefaultPathReadsFile(t *testing.T) {
	base := t.TempDir()
	var path string
	if runtime.GOOS == "windows" {
		t.Setenv("APPDATA", base)
		path = filepath.Join(base, "saferm", "config.toml")
	} else {
		t.Setenv("XDG_CONFIG_HOME", base)
		path = filepath.Join(base, "saferm", "config.toml")
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatalf("mkdir: %v", err)
	}
	if err := os.WriteFile(path, []byte("[guard]\ngit_detect = false\n"), 0o644); err != nil {
		t.Fatalf("write: %v", err)
	}

	cfg, used, err := Load("")
	if err != nil {
		t.Fatalf("Load: %v", err)
	}
	if used != path {
		t.Errorf("used = %q，期望 %q", used, path)
	}
	if cfg.Guard.GitDetect {
		t.Error("默认位置的配置文件没有被真正读取（git_detect 仍是默认 true）")
	}
}

// 默认位置**存在但写错**时同样必须硬失败——这是最要命的场景：
// 用户改了配置却以为生效了，而工具"看起来照常工作"。
func TestLoadDefaultPathRejectsBadConfig(t *testing.T) {
	base := t.TempDir()
	var path string
	if runtime.GOOS == "windows" {
		t.Setenv("APPDATA", base)
	} else {
		t.Setenv("XDG_CONFIG_HOME", base)
	}
	path = filepath.Join(base, "saferm", "config.toml")
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatalf("mkdir: %v", err)
	}
	if err := os.WriteFile(path, []byte("[confirm]\nfile_threshhold = 1\n"), 0o644); err != nil {
		t.Fatalf("write: %v", err)
	}

	cfg, used, err := Load("")
	if err == nil {
		t.Fatal("默认位置的配置写错时必须报错，绝不静默回退默认值")
	}
	if used != "" {
		t.Errorf("出错时 used 应为空，实际 %q", used)
	}
	assertDefault(t, cfg)
}

// 默认位置是目录而不是文件：不是"不存在"，要如实报错而不是装作没事。
func TestLoadDefaultPathIsDirectory(t *testing.T) {
	base := t.TempDir()
	if runtime.GOOS == "windows" {
		t.Setenv("APPDATA", base)
	} else {
		t.Setenv("XDG_CONFIG_HOME", base)
	}
	// 把 config.toml 造成目录
	if err := os.MkdirAll(filepath.Join(base, "saferm", "config.toml"), 0o755); err != nil {
		t.Fatalf("mkdir: %v", err)
	}
	_, _, err := Load("")
	if err == nil {
		t.Fatal("配置路径是目录时应报错")
	}
}
