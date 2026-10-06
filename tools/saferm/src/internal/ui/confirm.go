package ui

import (
	"bufio"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"

	"golang.org/x/term"
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
		return "确认级"
	case LevelDanger:
		return "危险级"
	default:
		return "无需确认"
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
// 刻意**不把"目标在工作目录之外"当危险级条件**：从固定目录启动时删任何别的
// 路径都要手敲名字太吵，结局是用户养成无脑加 --yes-i-am-sure 的习惯，
// 危险级就形同虚设。危险级只由"体量"与"在版本库工作区内"这类客观信号触发。
func Decide(risks []Risk, cfg ConfirmConfig) Level {
	if len(risks) == 0 {
		return LevelNone
	}

	var files, dirs, bytes int64
	var incomplete, inRepo, anyDir bool
	for _, r := range risks {
		files += r.Stats.Files
		dirs += r.Stats.Dirs
		bytes += r.Stats.Bytes
		incomplete = incomplete || r.Stats.Incomplete
		inRepo = inRepo || r.Git.InRepo
		anyDir = anyDir || r.IsDir
	}

	// 危险级：体量很大，或在版本库工作区内（后者正是本次事故的场景）
	if cfg.DangerFileThreshold > 0 && files+dirs > cfg.DangerFileThreshold {
		return LevelDanger
	}
	if inRepo {
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
var ErrNonInteractive = errors.New("需要人工确认，但当前不是交互式终端")

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

	if !p.Interactive() {
		if req.Level == LevelConfirm && ap.SkipConfirm {
			return true, nil
		}
		if req.Level == LevelDanger && ap.YesIAmSure {
			return true, nil
		}
		return false, fmt.Errorf("%w。%s", ErrNonInteractive, p.hint(req.Level))
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
		return "如确认无误，请显式加 --yes-i-am-sure（危险级的唯一放行开关）"
	default:
		return "如确认无误，请显式加 --yes 或 -f"
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
		fmt.Fprintf(p.Out, "危险级操作，请输入目标名称 %s 以确认（其它任意输入将取消）：", confirmPhrase(req))
	} else {
		fmt.Fprint(p.Out, "输入 yes 继续，其它任意输入取消：")
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
	if _, err := fmt.Fprintln(p.Out, "将移动到回收目录（原数据不会永久删除）："); err != nil {
		return err
	}

	const detailLimit = 5
	for i, r := range req.Risks {
		dest := "未知"
		if i < len(req.Dest) && req.Dest[i] != "" {
			dest = req.Dest[i]
		}
		if _, err := fmt.Fprintf(p.Out, "\n  目标  %s\n", r.Target); err != nil {
			return err
		}
		kind := "文件"
		if r.IsDir {
			kind = "目录"
		}
		if _, err := fmt.Fprintf(p.Out, "  类型  %s\n", kind); err != nil {
			return err
		}
		if r.IsDir || r.Stats.Files > 1 {
			desc := fmt.Sprintf("%s 个文件", HumanCount(r.Stats.Files))
			if r.Stats.Dirs > 0 {
				desc += fmt.Sprintf(" / %s 个子目录", HumanCount(r.Stats.Dirs))
			}
			desc += " / " + HumanBytes(r.Stats.Bytes, r.Stats.Truncated)
			if r.Stats.Incomplete {
				desc += "（统计不完整：有无法读取的内容，实际可能更多）"
			}
			if _, err := fmt.Fprintf(p.Out, "  内容  %s\n", desc); err != nil {
				return err
			}
		}
		if r.Git.InRepo {
			if _, err := fmt.Fprintf(p.Out, "  仓库  %s（目标在版本库工作区内，未提交的内容无法从远端找回）\n", r.Git.RepoRoot); err != nil {
				return err
			}
		}
		if _, err := fmt.Fprintf(p.Out, "  目的  %s\n", dest); err != nil {
			return err
		}
		if i+1 == detailLimit && len(req.Risks) > detailLimit {
			if _, err := fmt.Fprintf(p.Out, "\n  …… 还有 %d 个目标（用 -n 可查看完整清单）\n", len(req.Risks)-detailLimit); err != nil {
				return err
			}
			break
		}
	}
	_, err := fmt.Fprintln(p.Out)
	return err
}
