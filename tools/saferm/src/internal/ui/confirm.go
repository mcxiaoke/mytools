package ui

import (
	"bufio"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"

	goi18n "github.com/nicksnyder/go-i18n/v2/i18n"
	"golang.org/x/term"

	"saferm/internal/i18n"
)

// Level 是确认级别（设计文档 §6.1）。
type Level int

const (
	// LevelNone：无需确认，直接移动。
	LevelNone Level = iota
	// LevelConfirm：输入 yes 放行，-y / -f 可跳过。
	LevelConfirm
	// LevelDanger：必须手敲目标名称；-y / --yes 都无效，只有 --yes-i-am-sure 能跳过。
	LevelDanger
)

func (l Level) String() string {
	switch l {
	case LevelConfirm:
		return i18n.T(&goi18n.Message{ID: "LevelConfirmName", Other: "确认级"})
	case LevelDanger:
		return i18n.T(&goi18n.Message{ID: "LevelDangerName", Other: "危险级"})
	default:
		return i18n.T(&goi18n.Message{ID: "LevelNoneName", Other: "无需确认"})
	}
}

// Risk 是一个待移动目标的画像，用来决定确认级别与展示摘要。
type Risk struct {
	Target string
	IsDir  bool
	Stats  ScanStats
	Git    GitInfo
}

// ConfirmConfig 是确认相关的阈值，对应配置文件的 [confirm] 段。
type ConfirmConfig struct {
	FileThreshold       int64
	DangerFileThreshold int64
	BytesThreshold      int64
	AlwaysConfirmDir    bool
}

// DefaultConfirmConfig 返回设计文档 §7.2 里的默认值。
func DefaultConfirmConfig() ConfirmConfig {
	return ConfirmConfig{
		FileThreshold:       50,
		DangerFileThreshold: 5000,
		BytesThreshold:      1 << 30, // 1 GiB
		AlwaysConfirmDir:    true,
	}
}

// Decide 决定整批需要哪一级确认。
//
// 危险级只由两类信号触发：
//  1. **体量**：条目数超过 danger_file_threshold；
//  2. **目标自身就是版本库根**（直接含 .git/.hg/.svn）。
//
// 刻意**不把"位于版本库工作区内"当危险级条件**。它曾经是触发条件，但那是**常开**的：
// 开发者几乎所有文件都在某个仓库里，于是 `saferm -y` 在任何真实项目里都失效，
// 结局就是无脑加 `--yes-i-am-sure`，危险级彻底形同虚设 —— 与 §14 D10 否掉
// "目标在 cwd 之外"是同一条理由。常开的信号不能用来分级。
//
// "在仓库内"仍然会被检测并**展示**仓库根路径，因为"这里可能有还没推到远端的东西"
// 是有用的信息；只是它不该决定级别。真想按"未提交/未推送"分级，得调 `git status`，
// 那是 §14 D7 明确推迟到 v2 的增强档。
//
// 目标自身是仓库根之所以仍算危险信号：护栏第 8 条本来就拒绝它（除非被
// --allow-dangerous 绕过、或 protect_vcs_root=false），这里算第二道防线。

func Decide(risks []Risk, cfg ConfirmConfig) Level {
	if len(risks) == 0 {
		return LevelNone
	}

	var files, dirs, bytes int64
	var incomplete, isRepoRoot, anyDir bool
	for _, r := range risks {
		files += r.Stats.Files
		dirs += r.Stats.Dirs
		bytes += r.Stats.Bytes
		incomplete = incomplete || r.Stats.Incomplete
		isRepoRoot = isRepoRoot || r.Git.IsRepoRoot
		anyDir = anyDir || r.IsDir
	}

	// 危险级：体量很大，或目标本身就是版本库根
	if cfg.DangerFileThreshold > 0 && files+dirs > cfg.DangerFileThreshold {
		return LevelDanger
	}
	if isRepoRoot {
		return LevelDanger
	}

	// 确认级
	if cfg.FileThreshold > 0 && files+dirs > cfg.FileThreshold {
		return LevelConfirm
	}
	if cfg.BytesThreshold > 0 && bytes > cfg.BytesThreshold {
		return LevelConfirm
	}
	if anyDir && cfg.AlwaysConfirmDir {
		return LevelConfirm
	}
	if incomplete {
		// 数不出来就按最坏情况处理：绝不因为"统计不完整"而静默放行
		return LevelConfirm
	}
	return LevelNone
}

// ErrNonInteractive 表示需要人工确认，但当前环境给不了人。
//
// 文案在渲染时才本地化（i18n.LocalizedError），因此实例可以在包级提前创建，
// errors.Is 仍按同一实例判定。
var ErrNonInteractive = i18n.LocalizedError(&goi18n.Message{
	ID: "ErrNonInteractive", Other: "需要人工确认，但当前不是交互式终端",
})

// Approval 是命令行开关表达的"预先授权"。
type Approval struct {
	// SkipConfirm 对应 -y / -f：跳过确认级。
	SkipConfirm bool
	// YesIAmSure 对应 --yes-i-am-sure：跳过危险级。名字故意写长。
	YesIAmSure bool
}

// Request 是一次确认请求。
type Request struct {
	Risks []Risk
	// Dest 与 Risks 一一对应，是移动后的落点（用于让用户看到"会放到哪"）。
	Dest []string
	// Level 由 Decide 得出。
	Level Level
}

// Prompter 负责把请求讲清楚并读回一个决定。
type Prompter struct {
	In  io.Reader
	Out io.Writer
	// IsTTY 覆盖终端判定，便于测试交互路径；留空则按 In 是否为终端判断。
	IsTTY func() bool
}

// NewPrompter 构造连到标准输入输出的 Prompter。
func NewPrompter() *Prompter {
	return &Prompter{In: os.Stdin, Out: os.Stdout}
}

// Interactive 判断输入是不是交互式终端。
//
// 非交互（管道、CI、计划任务、被别的脚本调用）时必须显式授权，
// 否则直接拒绝：这条封死了"脚本里变量为空就静默执行"的成因。
func (p *Prompter) Interactive() bool {
	if p.IsTTY != nil {
		return p.IsTTY()
	}
	f, ok := p.In.(*os.File)
	if !ok {
		return false
	}
	return term.IsTerminal(int(f.Fd()))
}

// Ask 展示摘要并取得授权。
//
// 返回值 (false, nil) 表示用户取消（调用方应给退出码 3）；
// (false, ErrNonInteractive) 表示环境给不了人，需要显式开关。
func (p *Prompter) Ask(req Request, ap Approval) (bool, error) {
	if req.Level == LevelNone {
		return true, nil
	}

	// 预授权（-y/-f 对确认级、--yes-i-am-sure 对危险级）的语义是"跳过确认提示"，
	// 与运行环境无关：命中即直接放行，不打印摘要也不再追问。
	// 此前这段只在非交互分支生效，交互终端下 -y 仍会追问，与设计文档 §8.2 矛盾。
	if req.Level == LevelConfirm && ap.SkipConfirm {
		return true, nil
	}
	if req.Level == LevelDanger && ap.YesIAmSure {
		return true, nil
	}

	if !p.Interactive() {
		return false, fmt.Errorf("%w%s", ErrNonInteractive,
			i18n.T(&goi18n.Message{ID: "ErrNonInteractiveSuffix", Other: "。{{.Hint}}"}, i18n.Data{"Hint": p.hint(req.Level)}))
	}

	if err := p.printSummary(req); err != nil {
		return false, err
	}

	answer, err := p.readLine(req)
	if err != nil {
		return false, err
	}

	switch req.Level {
	case LevelDanger:
		want := confirmPhrase(req)
		return strings.TrimSpace(answer) == want, nil
	default:
		return strings.EqualFold(strings.TrimSpace(answer), "yes"), nil
	}
}

func (p *Prompter) hint(level Level) string {
	switch level {
	case LevelDanger:
		return i18n.T(&goi18n.Message{ID: "HintDanger", Other: "如确认无误，请显式加 --yes-i-am-sure（危险级的唯一放行开关）"})
	default:
		return i18n.T(&goi18n.Message{ID: "HintConfirm", Other: "如确认无误，请显式加 --yes 或 -f"})
	}
}

// prompt 文案与目标名：危险级要求手敲目标名称，确认级只输入 yes。
func confirmPhrase(req Request) string {
	if len(req.Risks) == 0 {
		return ""
	}
	name := filepath.Base(req.Risks[0].Target)
	if name == "." || name == string(filepath.Separator) || name == "" {
		name = req.Risks[0].Target
	}
	return name
}

func (p *Prompter) readLine(req Request) (string, error) {
	if req.Level == LevelDanger {
		fmt.Fprint(p.Out, i18n.T(&goi18n.Message{
			ID: "PromptDanger", Other: "危险级操作，请输入目标名称 {{.Name}} 以确认（其它任意输入将取消）：",
		}, i18n.Data{"Name": confirmPhrase(req)}))
	} else {
		fmt.Fprint(p.Out, i18n.T(&goi18n.Message{ID: "PromptYes", Other: "输入 yes 继续，其它任意输入取消："}))
	}

	reader := bufio.NewReader(p.In)
	line, err := reader.ReadString('\n')
	if err != nil && !errors.Is(err, io.EOF) {
		return "", err
	}
	// EOF 视作取消，不当作同意
	return strings.TrimRight(line, "\r\n"), nil
}

// printSummary 打印"将发生什么"。一次调用只汇总一次，不逐个目标打断。
func (p *Prompter) printSummary(req Request) error {
	if _, err := fmt.Fprintln(p.Out, i18n.T(&goi18n.Message{ID: "SummaryHeader", Other: "将移动到回收目录（原数据不会永久删除）："})); err != nil {
		return err
	}

	const detailLimit = 5
	for i, r := range req.Risks {
		dest := i18n.T(&goi18n.Message{ID: "UnknownDest", Other: "未知"})
		if i < len(req.Dest) && req.Dest[i] != "" {
			dest = req.Dest[i]
		}
		if _, err := fmt.Fprintln(p.Out, i18n.T(&goi18n.Message{ID: "SummaryTarget", Other: "\n  目标  {{.V}}"}, i18n.Data{"V": r.Target})); err != nil {
			return err
		}
		kind := i18n.T(&goi18n.Message{ID: "KindFile", Other: "文件"})
		if r.IsDir {
			kind = i18n.T(&goi18n.Message{ID: "KindDir", Other: "目录"})
		}
		if _, err := fmt.Fprintln(p.Out, i18n.T(&goi18n.Message{ID: "SummaryKind", Other: "  类型  {{.V}}"}, i18n.Data{"V": kind})); err != nil {
			return err
		}
		if r.IsDir || r.Stats.Files > 1 {
			desc := i18n.T(&goi18n.Message{ID: "SummaryFiles", Other: "{{.Count}} 个文件"},
				i18n.Data{"Count": HumanCount(r.Stats.Files)}, r.Stats.Files)
			if r.Stats.Dirs > 0 {
				desc += i18n.T(&goi18n.Message{ID: "SummaryDirs", Other: " / {{.Count}} 个子目录"},
					i18n.Data{"Count": HumanCount(r.Stats.Dirs)}, r.Stats.Dirs)
			}
			desc += " / " + HumanBytes(r.Stats.Bytes, r.Stats.Truncated)
			if r.Stats.Incomplete {
				desc += i18n.T(&goi18n.Message{ID: "IncompleteStats", Other: "（统计不完整：有无法读取的内容，实际可能更多）"})
			}
			if _, err := fmt.Fprintln(p.Out, i18n.T(&goi18n.Message{ID: "SummaryContent", Other: "  内容  {{.V}}"}, i18n.Data{"V": desc})); err != nil {
				return err
			}
		}
		if r.Git.InRepo {
			if _, err := fmt.Fprintln(p.Out, i18n.T(&goi18n.Message{
				ID: "SummaryRepo", Other: "  仓库  {{.Repo}}（目标在版本库工作区内，未提交的内容无法从远端找回）",
			}, i18n.Data{"Repo": r.Git.RepoRoot})); err != nil {
				return err
			}
		}
		if _, err := fmt.Fprintln(p.Out, i18n.T(&goi18n.Message{ID: "SummaryDest", Other: "  目的  {{.V}}"}, i18n.Data{"V": dest})); err != nil {
			return err
		}
		if i+1 == detailLimit && len(req.Risks) > detailLimit {
			if _, err := fmt.Fprintln(p.Out, i18n.T(&goi18n.Message{
				ID: "SummaryMore", Other: "\n  …… 还有 {{.N}} 个目标（用 -n 可查看完整清单）",
			}, i18n.Data{"N": len(req.Risks) - detailLimit}, len(req.Risks)-detailLimit)); err != nil {
				return err
			}
			break
		}
	}
	_, err := fmt.Fprintln(p.Out)
	return err
}
