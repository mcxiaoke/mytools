package volume

import (
	"fmt"
	"io"
	"strings"
	"text/tabwriter"
	"time"

	goi18n "github.com/nicksnyder/go-i18n/v2/i18n"

	"saferm/internal/i18n"
)

// ProbeSummary 是 `saferm where` 的渲染结果，纯函数、无副作用，便于测试。
type ProbeSummary struct {
	ConfigPath string
	// ConfigLines 是"生效配置"的预格式化行（设计文档 §8.1 要求 where 打印生效配置）。
	// 为空则不输出该段。内容由 cmd 层从 config 组装，volume 不依赖 config 包。
	ConfigLines []string
	Version     string
	Probes      []Probe
	// Unfinished 是没有正常收尾的操作（进程被杀/断电等），需要用户去核对。
	Unfinished []UnfinishedOp
	// BadManifests 是解析失败的清单数量。
	BadManifests int
	// Notes 是额外提示（例如"只枚举本地盘符"这类能力边界）。
	Notes []string
}

// UnfinishedOp 是一个未正常收尾的批次，用于展示。
type UnfinishedOp struct {
	OpID      string
	State     string
	TrashRoot string
	StartedAt time.Time
	Items     int
	Done      int
	Failed    int
}

// Render 把自检结果写成人类可读的文本。
//
// 输出要求（设计文档 §8.1）：即便某个卷不可用，也要报出"本来打算用哪个目录"，
// 否则用户不知道该去修什么。
func (s ProbeSummary) Render(w io.Writer) error {
	if _, err := fmt.Fprintf(w, "saferm %s\n", s.Version); err != nil {
		return err
	}
	if s.ConfigPath == "" {
		if _, err := fmt.Fprintln(w, i18n.T(&goi18n.Message{ID: "WhereConfigUnset", Other: "配置文件      未加载（当前使用内置默认值）"})); err != nil {
			return err
		}
	} else {
		if _, err := fmt.Fprintln(w, i18n.T(&goi18n.Message{ID: "WhereConfigPath", Other: "配置文件      {{.V}}"}, i18n.Data{"V": s.ConfigPath})); err != nil {
			return err
		}
	}

	if len(s.ConfigLines) > 0 {
		if _, err := fmt.Fprintln(w, i18n.T(&goi18n.Message{ID: "WhereEffectiveConfig", Other: "\n生效配置（优先级：命令行 > 配置文件 > 内置默认值）"})); err != nil {
			return err
		}
		for _, line := range s.ConfigLines {
			if _, err := fmt.Fprintf(w, "  %s\n", line); err != nil {
				return err
			}
		}
	}

	if _, err := fmt.Fprintln(w, i18n.T(&goi18n.Message{ID: "WhereVolumesHeader", Other: "\n各卷的回收目录"})); err != nil {
		return err
	}

	tw := tabwriter.NewWriter(w, 0, 0, 2, ' ', 0)
	if _, err := fmt.Fprintln(tw, i18n.T(&goi18n.Message{ID: "WhereTableHeader", Other: "  卷\t来源\t可写\t状态\t回收目录\t"})); err != nil {
		return err
	}
	for _, p := range s.Probes {
		status := probeStatus(p)
		writable := i18n.T(&goi18n.Message{ID: "Yes", Other: "是"})
		if !p.Writable {
			writable = i18n.T(&goi18n.Message{ID: "No", Other: "否"})
		}
		root := p.TrashRoot
		if p.Problem != "" {
			root += "   " + p.Problem
		}
		if _, err := fmt.Fprintf(tw, "  %s\t%s\t%s\t%s\t%s\t\n",
			p.VolumeRoot, p.Source, writable, status, root); err != nil {
			return err
		}
	}
	if err := tw.Flush(); err != nil {
		return err
	}

	if _, err := fmt.Fprintln(w, i18n.T(&goi18n.Message{
		ID:    "WhereStatusLegend",
		Other: "\n状态说明：就绪＝已存在且带 {{.Marker}} 标记；待创建＝尚不存在，确认后会创建；不可用＝见右侧原因",
	}, i18n.Data{"Marker": MarkerName})); err != nil {
		return err
	}

	if len(s.Unfinished) > 0 {
		if _, err := fmt.Fprintln(w, i18n.T(&goi18n.Message{ID: "WhereUnfinishedHeader", Other: "\n未正常收尾的操作（进程被杀或断电，需要人工核对）"})); err != nil {
			return err
		}
		tw := tabwriter.NewWriter(w, 0, 0, 2, ' ', 0)
		if _, err := fmt.Fprintln(tw, i18n.T(&goi18n.Message{ID: "WhereUnfinishedTable", Other: "  操作号\t状态\t条目\t已搬\t失败\t开始时间\t回收目录\t"})); err != nil {
			return err
		}
		for _, op := range s.Unfinished {
			if _, err := fmt.Fprintf(tw, "  %s\t%s\t%d\t%d\t%d\t%s\t%s\t\n",
				op.OpID, op.State, op.Items, op.Done, op.Failed,
				op.StartedAt.Format("2006-01-02 15:04:05"), op.TrashRoot); err != nil {
				return err
			}
		}
		if err := tw.Flush(); err != nil {
			return err
		}
		if _, err := fmt.Fprintln(w, i18n.T(&goi18n.Message{
			ID:    "WhereUnfinishedHint",
			Other: "  提示：清单文件与操作目录同级（<操作号>.op.json），可据此确认每一项的去向。",
		})); err != nil {
			return err
		}
	}

	if s.BadManifests > 0 {
		if _, err := fmt.Fprintln(w, i18n.T(&goi18n.Message{
			ID:    "WhereBadManifests",
			Other: "\n注意：有 {{.N}} 个清单文件无法解析，已单独计数（可能是写入时被中断）。",
		}, i18n.Data{"N": s.BadManifests}, s.BadManifests)); err != nil {
			return err
		}
	}

	for _, n := range s.Notes {
		if _, err := fmt.Fprintf(w, "\n%s\n", strings.TrimSpace(n)); err != nil {
			return err
		}
	}
	return nil
}

func probeStatus(p Probe) string {
	switch {
	case !p.Usable:
		return i18n.T(&goi18n.Message{ID: "StatusUnusable", Other: "不可用"})
	case !p.Exists:
		return i18n.T(&goi18n.Message{ID: "StatusToCreate", Other: "待创建"})
	case p.HasMarker:
		return i18n.T(&goi18n.Message{ID: "StatusReady", Other: "就绪"})
	default:
		// 存在且为空、可以认领
		return i18n.T(&goi18n.Message{ID: "StatusToClaim", Other: "待认领"})
	}
}

// DefaultNotes 返回 `where` 输出里固定的能力边界说明。
func DefaultNotes() []string {
	return []string{
		i18n.T(&goi18n.Message{ID: "WhereNotes", Other: `说明：本命令只枚举本地卷（Windows 盘符 / Linux 实体挂载点）。
      网络共享（如 \\server\share）不在此列表中，实际使用时按目标所在卷按需解析。
      回收目录必须与目标同卷，跨卷时本工具拒绝执行而不是退化成「复制+删除」。`}),
	}
}
