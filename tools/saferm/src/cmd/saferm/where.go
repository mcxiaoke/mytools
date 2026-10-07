package main

import (
	"fmt"
	"io"

	goi18n "github.com/nicksnyder/go-i18n/v2/i18n"
	"github.com/spf13/pflag"

	i18n "saferm/internal/i18n"
	"saferm/internal/trash"
	"saferm/internal/volume"
)

// runWhere 实现 `saferm where`：只读自检，打印生效配置与各卷的回收目录。
func (a *app) runWhere(args []string) int {
	fs := pflag.NewFlagSet("saferm where", pflag.ContinueOnError)
	fs.SortFlags = false
	fs.SetOutput(a.stderr)
	showHelp := fs.BoolP("help", "h", false, i18n.T(&goi18n.Message{ID: "FlagHelp", Other: "显示帮助"}))
	fs.StringVar(&a.configPath, "config", "", i18n.T(&goi18n.Message{ID: "FlagConfig", Other: "指定配置文件"}))
	// 与主命令保持同一套 rm 兼容面：--preserve-root / -r / -R 在这里也接受并忽略
	addRMCompatFlags(fs)

	if err := fs.Parse(args); err != nil {
		// 修掉一个静默失败：pflag 在 ContinueOnError 下不自己打印错误，
		// 之前这里直接 return，用户只看到退出码 1、一句话都没有。
		a.reportFlagError(err, args)
		fmt.Fprintln(a.stderr, i18n.T(&goi18n.Message{ID: "UsageWhere", Other: "用法：saferm where [选项]（saferm where --help 查看选项）"}))
		return exitUsage
	}
	if *showHelp {
		printWhereHelp(a.stdout)
		return exitOK
	}

	// where 也必须走同一套配置加载：它报告的必须是**实际生效**的回收根，
	// 而不是"默认值下的回收根"，否则这份自检会误导人（设计文档 §8.1）。
	cfg, cfgPath, err := a.effectiveConfig()
	if err != nil {
		fmt.Fprintf(a.stderr, "saferm: %v\n", err)
		return exitUsage
	}

	probes, err := volume.ProbeVolumes(volumeConfig(cfg, version, ""))
	if err != nil {
		fmt.Fprintln(a.stderr, i18n.T(&goi18n.Message{ID: "ErrProbeVolumes", Other: "saferm: 探测卷信息失败：{{.Err}}"}, i18n.Data{"Err": err.Error()}))
		return exitUsage
	}

	summary := volume.ProbeSummary{
		Version:     version,
		ConfigPath:  cfgPath,
		ConfigLines: effectiveConfigLines(cfg),
		Probes:      probes,
		Notes:       volume.DefaultNotes(),
	}

	// 顺带核对有没有"没有正常收尾"的操作：进程被杀或断电之后，
	// 用户需要知道上一次到底搬走了什么（设计文档 §8.1）。
	for _, p := range probes {
		if !p.Exists || !p.HasMarker {
			continue
		}
		ops, bad, err := trash.Unfinished(p.TrashRoot)
		if err != nil {
			fmt.Fprintln(a.stderr, i18n.T(&goi18n.Message{ID: "ErrReadManifest", Other: "saferm: 读取 {{.Root}} 的清单失败：{{.Err}}"},
				i18n.Data{"Root": p.TrashRoot, "Err": err.Error()}))
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

	if err := summary.Render(a.stdout); err != nil {
		fmt.Fprintln(a.stderr, i18n.T(&goi18n.Message{ID: "ErrRender", Other: "saferm: 输出失败：{{.Err}}"}, i18n.Data{"Err": err.Error()}))
		return exitUsage
	}

	// 有卷不可用、或存在未正常收尾的操作，就返回非零，便于脚本判断（§8.1）
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

func printUsage(w io.Writer) {
	fmt.Fprint(w, i18n.T(&goi18n.Message{ID: "HelpMain", Other: `saferm —— 移动而非删除的安全 rm

用法：
  saferm [选项] <路径>...     把路径移动到同卷的回收目录
  saferm where                查看生效配置与各卷的回收目录（只读，不创建任何东西）
  saferm version              显示版本

选项：
  -n, --dry-run               只显示将要移动到何处，不落盘、不创建任何目录
  -y, --yes                   跳过「确认级」提示（护栏与危险级照旧生效）
  -f, --force                 不存在的路径不报错；并跳过「确认级」提示
  -i, --interactive           强制确认（覆盖 -y / -f）
      --yes-i-am-sure         跳过「危险级」确认（唯一开关，名字故意写长）
      --allow-dangerous       绕过危险路径护栏（需显式打出）
      --literal               把参数当字面路径，不展开通配符
      --config <文件>         指定配置文件
      --trash-root <目录>     覆盖回收目录（单次生效）
  -r, -R                      接受但忽略（目录递归本来就是默认行为）
      --preserve-root         接受但忽略（卷根保护恒为开启，无法关闭）
  -v, --verbose               打印每个目标的详细处理过程
  -h, --help                  显示帮助
  -V, --version               显示版本

安全边界（默认行为，-f 也拆不掉）：
  · 拒绝删除：卷根、当前目录及其上级、系统关键目录、版本库根目录、回收目录自身
  · 拒绝跨卷：回收目录必须与目标同卷，本工具不做「复制+删除」的降级
  · 不跟随符号链接与 junction：移动的是链接本身
  · 大目录 / 目标本身是版本库根 → 危险级，必须手敲目标名称确认（-y 无效）
  · 非交互环境（管道、CI）默认拒绝，需显式加 --yes 或 --yes-i-am-sure
  · 任何失败都意味着「原数据未改动」，绝不降级成永久删除

手工恢复：把回收目录里对应操作目录的内容整体搬回该卷的根目录即可，
          目录结构已按原样保留。详见 docs/saferm-design.md。
`}))
}

func printWhereHelp(w io.Writer) {
	fmt.Fprint(w, i18n.T(&goi18n.Message{ID: "HelpWhere", Other: `saferm where —— 查看生效配置与各卷的回收目录

用法：
  saferm where [选项]

选项：
      --config <文件>         指定配置文件
  -h, --help                  显示帮助

只读操作：不创建目录、不写标记、不移动任何文件。
某个卷不可用、或存在未正常收尾的操作时，返回退出码 1，便于脚本判断。
`}))
}
