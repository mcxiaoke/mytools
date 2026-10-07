package main

import (
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"github.com/spf13/pflag"

	"saferm/internal/config"
	"saferm/internal/guard"
	"saferm/internal/platform"
	"saferm/internal/trash"
	"saferm/internal/ui"
	"saferm/internal/volume"
)

// 退出码（设计文档 §8.3）。
const (
	exitOK        = 0
	exitUsage     = 1
	exitPartial   = 2
	exitCancelled = 3
)

// app 把命令行的外部依赖收进结构体，方便端到端测试直接驱动。
type app struct {
	stdin  io.Reader
	stdout io.Writer
	stderr io.Writer
	args   []string

	// 以下三个便于测试注入；留空则用真实环境。
	isTTY func() bool
	now   func() time.Time
	pid   int

	// loadConfig 加载配置；留空则读真实配置文件（config.Load）。
	// 测试注入它是为了让用例不受开发机上 %APPDATA%\saferm\config.toml 的影响。
	loadConfig func(explicitPath string) (config.Config, string, error)

	// configPath 是 --config 的值；为空表示用默认位置。
	configPath string
	// started 在 run() 开头固定下来：操作号、清单时间戳都用它，
	// 避免"预览显示的路径"与"实际创建的目录"因为跨秒而不同。
	started time.Time
}

func (a *app) isInteractive() bool {
	if a.isTTY != nil {
		return a.isTTY()
	}
	return ui.NewPrompter().Interactive()
}

func (a *app) processID() int {
	if a.pid != 0 {
		return a.pid
	}
	return os.Getpid()
}

// predictedOpID 是本次操作预计的操作号（与 trash.Begin 实际抢到的名字一致，
// 除非恰好撞名）。
func (a *app) predictedOpID() string {
	return trash.OpIDFor(a.started, a.processID())
}

func (a *app) run() int {
	a.started = time.Now()
	if a.now != nil {
		a.started = a.now()
	}

	if len(a.args) > 0 {
		switch a.args[0] {
		case "where":
			return a.runWhere(a.args[1:])
		case "help":
			printUsage(a.stdout)
			return exitOK
		case "version":
			fmt.Fprintf(a.stdout, "saferm %s\n", version)
			return exitOK
		}
	}
	return a.runRemove(a.args)
}

// removeFlags 是"移动"这一步的全部开关。
type removeFlags struct {
	dryRun         bool
	yes            bool
	force          bool
	interactive    bool
	verbose        bool
	yesIAmSure     bool
	allowDangerous bool
	literal        bool
	trashRoot      string
	help           bool
	showVersion    bool
}

func (a *app) newRemoveFlagSet() (*pflag.FlagSet, *removeFlags) {
	f := &removeFlags{}
	fs := pflag.NewFlagSet("saferm", pflag.ContinueOnError)
	fs.SortFlags = false
	fs.SetOutput(a.stderr)

	fs.BoolVarP(&f.dryRun, "dry-run", "n", false, "只显示将要移动到何处，不落盘")
	fs.BoolVarP(&f.yes, "yes", "y", false, "跳过确认级提示")
	fs.BoolVarP(&f.force, "force", "f", false, "路径不存在不报错；并跳过确认级提示")
	fs.BoolVarP(&f.interactive, "interactive", "i", false, "强制确认（覆盖 -y / -f）")
	fs.BoolVar(&f.yesIAmSure, "yes-i-am-sure", false, "跳过危险级确认（唯一开关，名字故意写长）")
	fs.BoolVar(&f.allowDangerous, "allow-dangerous", false, "绕过危险路径护栏")
	fs.BoolVarP(&f.verbose, "verbose", "v", false, "打印每个目标的详细处理过程")
	fs.BoolVar(&f.literal, "literal", false, "强制把参数当作字面路径，不展开通配符")
	fs.StringVar(&f.trashRoot, "trash-root", "", "覆盖回收目录（单次生效）")
	fs.StringVar(&a.configPath, "config", "", "指定配置文件")

	// rm 兼容参数（-r/-R 与 --preserve-root）集中在 rmcompat.go 里注册
	addRMCompatFlags(fs)

	fs.BoolVarP(&f.help, "help", "h", false, "显示帮助")
	fs.BoolVarP(&f.showVersion, "version", "V", false, "显示版本")

	fs.Usage = func() { printUsage(a.stdout) }
	return fs, f
}

func (a *app) runRemove(args []string) int {
	fs, f := a.newRemoveFlagSet()
	if err := fs.Parse(args); err != nil {
		// 把"哪个参数不对"讲清楚，再给用法；已知的 rm 参数补等价写法
		a.reportFlagError(err, args)
		fmt.Fprintln(a.stderr, "用法：saferm [选项] <路径>...（saferm --help 查看全部选项）")
		return exitUsage
	}
	if f.help {
		printUsage(a.stdout)
		return exitOK
	}
	if f.showVersion {
		fmt.Fprintf(a.stdout, "saferm %s\n", version)
		return exitOK
	}

	operands := fs.Args()
	if len(operands) == 0 {
		fmt.Fprintln(a.stderr, "saferm: 没有给出任何路径")
		printUsage(a.stderr)
		return exitUsage
	}

	// 配置加载放在所有文件系统操作之前（设计文档 §7.1）：
	// 文件存在但解析失败 / 键名写错 → 硬失败，绝不静默回退默认值。
	// 否则用户以为护栏按配置生效了，实际用的是另一套值，是最危险的失败模式。
	cfg, cfgPath, err := a.effectiveConfig()
	if err != nil {
		fmt.Fprintf(a.stderr, "saferm: %v\n", err)
		return exitUsage
	}
	if f.verbose && cfgPath != "" {
		fmt.Fprintf(a.stdout, "配置文件 %s\n", cfgPath)
	}

	// 字面优先的通配符展开：展开结果会被当作**新的目标**重新走一遍全部护栏
	expanded, err := expandOperands(operands, f.literal)
	if err != nil {
		fmt.Fprintf(a.stderr, "saferm: %v\n", err)
		return exitUsage
	}

	// 护栏（设计文档 §5）：任何文件系统写操作之前跑完
	rep, err := guard.Check(expanded, guardOptions(cfg, f))
	if err != nil {
		fmt.Fprintf(a.stderr, "saferm: %v\n", err)
		return exitUsage
	}
	a.printWarnings(rep.Warnings)
	if rep.Failed() {
		fmt.Fprintf(a.stderr, "saferm: %v\n", rep.Violations)
		return exitUsage
	}
	a.printSkipped(rep.Skipped, f.verbose)

	// 两个长开关同时出现：护栏与危险级确认都被关掉了，必须显式警告
	if f.allowDangerous && f.yesIAmSure {
		fmt.Fprintln(a.stderr, "警告：--allow-dangerous 与 --yes-i-am-sure 同时生效，")
		fmt.Fprintln(a.stderr, "      危险路径护栏与危险级确认均已关闭，本次操作可能不可逆。")
	}

	// 解析落点（§7.4 的全部校验在这里完成）
	paths := make([]string, 0, len(rep.Targets))
	for _, t := range rep.Targets {
		paths = append(paths, t.Path)
	}
	placements, viols, err := volume.Resolve(paths, volumeConfig(cfg, version, f.trashRoot))
	if err != nil {
		fmt.Fprintf(a.stderr, "saferm: %v\n", err)
		return exitUsage
	}
	if len(viols) > 0 {
		fmt.Fprintf(a.stderr, "saferm: %v\n", viols)
		return exitUsage
	}
	if len(placements) == 0 {
		// 目标全部被 -f 跳过：什么都不做，且明确说清原数据未动
		fmt.Fprintln(a.stderr, "saferm: 没有任何可移动的目标（原数据未改动）")
		return exitOK
	}

	confirm := confirmConfig(cfg)
	groups := a.groupPlacements(rep, placements, scanLimits(confirm), cfg.Guard.GitDetect)
	risks := movableRisks(groups)
	level := ui.Decide(risks, confirm)
	if f.interactive && level == ui.LevelNone {
		level = ui.LevelConfirm
	}

	if f.dryRun {
		a.printPreview(groups, level)
		return exitOK
	}

	prompter := &ui.Prompter{In: a.stdin, Out: a.stdout}
	if a.isTTY != nil {
		prompter.IsTTY = a.isTTY
	}

	// -i 强制确认：把 -y/-f 的预先授权撤销
	skipConfirm := (f.yes || f.force) && !f.interactive

	approved, err := prompter.Ask(
		ui.Request{Level: level, Risks: risks, Dest: a.destinations(groups)},
		ui.Approval{SkipConfirm: skipConfirm, YesIAmSure: f.yesIAmSure})
	if err != nil {
		fmt.Fprintf(a.stderr, "saferm: %v\n", err)
		return exitUsage
	}
	if !approved {
		fmt.Fprintln(a.stderr, "saferm: 已取消，原数据未改动。")
		return exitCancelled
	}

	return a.execute(groups, f.verbose)
}

// group 是一次调用里"落到同一个回收根"的一组目标。
type group struct {
	trashRoot  string
	volumeRoot string
	items      []groupItem
}

// groupItem 是组内的一个条目：要么可移动，要么只是登记（-f 下路径不存在）。
type groupItem struct {
	target     string
	input      string
	skipReason string
	stats      ui.ScanStats
	isDir      bool
	git        ui.GitInfo
}

// groupPlacements 按回收根把目标分组，并把 -f 跳过的条目挂到对应组里。
//
// limits 与 gitDetect 都来自生效配置：扫描上限来自危险级阈值，
// gitDetect 为 false 时不做版本库检测（也就不会因为"在仓库里"升为危险级）。
func (a *app) groupPlacements(rep guard.Report, placements []volume.Placement, limits ui.ScanLimits, gitDetect bool) []group {
	byTrashRoot := map[string]*group{}
	var order []string

	dirOf := map[string]bool{}
	byPath := map[string]guard.Target{}
	for _, t := range rep.Targets {
		byPath[t.Path] = t
		dirOf[t.Path] = t.Info != nil && t.Info.IsDir()
	}

	for _, p := range placements {
		g, ok := byTrashRoot[p.TrashRoot]
		if !ok {
			g = &group{trashRoot: p.TrashRoot, volumeRoot: p.VolumeRoot}
			byTrashRoot[p.TrashRoot] = g
			order = append(order, p.TrashRoot)
		}
		t := byPath[p.Target]
		var git ui.GitInfo
		if gitDetect {
			git = ui.DetectGit(p.Target)
		}
		// 每个目标只扫一次：扫描可能是这次调用里最贵的操作
		stats := ui.Scan(p.Target, limits)
		g.items = append(g.items, groupItem{
			target: p.Target,
			input:  t.Input,
			stats:  stats,
			isDir:  dirOf[p.Target],
			git:    git,
		})
	}

	// -f 跳过的条目：只登记、不移动，挂到同卷的组里（没有对应组就只在输出里提示）
	for _, s := range rep.Skipped {
		volRoot, err := platform.VolumeRoot(s.Path)
		if err != nil {
			continue
		}
		for _, g := range byTrashRoot {
			if g.volumeRoot == volRoot {
				g.items = append(g.items, groupItem{
					target: s.Path, input: s.Input, skipReason: s.Reason,
				})
				break
			}
		}
	}

	sort.Strings(order)
	out := make([]group, 0, len(order))
	for _, key := range order {
		out = append(out, *byTrashRoot[key])
	}
	return out
}

// movableRisks 给出"会被真的移动"的目标画像，顺序与 destinations 一致。
func movableRisks(groups []group) []ui.Risk {
	var risks []ui.Risk
	for _, g := range groups {
		for _, it := range g.items {
			if it.skipReason != "" {
				continue
			}
			risks = append(risks, ui.Risk{
				Target: it.target, IsDir: it.isDir, Stats: it.stats, Git: it.git,
			})
		}
	}
	return risks
}

// destinations 给出预计落点，顺序与 movableRisks 一致。
//
// 落点必须与真正执行时的 trash.MirrorPath 同一套规则：预览与实际不一致
// 会破坏"确认的就是发生的"这一前提。
func (a *app) destinations(groups []group) []string {
	opID := a.predictedOpID()
	var out []string
	for _, g := range groups {
		for _, it := range g.items {
			if it.skipReason != "" {
				continue
			}
			out = append(out, previewDest(g, opID, it.target))
		}
	}
	return out
}

// previewDest 计算预计落点；算不出来时退回目标本身并让它可见，
// 而不是悄悄显示一个错的位置。
func previewDest(g group, opID, target string) string {
	dest, err := trash.MirrorPath(filepath.Join(g.trashRoot, opID), g.volumeRoot, target)
	if err != nil {
		return target
	}
	return dest
}

// execute 真正开始搬：按组创建回收根、写清单、逐项移动、结算。
func (a *app) execute(groups []group, verbose bool) int {
	host, _ := os.Hostname()
	user := os.Getenv("USERNAME")
	if user == "" {
		user = os.Getenv("USER")
	}
	cwd, _ := os.Getwd()

	totalDone, totalSkipped, totalFailed := 0, 0, 0
	var failures []string
	var opLines []string

	for _, g := range groups {
		warnings, err := volume.EnsureRoot(g.trashRoot, version)
		if err != nil {
			fmt.Fprintf(a.stderr, "saferm: %v\n", err)
			return a.setupFailed(totalDone, totalSkipped, totalFailed, failures, opLines)
		}
		for _, w := range warnings {
			fmt.Fprintf(a.stderr, "saferm: 提示：%s\n", w)
		}

		sources := make([]trash.Source, 0, len(g.items))
		movable := make([]int, 0, len(g.items))
		for i, it := range g.items {
			sources = append(sources, trash.Source{
				Input: it.input, Path: it.target, SkipReason: it.skipReason,
			})
			if it.skipReason == "" {
				movable = append(movable, i)
			}
		}

		sess, err := trash.Begin(g.trashRoot, g.volumeRoot, sources, trash.Options{
			Version: version, Host: host, User: user, Cwd: cwd,
			CommandLine: strings.Join(a.args, " "),
			Now:         func() time.Time { return a.started },
			Pid:         a.processID(),
		})
		if err != nil {
			fmt.Fprintf(a.stderr, "saferm: %v\n", err)
			return a.setupFailed(totalDone, totalSkipped, totalFailed, failures, opLines)
		}

		items := sess.Items()
		for _, idx := range movable {
			it := g.items[idx]
			stats := trash.Stats{Files: it.stats.Files + it.stats.Dirs, Bytes: it.stats.Bytes}
			if err := sess.Move(idx, stats); err != nil {
				failures = append(failures, fmt.Sprintf("%s：%v", it.input, err))
				continue
			}
			if verbose {
				fmt.Fprintf(a.stdout, "已移动 %s\n     → %s\n", it.target, items[idx].Destination)
			}
		}

		sum := sess.Finish()
		totalDone += sum.Done
		totalSkipped += sum.Skipped
		totalFailed += sum.Failed

		info := sess.Info()
		opLines = append(opLines,
			fmt.Sprintf("  操作号    %s", info.ID),
			fmt.Sprintf("  回收目录  %s", g.trashRoot),
			fmt.Sprintf("  清单      %s", info.Manifest))
	}

	a.printResult(totalDone, totalSkipped, totalFailed, failures, opLines)
	if totalFailed > 0 {
		return exitPartial
	}
	return exitOK
}

// setupFailed 是"预检通过但动手阶段失败"的统一出口（C7：失败意味着没删）。
//
// 如果前面的组已经移动成功，这已经不是"未发生任何移动"的用法错误（退出码 1
// 会撒谎）：必须按部分失败结算（退出码 2），并把已完成组的操作号与清单路径
// 如实打印出来，否则脚本与人都无法对账哪些东西进了回收目录。
func (a *app) setupFailed(done, skipped, failed int, failures, opLines []string) int {
	if len(opLines) == 0 {
		return exitUsage
	}
	a.printResult(done, skipped, failed, failures, opLines)
	return exitPartial
}

func (a *app) printWarnings(warnings []string) {
	for _, w := range warnings {
		fmt.Fprintf(a.stderr, "saferm: 警告：%s\n", w)
	}
}

func (a *app) printSkipped(skipped []guard.Skip, verbose bool) {
	for _, s := range skipped {
		if verbose {
			fmt.Fprintf(a.stdout, "跳过（%s）：%s\n", s.Reason, s.Input)
		}
	}
}

func (a *app) printPreview(groups []group, level ui.Level) {
	fmt.Fprintln(a.stdout, "dry-run：不会创建任何目录、不会移动任何文件")
	fmt.Fprintf(a.stdout, "确认级别：%s\n", level)
	opID := a.predictedOpID()
	for _, g := range groups {
		for _, it := range g.items {
			if it.skipReason != "" {
				fmt.Fprintf(a.stdout, "\n  跳过    %s（%s）\n", it.target, it.skipReason)
				continue
			}
			fmt.Fprintf(a.stdout, "\n  目标    %s\n", it.target)
			kind := "文件"
			if it.isDir {
				kind = "目录"
			}
			fmt.Fprintf(a.stdout, "  类型    %s\n", kind)
			if it.stats.Files+it.stats.Dirs > 0 {
				fmt.Fprintf(a.stdout, "  内容    %s 个条目 / %s\n",
					ui.HumanCount(it.stats.Files+it.stats.Dirs),
					ui.HumanBytes(it.stats.Bytes, it.stats.Truncated))
			}
			fmt.Fprintf(a.stdout, "  目的    %s\n", previewDest(g, opID, it.target))
			if it.git.InRepo {
				fmt.Fprintf(a.stdout, "  仓库    %s（在版本库工作区内）\n", it.git.RepoRoot)
			}
		}
	}
	fmt.Fprintln(a.stdout, "\n原数据未被改动。去掉 -n 即为真正执行。")
}

func (a *app) printResult(done, skipped, failed int, failures []string, opLines []string) {
	fmt.Fprintf(a.stdout, "\n已处理：成功 %d / 跳过 %d / 失败 %d\n", done, skipped, failed)
	for _, line := range opLines {
		fmt.Fprintln(a.stdout, line)
	}
	if len(failures) > 0 {
		fmt.Fprintln(a.stderr, "\n以下目标未能移动（原数据仍在原处，未被删除）：")
		for _, f := range failures {
			fmt.Fprintf(a.stderr, "  - %s\n", f)
		}
	}
	fmt.Fprintln(a.stdout, "\n原数据没有被永久删除：目标只是被移动到了回收目录。")
	fmt.Fprintln(a.stdout, "手工恢复：把对应操作目录里的内容整体搬回该卷的根目录即可（目录结构已原样保留）。")
}

// expandOperands 做"字面路径优先"的通配符展开。
//
// 规则（设计文档 §5.3）：
//   - 参数作为字面路径存在 → 按字面处理（`$null`、`[abc]`、`a*b` 这类怪名字文件因此能直接删）
//   - 字面不存在且含通配符 → 由工具展开
//   - **展开为空必须报错**，绝不"空模式匹配成当前目录"——那正是本次事故的形态
//
// 展开结果是"新的目标"，会与字面参数一起重新走一遍全部护栏。
func expandOperands(operands []string, literal bool) ([]string, error) {
	var out []string
	for _, op := range operands {
		if strings.TrimSpace(op) == "" || literal {
			out = append(out, op)
			continue
		}
		if _, err := os.Lstat(op); err == nil {
			out = append(out, op)
			continue
		}
		if !hasGlobMeta(op) {
			out = append(out, op)
			continue
		}
		matches, err := filepath.Glob(op)
		if err != nil {
			return nil, fmt.Errorf("通配符 %q 无效：%w", op, err)
		}
		if len(matches) == 0 {
			return nil, fmt.Errorf(
				"通配符 %q 没有匹配到任何路径。展开为空时本工具拒绝继续（避免把空模式当成当前目录）", op)
		}
		sort.Strings(matches)
		out = append(out, matches...)
	}
	return out, nil
}

func hasGlobMeta(p string) bool { return strings.ContainsAny(p, "*?[") }
