package invariants

import (
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strings"
	"testing"
)

// 本文件是设计文档 §10.1（C8）的可执行版本：**v1 的代码里不存在任何永久删除调用**。
//
// 用 go/ast 解析而不是正则匹配源码文本，原因有两个：
//   - 注释与字符串里出现 "os.Remove" 不该算违规（例如"不要用 os.Remove"这种说明）；
//   - 正则很容易漏掉 `o "os"` 这种别名导入或 `os . "os"` 点导入。
//
// 断言两条：
//
//	A. 全库（**含测试**）不出现任何删除类调用；
//	B. 非测试源码里对 os 包的"写入类"调用必须落在允许集合内：
//	   os.Rename / os.Mkdir / os.MkdirAll / os.WriteFile。
//
// B 的意义在于：将来有人加了 os.Chmod、os.Truncate、os.OpenFile 之类的写操作，
// 这条会拦下来，逼着人明确"这是不是允许的"，而不是悄悄扩大工具的写权限。

// trackedImports 是我们要盯住的包（按导入路径）。
// 删除能力主要来自这三处：标准库 os、底层 syscall、以及 x/sys/windows。
var trackedImports = map[string]bool{
	"os":                       true,
	"syscall":                  true,
	"golang.org/x/sys/windows": true,
}

// forbiddenCall 是删除类调用的名字模式。刻意用前缀/正则而不是逐个列举，
// 免得漏掉 RemoveAll / RemoveDirectoryW / DeleteFileW / Unlinkat 这类变体。
var forbiddenCall = regexp.MustCompile(`(?i)^(remove|delete|unlink|rmdir|shfileoperation)`)

// osWriteLike 是 os 包里"会改动文件系统"的函数名。
// 只列写入类：读取类的（Stat/Lstat/ReadDir/ReadFile/Getenv/...）不在此列，随便用。
var osWriteLike = map[string]bool{
	"Create":       true,
	"CreateTemp":   true,
	"Mkdir":        true,
	"MkdirAll":     true,
	"MkdirTemp":    true,
	"WriteFile":    true,
	"OpenFile":     true,
	"Rename":       true,
	"Chmod":        true,
	"Chown":        true,
	"Lchown":       true,
	"Truncate":     true,
	"Symlink":      true,
	"Link":         true,
	"Chtimes":      true,
	"StartProcess": true,
}

// allowedOSWrites 是设计文档 §10.1 允许的写操作集合（非测试源码）。
//
// 注意这里比文档多一个 os.Mkdir：文档写的是"Rename、MkdirAll、WriteFile"，但
// trash 包刻意用 os.Mkdir（单层）来抢占操作目录名——目录已存在时必须报错，
// 才能发现撞名，用 MkdirAll 会把撞名吞掉。文档已同步补上这一项。
var allowedOSWrites = map[string]bool{
	"Rename":    true,
	"Mkdir":     true,
	"MkdirAll":  true,
	"WriteFile": true,
}

// violation 是一条违规记录。
type violation struct {
	File string
	Line int
	Call string
	Why  string
}

func (v violation) String() string {
	return fmt.Sprintf("%s:%d %s（%s）", v.File, v.Line, v.Call, v.Why)
}

// scanSource 解析一份 Go 源码，返回其中的违规项。
//
// 抽成独立函数是为了能单独测试扫描器本身（见 TestScannerDetectsAndIgnores），
// 否则"扫描通过"有可能只是因为它什么都没扫。
func scanSource(filename string, src []byte, isTest bool) ([]violation, error) {
	fset := token.NewFileSet()
	// ParseComments 不必要：我们只关心调用表达式，注释天然不在 AST 里
	file, err := parser.ParseFile(fset, filename, src, 0)
	if err != nil {
		return nil, err
	}

	// 把导入的本地名映射回包路径，处理 `o "os"` 这类别名
	local := map[string]string{}
	dotImports := map[string]bool{}
	for _, imp := range file.Imports {
		path := strings.Trim(imp.Path.Value, `"`)
		if !trackedImports[path] {
			continue
		}
		name := filepath.Base(path)
		if imp.Name != nil {
			if imp.Name.Name == "_" {
				continue
			}
			if imp.Name.Name == "." {
				dotImports[path] = true
				continue
			}
			name = imp.Name.Name
		}
		local[name] = path
	}

	var out []violation
	ast.Inspect(file, func(n ast.Node) bool {
		call, ok := n.(*ast.CallExpr)
		if !ok {
			return true
		}
		pos := fset.Position(call.Pos())

		switch fun := call.Fun.(type) {
		case *ast.SelectorExpr:
			ident, ok := fun.X.(*ast.Ident)
			if !ok {
				return true
			}
			path, tracked := local[ident.Name]
			if !tracked {
				return true
			}
			out = append(out, judge(path, fun.Sel.Name, filename, pos.Line, isTest)...)

		case *ast.Ident:
			// 点导入（import . "os"）时调用是裸标识符，保守起见一律检视
			if len(dotImports) == 0 {
				return true
			}
			for path := range dotImports {
				out = append(out, judge(path, fun.Name, filename, pos.Line, isTest)...)
			}
		}
		return true
	})
	return out, nil
}

// judge 判定一次调用是否违规。
func judge(pkgPath, name, filename string, line int, isTest bool) []violation {
	// A. 删除类调用：任何地方都不允许
	if forbiddenCall.MatchString(name) {
		return []violation{{
			File: filename, Line: line, Call: pkgPath + "." + name,
			Why: "删除类调用；本工具只移动不删除（设计文档 §10.1 C8）",
		}}
	}

	// B. 非测试源码里的 os 写入类调用必须在白名单内
	if !isTest && pkgPath == "os" && osWriteLike[name] && !allowedOSWrites[name] {
		return []violation{{
			File: filename, Line: line, Call: "os." + name,
			Why: "不在允许的写操作集合里；如需新增请先更新设计文档 §10.1 与本文件的 allowedOSWrites",
		}}
	}
	return nil
}

// moduleRoot 从本测试文件出发向上找 go.mod，返回模块根目录。
func moduleRoot(t *testing.T) string {
	t.Helper()
	_, thisFile, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("拿不到本测试文件路径")
	}
	dir := filepath.Dir(thisFile)
	for {
		if _, err := os.Stat(filepath.Join(dir, "go.mod")); err == nil {
			return dir
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			t.Fatalf("从 %s 向上没找到 go.mod", filepath.Dir(thisFile))
		}
		dir = parent
	}
}

// TestNoPermanentDeleteCallsInSource 是 §10.1（C8）的机械验证。
func TestNoPermanentDeleteCallsInSource(t *testing.T) {
	root := moduleRoot(t)

	var violations []violation
	var parsed int

	err := filepath.WalkDir(root, func(path string, d fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			switch d.Name() {
			case "vendor", "testdata", ".git":
				return fs.SkipDir
			}
			return nil
		}
		if !strings.HasSuffix(d.Name(), ".go") {
			return nil
		}

		src, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		rel, relErr := filepath.Rel(root, path)
		if relErr != nil {
			rel = path
		}
		rel = filepath.ToSlash(rel)

		v, err := scanSource(rel, src, strings.HasSuffix(d.Name(), "_test.go"))
		if err != nil {
			// 解析不了就不能声称"检查过了"
			t.Errorf("%s 解析失败，无法验证不变量：%v", rel, err)
			return nil
		}
		parsed++
		violations = append(violations, v...)
		return nil
	})
	if err != nil {
		t.Fatalf("遍历 %s 失败：%v", root, err)
	}

	// 扫描器如果没扫到任何文件，"通过"就毫无意义
	if parsed < 20 {
		t.Fatalf("只解析到 %d 个 .go 文件，明显不对，扫描可能没生效", parsed)
	}

	if len(violations) > 0 {
		sort.Slice(violations, func(i, j int) bool {
			if violations[i].File != violations[j].File {
				return violations[i].File < violations[j].File
			}
			return violations[i].Line < violations[j].Line
		})
		var b strings.Builder
		fmt.Fprintf(&b, "发现 %d 处违反「不变量」的调用（共解析 %d 个 .go 文件）：", len(violations), parsed)
		for _, v := range violations {
			b.WriteString("\n  - " + v.String())
		}
		b.WriteString("\n\n本工具只允许 os.Rename / os.Mkdir / os.MkdirAll / os.WriteFile 这几种写操作。")
		b.WriteString("\n如果需要突破这个限制，请先改设计文档 §10.1，再改本文件的 allowedOSWrites，而不是删掉这条测试。")
		t.Error(b.String())
	}
}

// TestScannerDetectsAndIgnores 验证扫描器本身有效：既真能抓到违规，
// 又不会被注释、字符串、别名导入之类的形式骗过去（或误报）。
//
// 没有这条，"不变量测试通过"可能只说明扫描器坏掉了。
func TestScannerDetectsAndIgnores(t *testing.T) {
	cases := []struct {
		name      string
		src       string
		isTest    bool
		wantCalls []string // 期望抓到的调用；为空表示期望无违规
	}{
		{
			name:      "直接调用 os.Remove 必须抓到",
			src:       "package p\n\nimport \"os\"\n\nfunc f(p string) error { return os.Remove(p) }\n",
			wantCalls: []string{"os.Remove"},
		},
		{
			name:      "os.RemoveAll 必须抓到",
			src:       "package p\n\nimport \"os\"\n\nfunc f(p string) error { return os.RemoveAll(p) }\n",
			wantCalls: []string{"os.RemoveAll"},
		},
		{
			name:      "别名导入 o \"os\" 也必须抓到",
			src:       "package p\n\nimport o \"os\"\n\nfunc f(p string) error { return o.Remove(p) }\n",
			wantCalls: []string{"os.Remove"},
		},
		{
			name:      "点导入后的裸调用也要抓到",
			src:       "package p\n\nimport . \"os\"\n\nfunc f(p string) error { return Remove(p) }\n",
			wantCalls: []string{"os.Remove"},
		},
		{
			name:      "x/sys/windows 的 DeleteFileW 必须抓到",
			src:       "package p\n\nimport \"golang.org/x/sys/windows\"\n\nfunc f(p string) error { return windows.DeleteFile(p) }\n",
			wantCalls: []string{"golang.org/x/sys/windows.DeleteFile"},
		},
		{
			name:      "syscall.Unlink 必须抓到",
			src:       "package p\n\nimport \"syscall\"\n\nfunc f(p string) error { return syscall.Unlink(p) }\n",
			wantCalls: []string{"syscall.Unlink"},
		},
		{
			name:      "注释里提到 os.Remove 不算违规",
			src:       "package p\n\nimport \"os\"\n\n// 千万不要在这里调用 os.Remove，本工具只移动不删除\nfunc f() { _ = os.Getenv(\"X\") }\n",
			wantCalls: nil,
		},
		{
			name:      "字符串里出现 os.Remove 不算违规",
			src:       "package p\n\nvar msg = \"不要用 os.RemoveAll\"\n",
			wantCalls: nil,
		},
		{
			name:      "未导入 os 时同名方法不算违规",
			src:       "package p\n\ntype t struct{}\n\nfunc (t) Remove() error { return nil }\n\nfunc f() { var x t; _ = x.Remove() }\n",
			wantCalls: nil,
		},
		{
			name:      "非测试源码里 os.Chmod 属于越权写操作",
			src:       "package p\n\nimport \"os\"\n\nfunc f(p string) error { return os.Chmod(p, 0o644) }\n",
			isTest:    false,
			wantCalls: []string{"os.Chmod"},
		},
		{
			name:      "非测试源码里的允许写操作不报",
			src:       "package p\n\nimport \"os\"\n\nfunc f(a, b string) error {\n\t_ = os.MkdirAll(a, 0o700)\n\t_ = os.Mkdir(b, 0o700)\n\t_ = os.WriteFile(b, nil, 0o600)\n\treturn os.Rename(a, b)\n}\n",
			wantCalls: nil,
		},
		{
			name:      "测试文件里造夹具用的 os.Symlink 不报（白名单只约束非测试源码）",
			src:       "package p\n\nimport \"os\"\n\nfunc f(a, b string) error { return os.Symlink(a, b) }\n",
			isTest:    true,
			wantCalls: nil,
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, err := scanSource("synthetic.go", []byte(tc.src), tc.isTest)
			if err != nil {
				t.Fatalf("解析失败：%v", err)
			}
			var gotCalls []string
			for _, v := range got {
				gotCalls = append(gotCalls, v.Call)
			}
			if len(gotCalls) != len(tc.wantCalls) {
				t.Fatalf("抓到 %v，期望 %v", gotCalls, tc.wantCalls)
			}
			for i := range gotCalls {
				if gotCalls[i] != tc.wantCalls[i] {
					t.Fatalf("抓到 %v，期望 %v", gotCalls, tc.wantCalls)
				}
			}
		})
	}
}
