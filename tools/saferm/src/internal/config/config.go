// Package config 负责加载 saferm 的配置文件。
//
// 两条硬规则（设计文档 §7.1）：
//   - 文件**不存在** → 用内置默认值，不报错；
//   - 文件存在但**解析失败**（语法错误、类型不符），或**键名写错** → 报错退出，
//     绝不静默回退默认值。否则用户以为护栏生效了、其实配置根本没读进去，
//     这是本工具最危险的失败模式。
//
// 因此这里不自己写 TOML 解析器（那是最不值得自己造、又最容易静默出错的轮子），
// 直接用成熟的 go-toml/v2，并打开 DisallowUnknownFields（strict 模式）。
//
// 为什么是 go-toml/v2 而不是 BurntSushi/toml（两个都实测过，见 §10.1）：
// go-toml/v2 在 strict 模式与普通解码错误下，String() 都会渲染出
// **行号 + 原文片段 + 出错位置的下划线**，正好满足 §7.1「报错要带行号与原文片段」；
// BurntSushi 的 Undecoded() 只能给出键名，无法定位到行。例：
//
//	1| [trash]
//	2| default_rooot = "auto"
//	 | ~~~~~~~~~~~~~ unknown field
package config

import (
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"

	"github.com/pelletier/go-toml/v2"
)

// Config 是配置文件的结构，与设计文档 §7.2 一一对应。
//
// 刻意保持项数极少：配置项越多，"用户以为改了其实没生效"的面就越大。
type Config struct {
	Trash   TrashSection   `toml:"trash"`
	Confirm ConfirmSection `toml:"confirm"`
	Guard   GuardSection   `toml:"guard"`
}

// TrashSection 决定回收目录放在哪。
type TrashSection struct {
	// DefaultRoot 为 "auto" 或空表示按目标所在卷推导 `<卷根>/.saferm-trash`。
	DefaultRoot string `toml:"default_root"`
	// Roots 按卷覆盖。键是卷标识：Windows 用盘符字母（C），Linux 用挂载点（/home）。
	Roots map[string]string `toml:"roots"`
}

// ConfirmSection 是确认级别的阈值（设计文档 §6.1）。
type ConfirmSection struct {
	FileThreshold       int64 `toml:"file_threshold"`
	DangerFileThreshold int64 `toml:"danger_file_threshold"`
	BytesThreshold      int64 `toml:"bytes_threshold"`
	AlwaysConfirmDir    bool  `toml:"always_confirm_dir"`
}

// GuardSection 控制护栏的开关。注意这里只能"追加"保护项、不能移除内置项。
type GuardSection struct {
	// ProtectVCSRoot 为 true 时，目录含 .git/.hg/.svn 直接拒绝。
	ProtectVCSRoot bool `toml:"protect_vcs_root"`
	// GitDetect 为 true 时，目标在版本库工作区内会升为危险级。
	GitDetect bool `toml:"git_detect"`
	// ExtraProtected 是额外受保护的绝对路径（按子树保护）。
	ExtraProtected []string `toml:"extra_protected"`
}

// Default 返回内置默认值：即"没有任何配置文件"时的行为。
func Default() Config {
	return Config{
		Trash: TrashSection{
			DefaultRoot: "auto",
		},
		Confirm: ConfirmSection{
			FileThreshold:       50,
			DangerFileThreshold: 5000,
			BytesThreshold:      1 << 30, // 1 GiB
			AlwaysConfirmDir:    true,
		},
		Guard: GuardSection{
			ProtectVCSRoot: true,
			GitDetect:      true,
		},
	}
}

// Parse 从 r 读取并解析配置；未出现的字段保留 Default() 的值。
//
// 出错时返回 **Default() 与 error**，而不是零值 Config：零值 Config 的阈值全为 0、
// always_confirm_dir 为 false，相当于"不再追问"，一旦哪个调用方漏检 error，
// 就会静默退化成最不安全的配置。返回默认值 + 明确 error 至少把后果限制在"照旧"。
// **调用方必须先检查 error，不要在出错时使用返回值。**
func Parse(r io.Reader) (Config, error) {
	cfg := Default()

	dec := toml.NewDecoder(r)
	// 键名写错必须报错：拼错一个键而工具"照常工作"，比直接失败危险得多
	dec.DisallowUnknownFields()
	if err := dec.Decode(&cfg); err != nil {
		return Default(), fmt.Errorf("解析配置文件失败：%s", explain(err))
	}
	return cfg, nil
}

// explain 把 go-toml 的错误渲染成"行号 + 原文片段"的定位信息（设计文档 §7.1）。
func explain(err error) string {
	var strict *toml.StrictMissingError
	if errors.As(err, &strict) {
		// strict 模式的报错本身就是一段带行号的定位，直接透传
		return "有无法识别的配置项（键名是不是拼错了？）：\n" + strict.String()
	}
	var dec *toml.DecodeError
	if errors.As(err, &dec) {
		// 语法错误 / 类型不符：String() 自带行号与出错位置
		return "语法或类型不符：\n" + dec.String()
	}
	return err.Error()
}

// DefaultPath 返回默认的配置文件位置。
//
// 不做"向上级目录查找"这类项目级配置：那会让"在哪个目录执行行为就不同"，
// 属于难以察觉的差异（设计文档 §7.1）。
func DefaultPath() string {
	return defaultPath(runtime.GOOS, os.Getenv("APPDATA"), os.Getenv("XDG_CONFIG_HOME"), os.Getenv("HOME"))
}

// defaultPath 是 DefaultPath 的纯函数内核：把平台与环境**当参数传进来**，
// 这样 Windows 与 Linux 两条分支在任意机器上都能被测试覆盖到，
// 不必依赖"恰好有台 Linux 机器"来验证（此前 Linux 分支在 Windows 上只能跳过）。
func defaultPath(goos, appData, xdgConfigHome, home string) string {
	if goos == "windows" {
		if appData != "" {
			return filepath.Join(appData, "saferm", "config.toml")
		}
		return ""
	}
	if xdgConfigHome != "" {
		return filepath.Join(xdgConfigHome, "saferm", "config.toml")
	}
	if home != "" {
		return filepath.Join(home, ".config", "saferm", "config.toml")
	}
	return ""
}

// Load 按 explicitPath 加载配置。
//
//   - explicitPath 为空 → 用 DefaultPath()；文件不存在则返回默认值与空路径
//     （视为"没有配置文件"，这不是错误）。
//   - explicitPath 非空 → 文件必须存在，不存在即报错
//     （用户是显式指定的，不能装作没看见）。
//
// 返回的 used 是实际读取的文件路径（没有配置文件时为空），用于 `where` 展示。
// 出错时同样返回 Default()（理由见 Parse）。
func Load(explicitPath string) (cfg Config, used string, err error) {
	path := explicitPath
	if path == "" {
		path = DefaultPath()
		if path == "" {
			// 连默认位置都推不出来（环境变量缺失）：当作"没有配置"
			return Default(), "", nil
		}
		if _, statErr := os.Stat(path); statErr != nil {
			if os.IsNotExist(statErr) {
				return Default(), "", nil
			}
			return Default(), "", fmt.Errorf("检查配置文件 %q 失败：%w", path, statErr)
		}
	} else if _, statErr := os.Stat(path); statErr != nil {
		if os.IsNotExist(statErr) {
			return Default(), "", fmt.Errorf("指定的配置文件不存在：%s", path)
		}
		return Default(), "", fmt.Errorf("检查配置文件 %q 失败：%w", path, statErr)
	}

	f, err := os.Open(path)
	if err != nil {
		return Default(), "", fmt.Errorf("打开配置文件 %q 失败：%w", path, err)
	}
	defer f.Close()

	cfg, err = Parse(f)
	if err != nil {
		return Default(), "", fmt.Errorf("%s：%w", path, err)
	}
	return cfg, path, nil
}
