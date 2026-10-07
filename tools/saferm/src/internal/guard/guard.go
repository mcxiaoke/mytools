// Package guard 实现设计文档 §5 的全部护栏。
//
// 这是整个工具的核心资产：删除动作本身只是"移动到同卷回收目录"，
// 真正防止事故的是这里——它必须在任何文件系统写操作发生之前跑完，
// 并且对**最终目标集合**（含通配符展开结果）逐个检查。
//
// 与设计文档的对应关系：
//
//	§5.0 路径规范化  → AbsClean + platform.ExpandShortName + pathKey
//	§5.1 硬拦截清单  → Check（第 1~8、11、12 条）+ CheckTrashRelation（第 9、10 条）
//	§5.2 绕过与批次  → Dangerous() 决定哪些能被 --allow-dangerous 降级为警告
package guard

import (
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	goi18n "github.com/nicksnyder/go-i18n/v2/i18n"

	"saferm/internal/i18n"
	"saferm/internal/platform"
)

// Reason 是拒绝原因的分类，便于测试断言与文案统一。
type Reason string

const (
	// 参数错误类：调用方写错了，任何开关都不能绕过。
	ReasonEmptyOperand     Reason = "empty-operand"
	ReasonNoOperands       Reason = "no-operands"
	ReasonNotExist         Reason = "not-exist"
	ReasonUnreadable       Reason = "unreadable"
	ReasonUnresolvedPath   Reason = "unresolved-path"
	ReasonDuplicateOperand Reason = "duplicate-operand"
	ReasonNestedOperand    Reason = "nested-operand"

	// 危险路径类：可被 --allow-dangerous 降级为警告。
	ReasonIsCwd           Reason = "is-cwd"
	ReasonAncestorOfCwd   Reason = "ancestor-of-cwd"
	ReasonVolumeRoot      Reason = "volume-root"
	ReasonProtectedPath   Reason = "protected-path"
	ReasonVCSRoot         Reason = "vcs-root"
	ReasonInsideTrash     Reason = "inside-trash"
	ReasonAncestorOfTrash Reason = "ancestor-of-trash"

	// 环境与配置类：既不是参数写错，也不是"目标很危险"，
	// 而是当前环境下这件事根本没法安全完成。任何开关都不可绕过。
	ReasonCrossVolume   Reason = "cross-volume"
	ReasonTrashUnusable Reason = "trash-unusable"
)

// Bypassable 表示该违规属于"危险路径类"，可用 --allow-dangerous 降级为警告；
// 参数错误类永远不可绕过（设计文档 §5.2）。
func (r Reason) Bypassable() bool {
	switch r {
	case ReasonIsCwd, ReasonAncestorOfCwd, ReasonVolumeRoot, ReasonProtectedPath,
		ReasonVCSRoot, ReasonInsideTrash, ReasonAncestorOfTrash:
		return true
	}
	return false
}

// Violation 是一条被拒绝的理由。
type Violation struct {
	Reason Reason
	Input  string // 命令行原始参数
	Path   string // 规范化后的绝对路径
	Detail string // 补充说明（cwd、仓库根、冲突对象等）
}

func (v Violation) Error() string { return v.Message() }

// Message 生成面向用户的拒绝说明（源语言为中文，英文翻译见 internal/i18n/locales）。
func (v Violation) Message() string {
	where := v.Path
	if where == "" {
		where = v.Input
	}
	suffix := ""
	if v.Detail != "" {
		suffix = i18n.T(&goi18n.Message{ID: "DetailSuffix", Other: "（{{.V}}）"}, i18n.Data{"V": v.Detail})
	}
	switch v.Reason {
	case ReasonEmptyOperand:
		return i18n.T(&goi18n.Message{ID: "ViolationEmptyOperand", Other: "参数是空字符串或纯空白。若要删除名为 $null 的文件，PowerShell 里请写 saferm '$null'（用单引号）"})
	case ReasonNoOperands:
		return i18n.T(&goi18n.Message{ID: "ViolationNoOperands", Other: "没有给出任何路径"})
	case ReasonNotExist:
		return i18n.T(&goi18n.Message{ID: "ViolationNotExist", Other: "路径不存在：{{.Where}}。本工具不做路径猜测，需要忽略不存在的路径请显式加 -f"}, i18n.Data{"Where": where})
	case ReasonUnreadable:
		return i18n.T(&goi18n.Message{ID: "ViolationUnreadable", Other: "无法读取 {{.Where}}：{{.Err}}"}, i18n.Data{"Where": where, "Err": v.Detail})
	case ReasonUnresolvedPath:
		return i18n.T(&goi18n.Message{ID: "ViolationUnresolvedPath", Other: "无法可靠地规范化 {{.Where}}，为避免绕过护栏，拒绝执行：{{.Err}}"}, i18n.Data{"Where": where, "Err": v.Detail})
	case ReasonDuplicateOperand:
		return i18n.T(&goi18n.Message{ID: "ViolationDuplicateOperand", Other: "同一批参数里有重复项：{{.Where}}{{.Suffix}}"}, i18n.Data{"Where": where, "Suffix": suffix})
	case ReasonNestedOperand:
		return i18n.T(&goi18n.Message{ID: "ViolationNestedOperand", Other: "同一批参数里存在上下级关系：{{.Where}}{{.Suffix}}"}, i18n.Data{"Where": where, "Suffix": suffix})
	case ReasonIsCwd:
		return i18n.T(&goi18n.Message{ID: "ViolationIsCwd", Other: "拒绝：目标是当前工作目录：{{.Where}}"}, i18n.Data{"Where": where})
	case ReasonAncestorOfCwd:
		return i18n.T(&goi18n.Message{ID: "ViolationAncestorOfCwd", Other: "拒绝：目标是当前工作目录的上级：{{.Where}}{{.Suffix}}"}, i18n.Data{"Where": where, "Suffix": suffix})
	case ReasonVolumeRoot:
		return i18n.T(&goi18n.Message{ID: "ViolationVolumeRoot", Other: "拒绝：目标是卷根：{{.Where}}"}, i18n.Data{"Where": where})
	case ReasonProtectedPath:
		return i18n.T(&goi18n.Message{ID: "ViolationProtectedPath", Other: "拒绝：目标是受保护的系统或用户关键路径：{{.Where}}{{.Suffix}}"}, i18n.Data{"Where": where, "Suffix": suffix})
	case ReasonVCSRoot:
		return i18n.T(&goi18n.Message{ID: "ViolationVCSRoot", Other: "拒绝：目标是版本库根目录：{{.Where}}{{.Suffix}}"}, i18n.Data{"Where": where, "Suffix": suffix})
	case ReasonInsideTrash:
		return i18n.T(&goi18n.Message{ID: "ViolationInsideTrash", Other: "拒绝：目标位于回收目录内：{{.Where}}{{.Suffix}}"}, i18n.Data{"Where": where, "Suffix": suffix})
	case ReasonAncestorOfTrash:
		return i18n.T(&goi18n.Message{ID: "ViolationAncestorOfTrash", Other: "拒绝：目标包含回收目录，移动会把回收目录搬进它自己内部：{{.Where}}{{.Suffix}}"}, i18n.Data{"Where": where, "Suffix": suffix})
	case ReasonCrossVolume:
		return i18n.T(&goi18n.Message{ID: "ViolationCrossVolume", Other: "拒绝：回收目录与目标不在同一个卷，无法用一次 rename 完成移动（本工具刻意不做「复制+删除」的降级）：{{.Where}}{{.Suffix}}。请为该卷配置回收根，saferm where 可看当前解析结果"}, i18n.Data{"Where": where, "Suffix": suffix})
	case ReasonTrashUnusable:
		return i18n.T(&goi18n.Message{ID: "ViolationTrashUnusable", Other: "拒绝：回收目录不可用：{{.Where}}{{.Suffix}}"}, i18n.Data{"Where": where, "Suffix": suffix})
	}
	return i18n.T(&goi18n.Message{ID: "ViolationGeneric", Other: "拒绝：{{.Where}}{{.Suffix}}"}, i18n.Data{"Where": where, "Suffix": suffix})
}

// Violations 是一批拒绝理由，实现 error 以便一次性汇报。
type Violations []Violation

func (vs Violations) Error() string {
	if len(vs) == 0 {
		return ""
	}
	var b strings.Builder
	fmt.Fprint(&b, i18n.T(&goi18n.Message{ID: "PreflightFailed", Other: "预检未通过，共 {{.N}} 项，整批未执行（原数据未改动）"}, i18n.Data{"N": len(vs)}, len(vs)))
	for _, v := range vs {
		b.WriteString("\n  - ")
		b.WriteString(v.Message())
	}
	return b.String()
}

// Target 是通过检查、可以进入移动流程的目标。
type Target struct {
	Input string      // 命令行原始参数
	Path  string      // 规范化后的绝对路径（已展开 8.3 短名）
	Info  os.FileInfo // Lstat 结果：不跟随最后一级链接
}

// Skip 是 -f 语义下被静默跳过的路径（不存在）。
type Skip struct {
	Input  string
	Path   string
	Reason string
}

// Options 控制检查行为。
//
// 默认值即"最安全"：零值表示检查 VCS 根、不忽略缺失、不允许危险路径。
type Options struct {
	// Cwd 留空时取 os.Getwd()。主要供测试注入。
	Cwd string

	// IgnoreMissing 对应 -f：路径不存在不报错，跳过该项。
	IgnoreMissing bool

	// AllowDangerous 对应 --allow-dangerous：把"危险路径类"拒绝降级为警告。
	// 参数错误类不受影响。
	AllowDangerous bool

	// ExtraProtected 是配置追加的受保护路径（按子树保护），只能加不能减。
	ExtraProtected []string

	// DisableVCSRootCheck 关闭"目录含 .git/.hg/.svn 即拒绝"。
	// 用反向命名是为了让零值等于"开启检查"。
	DisableVCSRootCheck bool
}

// Report 是一次预检的完整结果。
type Report struct {
	Targets    []Target
	Skipped    []Skip
	Violations Violations
	// Warnings 记录被 --allow-dangerous 放行的危险规则，必须打印给用户看。
	Warnings []string
	// Cwd 是本次检查实际使用的工作目录（已规范化），供后续日志与相对路径显示。
	Cwd string
}

// Failed 表示预检未通过（存在不可放行的拒绝项）。
func (r Report) Failed() bool { return len(r.Violations) > 0 }

// Add 记录一条拒绝，并按 §5.2 的绕过规则决定它是"拒绝"还是"警告"。
//
// 绕过策略只在这一处实现，避免各处自行判断导致某条规则被漏掉或被多绕一次。
func (r *Report) Add(v Violation, allowDangerous bool) {
	if v.Reason.Bypassable() && allowDangerous {
		r.Warnings = append(r.Warnings, i18n.T(&goi18n.Message{
			ID: "AllowDangerousWarning", Other: "--allow-dangerous 生效，已放行本应拒绝的路径：{{.Msg}}",
		}, i18n.Data{"Msg": v.Message()}))
		return
	}
	r.Violations = append(r.Violations, v)
}

type protectedPath struct {
	Path    string
	Subtree bool
}

var vcsMarkers = []string{".git", ".hg", ".svn"}

// Check 对一批命令行参数做 §5.1 第 1~8、11、12 条的检查。
//
// 第 9、10 条（与回收目录的关系）需要在解析出回收根之后调用 CheckTrashRelation，
// 因为那取决于目标所在的卷。
//
// 返回的 error 只用于"工具自身出问题"（取不到 cwd 之类）；语义上的拒绝统一走
// Report.Violations，便于一次性汇总给用户。
func Check(operands []string, opts Options) (Report, error) {
	var rep Report

	if len(operands) == 0 {
		// 第 2 条：绝不"没有参数就静默成功"
		rep.Violations = append(rep.Violations, Violation{Reason: ReasonNoOperands})
		return rep, nil
	}

	cwd, err := resolveCwd(opts.Cwd)
	if err != nil {
		return rep, err
	}
	rep.Cwd = cwd

	protected := builtinProtected()
	for _, p := range opts.ExtraProtected {
		if strings.TrimSpace(p) != "" {
			protected = append(protected, protectedPath{Path: p, Subtree: true})
		}
	}

	c := &checker{opts: opts, cwd: cwd, protected: protected, rep: &rep}

	// 第一遍：规范化 + 存在性 + 重复项（第 1、3、11 条）
	seen := make(map[string]string)
	for _, in := range operands {
		if strings.TrimSpace(in) == "" {
			c.reject(Violation{Reason: ReasonEmptyOperand, Input: in})
			continue
		}
		t, skip, ok := c.normalize(in)
		if skip != nil {
			rep.Skipped = append(rep.Skipped, *skip)
			continue
		}
		if !ok {
			continue
		}
		if first, dup := seen[pathKey(t.Path)]; dup {
			c.reject(Violation{
				Reason: ReasonDuplicateOperand, Input: in, Path: t.Path,
				Detail: i18n.T(&goi18n.Message{ID: "DetailSameAs", Other: "与 {{.V}} 指向同一位置"}, i18n.Data{"V": strconv.Quote(first)}),
			})
			continue
		}
		seen[pathKey(t.Path)] = in
		rep.Targets = append(rep.Targets, t)
	}

	// 第二遍：单条危险路径检查（第 4~8 条）
	for _, t := range rep.Targets {
		c.checkCwdRelation(t)
		c.checkVolumeRoot(t)
		c.checkProtected(t)
		if !opts.DisableVCSRootCheck {
			c.checkVCSRoot(t)
		}
	}

	// 第三遍：批次内的上下级关系（第 12 条）
	c.checkNested(rep.Targets)

	return rep, nil
}

type checker struct {
	opts      Options
	cwd       string
	protected []protectedPath
	rep       *Report
}

// reject 记录一条拒绝；危险路径类在 --allow-dangerous 下降级为警告。
func (c *checker) reject(v Violation) {
	c.rep.Add(v, c.opts.AllowDangerous)
}

// normalize 完成 §5.0 的规范化：绝对化 → 存在性 → 短名展开。
func (c *checker) normalize(in string) (Target, *Skip, bool) {
	abs, err := platform.AbsClean(in)
	if err != nil {
		c.reject(Violation{Reason: ReasonUnresolvedPath, Input: in, Detail: err.Error()})
		return Target{}, nil, false
	}

	info, err := os.Lstat(abs)
	if err != nil {
		if os.IsNotExist(err) {
			if c.opts.IgnoreMissing {
				return Target{}, &Skip{Input: in, Path: abs, Reason: i18n.T(&goi18n.Message{ID: "SkipNotExists", Other: "路径不存在"})}, false
			}
			c.reject(Violation{Reason: ReasonNotExist, Input: in, Path: abs})
			return Target{}, nil, false
		}
		c.reject(Violation{Reason: ReasonUnreadable, Input: in, Path: abs, Detail: err.Error()})
		return Target{}, nil, false
	}

	// 短名展开必须在所有路径比较之前：否则 PROGRA~1 这类写法能绕过护栏判定。
	expanded, err := platform.ExpandShortName(abs)
	if err != nil {
		c.reject(Violation{Reason: ReasonUnresolvedPath, Input: in, Path: abs, Detail: err.Error()})
		return Target{}, nil, false
	}

	return Target{Input: in, Path: expanded, Info: info}, nil, true
}

// 第 4、5 条：目标是 cwd 本身，或 cwd 的上级。
func (c *checker) checkCwdRelation(t Target) {
	if equalPath(t.Path, c.cwd) {
		c.reject(Violation{
			Reason: ReasonIsCwd, Input: t.Input, Path: t.Path,
			Detail: i18n.T(&goi18n.Message{ID: "DetailIsCwd", Other: "要删当前所在目录，请先 cd 到别处；确认无误可用 --allow-dangerous"}),
		})
		return
	}
	if isAncestor(t.Path, c.cwd) {
		c.reject(Violation{
			Reason: ReasonAncestorOfCwd, Input: t.Input, Path: t.Path,
			Detail: i18n.T(&goi18n.Message{ID: "DetailCwdIs", Other: "当前工作目录 {{.V}}"}, i18n.Data{"V": c.cwd}),
		})
	}
}

// 第 6 条：卷根。用 platform.VolumeRoot 而不是"取盘符"，才能正确识别
// 挂载到文件夹的卷，以及 UNC 共享根。
func (c *checker) checkVolumeRoot(t Target) {
	root, err := platform.VolumeRoot(t.Path)
	if err != nil {
		// 拿不到卷信息属于"无法可靠判定"，按拒绝处理（不猜）
		c.reject(Violation{Reason: ReasonUnresolvedPath, Input: t.Input, Path: t.Path, Detail: err.Error()})
		return
	}
	if equalPath(t.Path, root) {
		c.reject(Violation{Reason: ReasonVolumeRoot, Input: t.Input, Path: t.Path,
			Detail: i18n.T(&goi18n.Message{ID: "DetailVolumeMount", Other: "卷挂载点 {{.V}}"}, i18n.Data{"V": root})})
	}
}

// 第 7 条：系统与用户关键路径。
func (c *checker) checkProtected(t Target) {
	for _, p := range c.protected {
		if p.Subtree {
			if isInsideInclusive(t.Path, p.Path) {
				c.reject(Violation{
					Reason: ReasonProtectedPath, Input: t.Input, Path: t.Path,
					Detail: i18n.T(&goi18n.Message{ID: "DetailInsideProtected", Other: "位于受保护目录 {{.V}} 之内"}, i18n.Data{"V": p.Path}),
				})
				return
			}
			continue
		}
		if equalPath(t.Path, p.Path) {
			c.reject(Violation{
				Reason: ReasonProtectedPath, Input: t.Input, Path: t.Path,
				Detail: i18n.T(&goi18n.Message{ID: "DetailProtectedItself", Other: "受保护目录本身 {{.V}}"}, i18n.Data{"V": p.Path}),
			})
			return
		}
	}

	// 卷根下的系统目录（逐卷存在，无法靠环境变量枚举）
	root, err := platform.VolumeRoot(t.Path)
	if err != nil {
		return
	}
	for _, name := range volumeRootSystemNames {
		if equalPath(t.Path, filepath.Join(root, name)) {
			c.reject(Violation{
				Reason: ReasonProtectedPath, Input: t.Input, Path: t.Path,
				Detail: i18n.T(&goi18n.Message{ID: "DetailSystemDirOnVolume", Other: "卷根下的系统目录 {{.V}}"}, i18n.Data{"V": name}),
			})
			return
		}
	}
}

// 第 8 条：VCS 仓库根。目录"直接含" .git/.hg/.svn 即视为仓库根。
//
// 必须同时识别 .git 的两种形态：普通仓库里是目录，
// worktree 与 submodule 里是内容为 `gitdir: ...` 的普通文件。
// 只判目录会让 submodule 路径漏检。
func (c *checker) checkVCSRoot(t Target) {
	if !t.Info.IsDir() {
		return
	}
	for _, marker := range vcsMarkers {
		if equalPath(filepath.Base(t.Path), marker) {
			c.reject(Violation{
				Reason: ReasonVCSRoot, Input: t.Input, Path: t.Path,
				Detail: i18n.T(&goi18n.Message{ID: "DetailVCSMetadataDir", Other: "这是版本库元数据目录 {{.V}}"}, i18n.Data{"V": marker}),
			})
			return
		}
		child := filepath.Join(t.Path, marker)
		if _, err := os.Lstat(child); err == nil {
			c.reject(Violation{
				Reason: ReasonVCSRoot, Input: t.Input, Path: t.Path,
				Detail: i18n.T(&goi18n.Message{ID: "DetailVCSMetadataChild", Other: "目录含版本库元数据 {{.V}}，误删会丢掉未推送的内容"}, i18n.Data{"V": marker}),
			})
			return
		}
	}
}

// 第 12 条：同一批参数里存在上下级关系。
// 移动上级时下级已经跟着走了，第二项只会变成"路径不存在"，属于调用方搞错了意图。
func (c *checker) checkNested(targets []Target) {
	for i := range targets {
		for j := range targets {
			if i == j {
				continue
			}
			if isAncestor(targets[i].Path, targets[j].Path) {
				c.reject(Violation{
					Reason: ReasonNestedOperand, Input: targets[i].Input, Path: targets[i].Path,
					Detail: i18n.T(&goi18n.Message{ID: "DetailNestedAncestor", Other: "它是 {{.V}} 的上级，移动它会连带把后者一起搬走"}, i18n.Data{"V": strconv.Quote(targets[j].Input)}),
				})
			}
		}
	}
}

// CheckTrashRelation 实现 §5.1 第 9、10 条：目标与回收目录的互为祖先关系。
//
// ok=false 表示无冲突。返回的 Violation 交给 Report.Add 决定是拒绝还是警告。
//
// 必须在解析出**最终**回收根之后调用（设计文档 §7.4）：
// 只比较配置里的原始字符串会被 `roots.D = "D:\projects\trash"` + `saferm D:\projects` 绕过。
func CheckTrashRelation(target, trashRoot string) (Violation, bool) {
	if trashRoot == "" {
		return Violation{}, false
	}
	if isInsideInclusive(target, trashRoot) {
		return Violation{
			Reason: ReasonInsideTrash, Path: target,
			Detail: i18n.T(&goi18n.Message{ID: "DetailInsideTrash", Other: "回收目录 {{.V}}"}, i18n.Data{"V": trashRoot}),
		}, true
	}
	if isAncestor(target, trashRoot) {
		return Violation{
			Reason: ReasonAncestorOfTrash, Path: target,
			Detail: i18n.T(&goi18n.Message{ID: "DetailTrashInsideTarget", Other: "回收目录位于该目标之内：{{.V}}"}, i18n.Data{"V": trashRoot}),
		}, true
	}
	return Violation{}, false
}

// resolveCwd 取并规范化当前工作目录。
// cwd 自己也要做短名展开，否则在短名路径下启动时，第 4、5 条会被绕过。
func resolveCwd(explicit string) (string, error) {
	cwd := explicit
	if cwd == "" {
		wd, err := os.Getwd()
		if err != nil {
			return "", fmt.Errorf("get working directory: %w", err)
		}
		cwd = wd
	}
	abs, err := platform.AbsClean(cwd)
	if err != nil {
		return "", err
	}
	if expanded, err := platform.ExpandShortName(abs); err == nil {
		return expanded, nil
	}
	return abs, nil
}
