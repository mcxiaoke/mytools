// Package trash 负责"把目标搬进回收目录"这一步：创建操作目录、按去盘符的
// 镜像路径改名、并维护清单的状态机。
//
// 与 guard / volume 的分工：guard 决定"该不该删"，volume 决定"放到哪个卷的哪个根"，
// 本包只负责"真的搬"以及"留下可对账的记录"。
//
// 本包不会删除任何东西：唯一的文件系统写操作是 MkdirAll、Rename、WriteFile。
package trash

import (
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"

	"saferm/internal/platform"
)

// Stats 是一个条目的内容统计（由调用方的扫描阶段提供；未知时留零）。
type Stats struct {
	Files int64
	Bytes int64
}

// Source 是一个待处理的目标，已经过 guard 与 volume 的检查。
type Source struct {
	Input string // 命令行原始参数
	Path  string // 规范化后的绝对路径
	// SkipReason 非空表示只登记、不移动（例如 -f 下"路径不存在"）。
	// 把这类条目留在清单里，清单才是"用户要求了什么"的完整记录。
	SkipReason string
}

// Options 是开始一次操作所需的上下文信息。
type Options struct {
	Version     string
	Host        string
	User        string
	Cwd         string
	CommandLine string
	// Now 便于测试注入时间；留空则用 time.Now。
	Now func() time.Time
	// Pid 便于测试注入进程号；留空则用 os.Getpid。
	Pid int
}

func (o Options) now() time.Time {
	if o.Now != nil {
		return o.Now()
	}
	return time.Now()
}

func (o Options) pid() int {
	if o.Pid != 0 {
		return o.Pid
	}
	return os.Getpid()
}

// Session 是一次操作的句柄。它的所有方法都会立即把状态写回清单，
// 因此在任何时刻被强杀，磁盘上的记录都与实际进度对得上。
type Session struct {
	manifest *Manifest
	now      func() time.Time
}

// OpInfo 是对外暴露的操作信息，供输出使用。
type OpInfo struct {
	ID        string
	Dir       string
	Manifest  string
	TrashRoot string
}

// Begin 创建操作目录，把所有目标登记为 pending 并落盘。
//
// 顺序很关键：**先写清单再移动**。反过来的话，进程在移动过程中被杀就会
// 找不到原路径，事故变得不可解释（设计文档 §18 的结论）。
func Begin(trashRoot, volumeRoot string, sources []Source, opts Options) (*Session, error) {
	if len(sources) == 0 {
		return nil, fmt.Errorf("没有需要处理的目标")
	}

	opID, opDir, err := claimOpDir(trashRoot, opts)
	if err != nil {
		return nil, err
	}

	items := make([]Item, 0, len(sources))
	for _, src := range sources {
		if src.SkipReason != "" {
			items = append(items, Item{
				Input: src.Input, Source: src.Path,
				State: StateSkipped, Error: src.SkipReason,
			})
			continue
		}
		item, err := newItem(opDir, volumeRoot, src)
		if err != nil {
			// 单个条目连落点都算不出来（例如路径就在卷根、或镜像路径过长）
			// 时，不阻断整批：登记为失败，让用户看到具体是哪一个。C7 保证它没被动过。
			items = append(items, Item{
				Input: src.Input, Source: src.Path,
				State: StateFailed, Error: err.Error(),
			})
			continue
		}
		items = append(items, item)
	}

	m := &Manifest{
		ToolVersion: opts.Version,
		OpID:        opID,
		OpDir:       opDir,
		VolumeRoot:  volumeRoot,
		TrashRoot:   trashRoot,
		StartedAt:   opts.now(),
		Host:        opts.Host,
		User:        opts.User,
		Cwd:         opts.Cwd,
		CommandLine: opts.CommandLine,
		State:       OpPending,
		Items:       items,
	}
	m.Summary = summarize(m.Items)

	if err := m.Save(); err != nil {
		return nil, err
	}
	return &Session{manifest: m, now: opts.now}, nil
}

// Info 返回操作的基本信息。
func (s *Session) Info() OpInfo {
	m := s.manifest
	return OpInfo{
		ID:        m.OpID,
		Dir:       m.OpDir,
		Manifest:  ManifestPath(m.TrashRoot, m.OpID),
		TrashRoot: m.TrashRoot,
	}
}

// Items 返回清单条目（副本），索引与 Begin 传入的 sources 一一对应。
func (s *Session) Items() []Item {
	out := make([]Item, len(s.manifest.Items))
	copy(out, s.manifest.Items)
	return out
}

// Move 执行第 i 项的移动，并把状态机走完（pending → in-progress → done/failed）。
//
// 每一步都落盘。任何失败都只记录并返回，**不降级成删除、不重试、不改属性**（C7）：
// 失败意味着目标原封不动留在原处。
func (s *Session) Move(i int, stats Stats) error {
	if i < 0 || i >= len(s.manifest.Items) {
		return fmt.Errorf("条目索引越界：%d", i)
	}
	it := &s.manifest.Items[i]
	if it.State == StateSkipped || it.State == StateFailed {
		// 预登记阶段就已判定跳过/失败的条目（例如镜像路径过长）不再尝试
		return nil
	}

	if err := s.rename(i); err != nil {
		it.State = StateFailed
		it.Error = err.Error()
		it.Files, it.Bytes = 0, 0
		_ = s.persist()
		return err
	}

	it.State = StateDone
	it.Error = ""
	it.Files, it.Bytes = stats.Files, stats.Bytes
	return s.persist()
}

// rename 完成实际的移动：先建好镜像路径的父目录，再同卷改名。
//
// 超长路径不依赖系统设置：Go 的 os 包对超长路径会自动加 verbatim 前缀。
// Windows 保留设备名（nul/con/aux…）在 guard 预检阶段就已被拒绝
// （AbsClean 的字面性检查），走不到这里。
//
// 失败路径上会把状态推进到 in-progress 并落盘，这样"进程正好在改名中途被杀"
// 也能从清单里看出这一项处于不确定状态。
func (s *Session) rename(i int) error {
	it := &s.manifest.Items[i]
	it.State = StateInProgress
	if err := s.persist(); err != nil {
		return err
	}

	if it.Destination == "" {
		return fmt.Errorf("条目 %q 没有镜像落点", it.Input)
	}

	// 目标已存在就拒绝，绝不覆盖 —— 覆盖会让"可恢复"变成"两败俱伤"。
	if _, err := os.Lstat(it.Destination); err == nil {
		return fmt.Errorf("回收目录里已存在 %q，拒绝覆盖", it.Destination)
	} else if !os.IsNotExist(err) {
		return fmt.Errorf("检查落点 %q 失败：%w", it.Destination, err)
	}

	if err := os.MkdirAll(filepath.Dir(it.Destination), 0o700); err != nil {
		return fmt.Errorf("创建镜像目录 %q 失败：%w", filepath.Dir(it.Destination), err)
	}

	if err := os.Rename(it.Source, it.Destination); err != nil {
		if isCrossDevice(err) {
			return fmt.Errorf("目标与回收目录不在同一个卷，rename 失败（本工具不做「复制+删除」的降级）：%w", err)
		}
		return fmt.Errorf("移动 %q 失败：%w", it.Source, err)
	}
	return nil
}

// Finish 结算整批状态并落盘。
func (s *Session) Finish() Summary {
	m := s.manifest
	m.Summary = summarize(m.Items)
	m.State = m.Summary.State
	switch m.State {
	case OpDone, OpPartial, OpFailed:
		finished := s.now()
		m.FinishedAt = &finished
	}
	_ = m.Save()
	return m.Summary
}

func (s *Session) persist() error {
	s.manifest.Summary = summarize(s.manifest.Items)
	return s.manifest.Save()
}

// summarize 汇总条目状态。
//
// 整批状态只有 done / partial / failed / pending 四种：
// 只要有一项没搬成功就不算 done，避免用户误以为"全搬走了"；
// 还有 pending/in-progress 残留则说明这次没有正常收尾（进程被杀之类）。
func summarize(items []Item) Summary {
	sum := Summary{Total: len(items)}
	for _, it := range items {
		switch it.State {
		case StateDone:
			sum.Done++
		case StateSkipped:
			sum.Skipped++
		case StateFailed:
			sum.Failed++
		}
		sum.Files += it.Files
		sum.Bytes += it.Bytes
	}

	settled := sum.Done + sum.Skipped + sum.Failed
	switch {
	case settled < sum.Total:
		sum.State = OpPending // 有条目没走完，说明被中断
	case sum.Failed == 0:
		sum.State = OpDone // 全部成功，或全部跳过（原数据未动）
	case sum.Done+sum.Skipped > 0:
		sum.State = OpPartial
	default:
		sum.State = OpFailed
	}
	return sum
}

// OpIDFor 给出指定时刻与进程号对应的操作号。
//
// 供调用方在**真正创建之前**预估落点（交互提示里要展示"会放到哪"，
// 但确认之前不允许创建任何东西）。实际抢占时若撞名会追加序号。
func OpIDFor(now time.Time, pid int) string {
	return now.Format("20060102-150405") + fmt.Sprintf("-%d", pid)
}

// claimOpDir 抢占一个操作目录名：`YYYYMMDD-HHMMSS-<pid>`，撞名则追加序号。
//
// 用 os.Mkdir 而不是 MkdirAll：目录已存在时它必须报错，这样我们才能发现撞名。
func claimOpDir(trashRoot string, opts Options) (opID, opDir string, err error) {
	base := OpIDFor(opts.now(), opts.pid())
	for attempt := 0; attempt < 100; attempt++ {
		id := base
		if attempt > 0 {
			id = fmt.Sprintf("%s-%d", base, attempt+1)
		}
		dir := filepath.Join(trashRoot, id)
		mkErr := os.Mkdir(dir, 0o700)
		if mkErr == nil {
			return id, dir, nil
		}
		if !os.IsExist(mkErr) {
			return "", "", fmt.Errorf("创建操作目录 %q 失败：%w", dir, mkErr)
		}
	}
	return "", "", fmt.Errorf("在 %q 下无法分配操作目录名（连续撞名）", trashRoot)
}

// MirrorPath 计算 src 在操作目录内的镜像位置。
//
// 规则（设计文档 §4.1）：**去掉卷挂载点前缀，其余目录结构原样保留**。
//
//	D:\projects\a\tools   + 卷根 D:\            → <opDir>\projects\a\tools
//	/data/a/b             + 卷根 /data          → <opDir>/a/b
//	/etc/fstab            + 卷根 /              → <opDir>/etc/fstab
//	\\nas\share\a         + 卷根 \\nas\share\   → <opDir>\a
//
// 这样手工恢复时只要把操作目录里的内容整体搬回卷根，结构天然对齐。
func MirrorPath(opDir, volumeRoot, src string) (string, error) {
	rel, err := relativeToVolume(volumeRoot, src)
	if err != nil {
		return "", err
	}
	mirror := filepath.Join(opDir, rel)

	// 超长时报错而不是截断改名：截断会破坏目录结构，
	// 而树结构正是"手工恢复不乱"的前提（设计文档 §4.4）。
	if len(mirror) > maxPathLength {
		return "", fmt.Errorf(
			"镜像路径过长（%d 字符，上限 %d）：%s。请为本卷配置更浅的回收根",
			len(mirror), maxPathLength, mirror)
	}
	return mirror, nil
}

// maxPathLength 取 Windows 扩展路径上限留一点余量；Linux 上只是个宽松护栏。
const maxPathLength = 32000

// relativeToVolume 去掉卷挂载点前缀。
func relativeToVolume(volumeRoot, p string) (string, error) {
	vr := filepath.Clean(volumeRoot)
	pp := filepath.Clean(p)

	if len(pp) <= len(vr) || !hasVolumePrefix(pp, vr) {
		return "", fmt.Errorf("路径 %q 不在卷 %q 之内", p, volumeRoot)
	}

	rel := strings.TrimLeft(pp[len(vr):], "/\\")
	if rel == "" {
		return "", fmt.Errorf("路径 %q 就是卷根 %q，不能作为删除目标", p, volumeRoot)
	}
	// Clean 已经消掉了 . 与 ..，这里再挡一次，避免镜像出带上级引用的路径
	for _, part := range strings.FieldsFunc(rel, func(r rune) bool { return r == '\\' || r == '/' }) {
		if part == ".." {
			return "", fmt.Errorf("路径 %q 相对卷根含有上级引用，拒绝", p)
		}
	}
	return rel, nil
}

// hasVolumePrefix 判断 path 是否以 volumeRoot 开头。
//
// Windows 上盘符大小写不敏感，所以用 EqualFold 比较前缀；两边长度一致，
// 因此可以按字节切片（实际使用中的卷挂载点都是 ASCII）。
func hasVolumePrefix(path, volumeRoot string) bool {
	if runtime.GOOS == "windows" {
		return strings.EqualFold(path[:len(volumeRoot)], volumeRoot)
	}
	return strings.HasPrefix(path, volumeRoot)
}

// newItem 构造一个待移动条目：判断类型、算出镜像落点。
func newItem(opDir, volumeRoot string, src Source) (Item, error) {
	kind, err := kindOf(src.Path)
	if err != nil {
		return Item{}, err
	}
	dest, err := MirrorPath(opDir, volumeRoot, src.Path)
	if err != nil {
		return Item{}, err
	}
	return Item{
		Input:       src.Input,
		Source:      src.Path,
		Destination: dest,
		Kind:        kind,
		State:       StatePending,
	}, nil
}

// kindOf 判断条目类型。符号链接/junction 单独标记：
// 我们移动的是链接本身，不是它指向的内容。
func kindOf(p string) (string, error) {
	info, err := os.Lstat(p)
	if err != nil {
		return "", fmt.Errorf("lstat %q 失败：%w", p, err)
	}
	if info.IsDir() {
		return "dir", nil
	}
	if isLink, linkErr := platform.IsReparsePoint(p); linkErr == nil && isLink {
		return "link", nil
	}
	return "file", nil
}
