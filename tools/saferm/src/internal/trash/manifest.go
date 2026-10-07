package trash

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"
)

// 条目状态机（设计文档 §9）：pending → in-progress → done / skipped / failed。
const (
	StatePending    = "pending"
	StateInProgress = "in-progress"
	StateDone       = "done"
	StateSkipped    = "skipped"
	StateFailed     = "failed"
)

// 整批汇总状态。
const (
	OpPending = "pending"
	OpDone    = "done"
	OpPartial = "partial"
	OpFailed  = "failed"
)

// Item 是清单里的一个条目。
//
// 注意 Source 与 Destination 都是绝对路径：Destination 是"去掉卷挂载点前缀后
// 原样保留目录结构"的镜像位置，手工恢复时把操作目录里的内容整体搬回卷根即可。
type Item struct {
	Input       string `json:"input"`
	Source      string `json:"source"`
	Destination string `json:"destination,omitempty"`
	Kind        string `json:"kind,omitempty"` // file / dir / link
	State       string `json:"state"`
	Error       string `json:"error,omitempty"`
	Files       int64  `json:"files,omitempty"`
	Bytes       int64  `json:"bytes,omitempty"`
}

// Summary 是整批的统计结果。
type Summary struct {
	Total   int    `json:"total"`
	Done    int    `json:"done"`
	Skipped int    `json:"skipped"`
	Failed  int    `json:"failed"`
	Files   int64  `json:"files"`
	Bytes   int64  `json:"bytes"`
	State   string `json:"state"`
}

// Manifest 是 `<操作目录名>.op.json` 的内容。
//
// 它不是索引库，只是"这次调用干了什么"的留痕：任何时刻中断，都能据此看出
// 哪些条目已经搬走、哪些还没动（设计文档 §4.6、§9）。
type Manifest struct {
	ToolVersion string     `json:"tool_version"`
	OpID        string     `json:"op_id"`
	OpDir       string     `json:"op_dir"`
	VolumeRoot  string     `json:"volume_root"`
	TrashRoot   string     `json:"trash_root"`
	StartedAt   time.Time  `json:"started_at"`
	FinishedAt  *time.Time `json:"finished_at,omitempty"`
	Host        string     `json:"host"`
	User        string     `json:"user"`
	Cwd         string     `json:"cwd"`
	CommandLine string     `json:"command_line"`
	State       string     `json:"state"`
	Summary     Summary    `json:"summary"`
	Items       []Item     `json:"items"`
}

// ManifestPath 返回某个操作目录对应的清单文件路径。
//
// 清单刻意放在操作目录**外面**：这样操作目录内部只有待手工恢复的内容，
// 没有任何杂质文件（设计文档 §4.3）。
func ManifestPath(trashRoot, opID string) string {
	return filepath.Join(trashRoot, opID+".op.json")
}

// Save 原子地写入清单：先写临时文件再 rename，避免留下半截 JSON。
func (m *Manifest) Save() error {
	if m.TrashRoot == "" || m.OpID == "" {
		return fmt.Errorf("清单缺少回收根或操作号，拒绝写入")
	}
	data, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return fmt.Errorf("序列化清单失败：%w", err)
	}
	data = append(data, '\n')

	path := ManifestPath(m.TrashRoot, m.OpID)
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return fmt.Errorf("写入清单临时文件失败：%w", err)
	}
	if err := os.Rename(tmp, path); err != nil {
		return fmt.Errorf("提交清单失败：%w", err)
	}
	return nil
}

// LoadManifest 读取一个清单文件。
func LoadManifest(path string) (*Manifest, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var m Manifest
	if err := json.Unmarshal(data, &m); err != nil {
		return nil, fmt.Errorf("解析清单 %q 失败：%w", path, err)
	}
	return &m, nil
}

// UnfinishedOp 描述一个没有正常收尾的操作。
type UnfinishedOp struct {
	OpID      string
	State     string
	StartedAt time.Time
	Items     int
	Done      int
	Failed    int
}

// Unfinished 扫描回收根，列出没有正常收尾的操作。
//
// 用途：进程被杀、断电之后，用户需要知道"上一次到底搬走了什么"。
// 清单本身是 JSON，坏掉的清单会被跳过并计入返回的 badCount。
func Unfinished(trashRoot string) (ops []UnfinishedOp, badCount int, err error) {
	entries, err := os.ReadDir(trashRoot)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, 0, nil
		}
		return nil, 0, err
	}

	for _, e := range entries {
		name := e.Name()
		if len(name) < len(".op.json") || filepath.Ext(name) != ".json" {
			continue
		}
		m, err := LoadManifest(filepath.Join(trashRoot, name))
		if err != nil {
			badCount++
			continue
		}
		// 只有"从未正常收尾"（FinishedAt 为空，即进程被杀/断电）的操作才算未完成。
		// partial / failed 是正常收尾的批次：结果已经记录在清单里，不算悬案，
		// 否则一次含失败项的历史批次会让 `where` 永远返回非零退出码。
		if m.FinishedAt != nil {
			continue
		}
		ops = append(ops, UnfinishedOp{
			OpID:      m.OpID,
			State:     m.State,
			StartedAt: m.StartedAt,
			Items:     len(m.Items),
			Done:      m.Summary.Done,
			Failed:    m.Summary.Failed,
		})
	}
	return ops, badCount, nil
}
