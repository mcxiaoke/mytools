// Package volume 负责"这次删除的落脚点在哪"：按目标所在的卷解析出回收根，
// 并在动手之前完成设计文档 §7.4 的全部校验。
//
// 它与 guard 的分工：guard 判断"这个目标该不该删"，volume 判断
// "删掉之后能不能安全地放"。两者都在任何文件系统写操作之前跑完；本包**不创建**任何
// 目录（dry-run 必须零副作用），需要创建时只返回 NeedCreate=true，由调用方在确认后创建。
package volume

import (
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"

	"saferm/internal/guard"
	"saferm/internal/platform"
)

// DefaultTrashDirName 是默认回收根的名字（位于卷根下）。
const DefaultTrashDirName = ".saferm-trash"

// AutoRoot 是 DefaultRoot 的特殊值：按目标所在卷的根推导。
const AutoRoot = "auto"

// Config 是解析回收根所需的配置片段（完整配置在 internal/config 里组装）。
//
// 零值即安全默认：按卷根推导、启用同卷兜底、不忽略任何校验。
type Config struct {
	// DefaultRoot 为 "" 或 "auto" 时按 `<卷根>/.saferm-trash` 推导；
	// 其它值视为显式路径（必须与目标同卷，否则拒绝）。
	DefaultRoot string

	// Roots 按卷覆盖，键为 Key(卷挂载点) 的结果。
	Roots map[string]string

	// DisableFallback 关闭"卷根不可写时退到用户目录下的同设备候选"。
	// 反向命名是为了让零值等于"启用兜底"（设计文档 §7.3 第 3 步）。
	DisableFallback bool

	// Version 是要写进标记文件的工具版本。
	Version string
}

// Key 把卷挂载点映射成配置里使用的键。
//
// Windows：`D:\` → `D`；UNC `\\server\share\` → `\\SERVER\SHARE`（大小写不敏感比较）。
// Linux：挂载点原样，例如 `/`、`/home`、`/data`。
func Key(volumeRoot string) string {
	if runtime.GOOS == "windows" {
		r := strings.TrimRight(volumeRoot, `\/`)
		if len(r) >= 2 && r[1] == ':' {
			return strings.ToUpper(r[:1])
		}
		return strings.ToUpper(r)
	}
	r := strings.TrimRight(volumeRoot, "/")
	if r == "" {
		return "/"
	}
	return r
}

// Placement 描述"某个目标要被搬到哪里去"。
type Placement struct {
	Target     string
	VolumeRoot string
	TrashRoot  string
	RootSource string // config / default / fallback
	Fallback   bool
	// NeedCreate 表示回收根尚不存在，调用方在确认之后需要创建它并写入标记。
	NeedCreate bool
}

// rootChoice 是"某个卷上选定哪个回收根"的解析结果（与具体目标无关）。
type rootChoice struct {
	root       string
	source     string
	fallback   bool
	needCreate bool
	problem    string // 非空表示该卷没有任何可用回收根
}

// Resolve 为每个目标解析回收根，并完成 §7.4 的校验。
//
// 返回值中的 violations 交给调用方计入 guard.Report 一起汇报；error 只用于
// 工具自身出问题（如取不到卷信息之外的意外）。
func Resolve(targets []string, cfg Config) ([]Placement, guard.Violations, error) {
	var plans []Placement
	var viols guard.Violations

	cache := map[string]rootChoice{}

	for _, target := range targets {
		volRoot, err := platform.VolumeRoot(target)
		if err != nil {
			viols = append(viols, guard.Violation{
				Reason: guard.ReasonUnresolvedPath, Path: target, Detail: err.Error(),
			})
			continue
		}

		choice, ok := cache[volRoot]
		if !ok {
			choice = resolveRootForVolume(volRoot, cfg)
			cache[volRoot] = choice
		}
		if choice.problem != "" {
			viols = append(viols, guard.Violation{
				Reason: guard.ReasonTrashUnusable, Path: target, Detail: choice.problem,
			})
			continue
		}

		// 与目标实际同卷：用 SameVolume 而不是比较挂载点字符串，
		// 这样 bind mount、网络盘等情形也不会漏判。
		same, err := platform.SameVolume(target, choice.root)
		if err != nil {
			viols = append(viols, guard.Violation{
				Reason: guard.ReasonUnresolvedPath, Path: target, Detail: err.Error(),
			})
			continue
		}
		if !same {
			viols = append(viols, guard.Violation{
				Reason: guard.ReasonCrossVolume, Path: target,
				Detail: fmt.Sprintf("目标在 %s，回收目录 %s 在 %s",
					volRoot, choice.root, mustVolumeRoot(choice.root)),
			})
			continue
		}

		// §5.1 第 9、10 条：目标与回收根的互为祖先关系，必须针对
		// **最终解析出的回收根**做，而不是配置里的原始字符串。
		if v, bad := guard.CheckTrashRelation(target, choice.root); bad {
			viols = append(viols, v)
			continue
		}

		plans = append(plans, Placement{
			Target:     target,
			VolumeRoot: volRoot,
			TrashRoot:  choice.root,
			RootSource: choice.source,
			Fallback:   choice.fallback,
			NeedCreate: choice.needCreate,
		})
	}

	return plans, viols, nil
}

// resolveRootForVolume 按 §7.3 的顺序为某个卷挑一个可用回收根。
//
// 关键取舍：**显式配置的回收根不会降级到别处**。如果用户为某卷指定了路径，
// 而它不可用，我们只报错。否则"配置写错了"会被静默掩盖 —— 用户以为文件进了
// 自己指定的位置，实际却被丢到盘根或用户目录里，这与本工具的存在意义相悖。
// 兜底候选只在"用的是推导出来的默认位置"时才参与。
func resolveRootForVolume(volRoot string, cfg Config) rootChoice {
	type candidate struct {
		path     string
		source   string
		fallback bool
	}

	var candidates []candidate
	switch {
	case strings.TrimSpace(cfg.Roots[Key(volRoot)]) != "":
		candidates = append(candidates, candidate{strings.TrimSpace(cfg.Roots[Key(volRoot)]), "config", false})

	case strings.TrimSpace(cfg.DefaultRoot) != "" && !strings.EqualFold(cfg.DefaultRoot, AutoRoot):
		candidates = append(candidates, candidate{strings.TrimSpace(cfg.DefaultRoot), "default", false})

	default:
		candidates = append(candidates, candidate{filepath.Join(volRoot, DefaultTrashDirName), "default", false})
		if !cfg.DisableFallback {
			if p := fallbackRoot(volRoot); p != "" {
				candidates = append(candidates, candidate{p, "fallback", true})
			}
		}
	}

	var problems []string
	for _, cd := range candidates {
		choice, ok := inspectCandidate(cd.path, cd.source, cd.fallback)
		if ok {
			return choice
		}
		problems = append(problems, fmt.Sprintf("%s（%s）", cd.path, choice.problem))
	}
	// 全部候选都不可用时，仍把首选候选路径带回去：`where` 与报错都需要展示
	// "本来打算用哪个目录"，否则用户不知道该去修哪个配置。
	primary, _ := platform.AbsClean(candidates[0].path)
	return rootChoice{
		root:    primary,
		source:  candidates[0].source,
		problem: "该卷上没有可用的回收目录：" + strings.Join(problems, "；"),
	}
}

// inspectCandidate 检查一个候选回收根是否可用（§4.7 + §7.4）。
//
// 只读，不创建任何东西。
func inspectCandidate(root, source string, fallback bool) (rootChoice, bool) {
	choice := rootChoice{root: root, source: source, fallback: fallback}

	abs, err := platform.AbsClean(root)
	if err != nil {
		choice.problem = "路径无法规范化：" + err.Error()
		return choice, false
	}
	choice.root = abs

	// 回收根本身不能是卷根：那等于往盘根倒文件，配置写错时必须拦住
	if vr, err := platform.VolumeRoot(abs); err == nil && samePath(vr, abs) {
		choice.problem = "它是卷根，不能用作回收目录"
		return choice, false
	}

	info, err := os.Lstat(abs)
	if err != nil {
		if os.IsNotExist(err) {
			choice.needCreate = true
			return choice, true
		}
		choice.problem = "无法访问：" + err.Error()
		return choice, false
	}
	if !info.IsDir() {
		choice.problem = "它不是目录"
		return choice, false
	}

	entries, err := os.ReadDir(abs)
	if err != nil {
		choice.problem = "无法读取目录内容：" + err.Error()
		return choice, false
	}
	if len(entries) == 0 {
		// 空目录可以认领（§4.7 规则 2）
		return choice, true
	}
	if _, ok, err := ReadMarker(abs); err != nil {
		choice.problem = err.Error()
		return choice, false
	} else if !ok {
		choice.problem = "目录非空且没有 " + MarkerName + " 标记，可能是配置写错的已有目录"
		return choice, false
	}
	return choice, true
}

// fallbackRoot 给出"卷根不可写时"的同设备候选（§7.3 第 3 步）。
//
// 它只是候选，能否真正使用仍由 SameVolume 判定：不同设备一律拒绝，
// 因为跨卷意味着要"复制+删除"，那是本工具刻意不做的事。
func fallbackRoot(volRoot string) string {
	key := Key(volRoot)
	if runtime.GOOS == "windows" {
		if base := os.Getenv("LOCALAPPDATA"); base != "" {
			return filepath.Join(base, "saferm", "trash", key)
		}
		return ""
	}
	base := os.Getenv("XDG_DATA_HOME")
	if base == "" {
		if home := os.Getenv("HOME"); home != "" {
			base = filepath.Join(home, ".local", "share")
		}
	}
	if base == "" {
		return ""
	}
	return filepath.Join(base, "saferm", "trash", sanitizeMountKey(key))
}

// sanitizeMountKey 把挂载点变成安全的单层目录名：`/data` → `data`，`/` → `root`。
func sanitizeMountKey(mount string) string {
	s := strings.Trim(mount, "/")
	if s == "" {
		return "root"
	}
	return strings.NewReplacer("/", "_", ":", "_").Replace(s)
}

// mustVolumeRoot 是给错误描述用的尽力而为版本：拿不到就说明拿不到，不阻断主流程。
func mustVolumeRoot(p string) string {
	if vr, err := platform.VolumeRoot(p); err == nil {
		return vr
	}
	return "未知卷"
}

// samePath 是比较两个绝对路径是否指向同一位置（大小写规则交给平台处理）。
func samePath(a, b string) bool {
	if runtime.GOOS == "windows" {
		return strings.EqualFold(filepath.Clean(a), filepath.Clean(b))
	}
	return filepath.Clean(a) == filepath.Clean(b)
}
