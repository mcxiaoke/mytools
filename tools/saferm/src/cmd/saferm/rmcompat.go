package main

import (
	"fmt"
	"strings"

	"github.com/spf13/pflag"
)

// 本文件集中处理"从 rm 迁移过来"这件事：哪些 rm 参数被接受并忽略、
// 哪些被拒绝、拒绝时怎么告诉用户等价写法。
//
// 设计文档 §8.2 的原则是：**rm 兼容参数宁可少而明确，也不能静默改变语义**。
// 因此这里只收纳两类：
//   - 对 saferm 而言"恒为真"的安全参数（如 --preserve-root）；
//   - 本来就与 saferm 默认行为一致的参数（如 -r/-R，目录递归是默认行为）。
//
// 像 -d（只删空目录）、--no-preserve-root（关闭卷根保护）这类会**改变语义**的参数
// 一律不静默接受：前者会让用户以为"非空目录不会被删"，后者恰恰是本工具要防的事。

// ignoreFlagValue 是"接受任何取值、但什么都不做"的 flag 值。
//
// 用自定义 Value 而不是 BoolVar，是为了同时接纳 GNU 的两种写法：
// `--preserve-root` 与 `--preserve-root=all`（coreutils 9.x 的文档里是
// `--preserve-root[=all]`）。若用 BoolVar，`=all` 会因无法解析成布尔而报错，
// 给用户一个莫名其妙的 "invalid argument"。
type ignoreFlagValue struct{}

func (ignoreFlagValue) String() string   { return "" }
func (ignoreFlagValue) Type() string     { return "bool" }
func (ignoreFlagValue) Set(string) error { return nil }

// addRMCompatFlags 注册"接受但忽略"的 rm 参数。
//
//   - -r / -R：目录递归本来就是 saferm 的默认行为。
//   - --preserve-root：saferm 的卷根保护**恒为开启**，无法关闭，所以这个参数
//     对它来说恒为真，接受它没有任何安全成本，却能救掉一批现存脚本。
//     顺带说明 `=all` 的含义（拒绝删除与父目录不同设备的参数）：那正是
//     saferm 已有的行为——挂载点会被当作卷根拒绝，而且回收根必须与目标同卷。
func addRMCompatFlags(fs *pflag.FlagSet) {
	var ignoreRecurse, ignoreRecurseUpper bool
	fs.BoolVarP(&ignoreRecurse, "recursive", "r", false, "接受但忽略（目录递归是默认行为）")
	fs.BoolVarP(&ignoreRecurseUpper, "recursive-all", "R", false, "接受但忽略（同上）")

	fs.Var(ignoreFlagValue{}, "preserve-root", "接受但忽略（卷根保护恒为开启，无法关闭）")
	// 允许不带值：`--preserve-root` 等价于 `--preserve-root=true`
	fs.Lookup("preserve-root").NoOptDefVal = "true"
}

// rmOnlyFlags 是"rm 有、saferm 故意没有"的参数 → 等价写法或拒绝理由。
//
// 键的写法：短选项用 "-d"，长选项用 "--dir"（不含取值部分）。
var rmOnlyFlags = map[string]string{
	"-d":    "saferm 没有 -d（rm 用它只删空目录）：直接给目录路径即可，目录默认走确认级，加 -y 可跳过",
	"--dir": "saferm 没有 --dir：直接给目录路径即可，目录默认走确认级，加 -y 可跳过",

	"-I":            "saferm 没有 -I：跳过确认级用 -y，强制确认用 -i",
	"--interactive": "saferm 的确认开关是 -i（强制确认）与 -y（跳过确认级）",

	"--one-file-system": "saferm 不需要 --one-file-system：回收目录必须与目标同卷，本工具不做跨文件系统的移动",

	"--no-preserve-root": "saferm 拒绝关闭卷根保护（这正是本工具存在的意义之一）；需要释放空间请手工处置回收目录",
}

// rmCompatHint 在参数解析失败后做一次尽力而为的扫描，
// 找出用户可能想用的是哪个 rm 参数，并给出等价写法。
//
// 只在解析已经失败时调用，所以这里不需要严格的"这是不是参数"判断——
// 但 `--` 之后一律视为路径，不再扫描。
func rmCompatHint(args []string) string {
	for _, a := range args {
		if a == "--" {
			return ""
		}
		if strings.HasPrefix(a, "--") {
			name := strings.TrimPrefix(a, "--")
			if i := strings.IndexByte(name, '='); i >= 0 {
				name = name[:i]
			}
			if h, ok := rmOnlyFlags["--"+name]; ok {
				return h
			}
			continue
		}
		if strings.HasPrefix(a, "-") && len(a) > 1 {
			// 短选项可能被合并（如 -rf），逐个字符查
			for _, r := range a[1:] {
				if h, ok := rmOnlyFlags["-"+string(r)]; ok {
					return h
				}
			}
		}
	}
	return ""
}

// reportFlagError 汇报参数解析失败。
//
// 必须把 pflag 的原始错误打出来：它才是"到底是哪个参数不对"的唯一来源。
// （pflag 在 ContinueOnError 模式下**不会**自己打印，之前这里只打了用法提示，
// 用户完全不知道哪个参数错了。）已知的 rm 参数再补一句等价写法。
func (a *app) reportFlagError(err error, args []string) {
	fmt.Fprintf(a.stderr, "saferm: %v\n", err)
	if hint := rmCompatHint(args); hint != "" {
		fmt.Fprintf(a.stderr, "saferm: %s\n", hint)
	}
}
