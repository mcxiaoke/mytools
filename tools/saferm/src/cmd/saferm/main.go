// Command saferm 是"移动而非删除"的安全 rm：目标被移动到同卷的回收目录，
// 而不是永久删除，也不进系统回收站。
//
// 本程序的设计不变量：代码里不存在任何永久删除调用（见 docs/saferm-design.md §10.1）。
package main

import (
	"fmt"
	"os"

	"github.com/spf13/pflag"

	"saferm/internal/trash"
	"saferm/internal/volume"
)

// version 由构建脚本通过 -ldflags 注入；本地开发时保持 dev。
var version = "dev"

// exitCode 与设计文档 §8.3 一致。
const (
	exitOK        = 0
	exitUsage     = 1
	exitPartial   = 2
	exitCancelled = 3
)

func main() {
	os.Exit(run(os.Args[1:]))
}

func run(args []string) int {
	if len(args) > 0 && args[0] == "where" {
		return runWhere(args[1:])
	}
	return runRemove(args)
}

// runWhere 实现 `saferm where`：只读自检，打印生效配置与各卷的回收目录。
func runWhere(args []string) int {
	fs := pflag.NewFlagSet("saferm where", pflag.ContinueOnError)
	fs.SortFlags = false
	showHelp := fs.BoolP("help", "h", false, "显示帮助")

	if err := fs.Parse(args); err != nil {
		return exitUsage
	}
	if *showHelp {
		printWhereHelp()
		return exitOK
	}

	probes, err := volume.ProbeVolumes(volume.Config{Version: version})
	if err != nil {
		fmt.Fprintf(os.Stderr, "saferm: 探测卷信息失败：%v\n", err)
		return exitUsage
	}

	summary := volume.ProbeSummary{Version: version, Probes: probes, Notes: volume.DefaultNotes()}

	// 顺带核对有没有"没有正常收尾"的操作：进程被杀或断电之后，
	// 用户需要知道上一次到底搬走了什么（设计文档 §8.1）。
	for _, p := range probes {
		if !p.Exists || !p.HasMarker {
			continue
		}
		ops, bad, err := trash.Unfinished(p.TrashRoot)
		if err != nil {
			fmt.Fprintf(os.Stderr, "saferm: 读取 %s 的清单失败：%v\n", p.TrashRoot, err)
			continue
		}
		summary.BadManifests += bad
		for _, op := range ops {
			summary.Unfinished = append(summary.Unfinished, volume.UnfinishedOp{
				OpID:      op.OpID,
				State:     op.State,
				TrashRoot: p.TrashRoot,
				StartedAt: op.StartedAt,
				Items:     op.Items,
				Done:      op.Done,
				Failed:    op.Failed,
			})
		}
	}

	if err := summary.Render(os.Stdout); err != nil {
		fmt.Fprintf(os.Stderr, "saferm: 输出失败：%v\n", err)
		return exitUsage
	}

	// 有卷不可用、或存在未正常收尾的操作，就返回非零，便于脚本判断（设计文档 §8.1）
	if len(summary.Unfinished) > 0 || summary.BadManifests > 0 {
		return exitUsage
	}
	for _, p := range probes {
		if !p.Usable {
			return exitUsage
		}
	}
	return exitOK
}

// runRemove 是默认动作：把路径移动到回收目录。
// 移动能力尚未接入（设计文档 §12 第 3~5 步），这里先明确拒绝而不是假装成功。
func runRemove(args []string) int {
	fs := pflag.NewFlagSet("saferm", pflag.ContinueOnError)
	fs.SortFlags = false
	showVersion := fs.BoolP("version", "V", false, "显示版本")
	showHelp := fs.BoolP("help", "h", false, "显示帮助")

	if err := fs.Parse(args); err != nil {
		return exitUsage
	}
	if *showVersion {
		fmt.Printf("saferm %s\n", version)
		return exitOK
	}
	if *showHelp || len(fs.Args()) == 0 {
		printUsage()
		if *showHelp {
			return exitOK
		}
		return exitUsage
	}

	fmt.Fprintln(os.Stderr, "saferm: 移动功能尚未接入（当前仅完成护栏与回收目录解析）。")
	fmt.Fprintln(os.Stderr, "        已实现的检查没有任何副作用，原数据未被改动。")
	fmt.Fprintln(os.Stderr, "        可用 `saferm where` 查看各卷的回收目录解析结果。")
	return exitUsage
}

func printUsage() {
	fmt.Print(`saferm —— 移动而非删除的安全 rm

用法：
  saferm [选项] <路径>...     把路径移动到同卷的回收目录
  saferm where                查看生效配置与各卷的回收目录（只读，不创建任何东西）

选项：
  -n, --dry-run               只显示将要移动到何处，不落盘
  -y, --yes                   跳过「确认级」提示（护栏与危险级照旧生效）
  -f, --force                 不存在的路径不报错；并跳过「确认级」提示
  -r, -R                      接受但忽略（目录递归本来就是默认行为）
  -v, --verbose               打印每个目标的详细处理过程
      --yes-i-am-sure         跳过「危险级」确认（唯一开关，名字故意写长）
      --allow-dangerous       绕过危险路径护栏（需显式打出）
      --config <文件>         指定配置文件
      --trash-root <目录>     覆盖回收目录（单次生效）
  -h, --help                  显示帮助
  -V, --version               显示版本

说明：本工具默认拒绝跨卷、拒绝卷根、拒绝当前目录及其上级、拒绝版本库根目录，
      也不会永久删除任何东西。详见 docs/saferm-design.md。
`)
}

func printWhereHelp() {
	fmt.Print(`saferm where —— 查看生效配置与各卷的回收目录

用法：
  saferm where [选项]

只读操作：不创建目录、不写标记、不移动任何文件。
某个卷不可用时返回退出码 1，便于脚本判断。
`)
}
