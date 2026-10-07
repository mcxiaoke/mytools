package main

import (
	"fmt"
	"sort"
	"strings"

	"saferm/internal/config"
	"saferm/internal/guard"
	"saferm/internal/ui"
	"saferm/internal/volume"
)

// 本文件是"配置 → 各模块"的装配层（设计文档 §7.1）：
//
//	命令行参数  >  配置文件  >  内置默认值
//
// 配置只在这一处被翻译成 guard / volume / ui 的入参，避免同一项在各处
// 各读一次导致言行不一。

// effectiveConfig 取得本次运行生效的配置与实际读到的文件路径（没有则为空）。
//
// app.loadConfig 为 nil 时读真实磁盘；测试注入它以保证用例不受开发机上的
// %APPDATA%\saferm\config.toml 影响。
func (a *app) effectiveConfig() (config.Config, string, error) {
	load := a.loadConfig
	if load == nil {
		load = config.Load
	}
	return load(a.configPath)
}

// guardOptions 把配置与命令行开关合成为 guard 的选项。
//
// 配置里的 [guard] 段只能在本工具给定的几个开关上收紧或放松；
// extra_protected 是**追加**保护项，永远无法移除内置保护（设计文档 §7.2）。
func guardOptions(cfg config.Config, f *removeFlags) guard.Options {
	return guard.Options{
		IgnoreMissing:  f.force,
		AllowDangerous: f.allowDangerous,
		ExtraProtected: cfg.Guard.ExtraProtected,
		// 反向命名：零值等于"开启检查"，所以这里取反
		DisableVCSRootCheck: !cfg.Guard.ProtectVCSRoot,
	}
}

// confirmConfig 把 [confirm] 段交给 ui 决定确认级别。
func confirmConfig(cfg config.Config) ui.ConfirmConfig {
	return ui.ConfirmConfig{
		FileThreshold:       cfg.Confirm.FileThreshold,
		DangerFileThreshold: cfg.Confirm.DangerFileThreshold,
		BytesThreshold:      cfg.Confirm.BytesThreshold,
		AlwaysConfirmDir:    cfg.Confirm.AlwaysConfirmDir,
	}
}

// scanLimits 决定扫描的早退上限：拿危险级阈值当上限，
// 数到那儿就够判断"要不要升为危险级"了，不必把几十万文件数完（设计文档 §6.1）。
//
// 上限为 0 表示"该维度不设限"，ScanLimits 自己按 > 0 判断。
func scanLimits(cc ui.ConfirmConfig) ui.ScanLimits {
	return ui.ScanLimits{MaxFiles: cc.DangerFileThreshold, MaxBytes: cc.BytesThreshold}
}

// volumeConfig 把 [trash] 段交给 volume。
//
// --trash-root 优先级最高：按"覆盖回收根"的语义，它同时忽略 [trash.roots]，
// 否则"命令行覆盖"会被按卷配置架空（设计文档 §7.2 的优先级表）。
func volumeConfig(cfg config.Config, version, trashRootFlag string) volume.Config {
	vc := volume.Config{Version: version}
	if v := strings.TrimSpace(trashRootFlag); v != "" {
		vc.DefaultRoot = v
		return vc
	}
	vc.DefaultRoot = strings.TrimSpace(cfg.Trash.DefaultRoot)
	vc.Roots = cfg.Trash.Roots
	return vc
}

// effectiveConfigLines 生成 `saferm where` 里的"生效配置"摘要。
//
// 这一段的用处不只是好看：它是"配置到底读进去没有"的可见证据。
// 本工具最危险的失败模式就是"用户以为护栏生效了、其实配置根本没读进去"，
// 所以把生效值明确摆出来，比任何文档都直接。
func effectiveConfigLines(cfg config.Config) []string {
	lines := []string{
		cfgLine("trash.default_root", "%s", orDefault(cfg.Trash.DefaultRoot, "(空)")),
	}
	if len(cfg.Trash.Roots) == 0 {
		lines = append(lines, cfgLine("trash.roots", "未配置（各卷统一用 <卷根>/.saferm-trash）"))
	} else {
		lines = append(lines, cfgLine("trash.roots", "按卷覆盖："))
		keys := make([]string, 0, len(cfg.Trash.Roots))
		for k := range cfg.Trash.Roots {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		for _, k := range keys {
			lines = append(lines, cfgLine("", "%s = %s", k, cfg.Trash.Roots[k]))
		}
	}

	lines = append(lines,
		cfgLine("confirm.file_threshold", "%s（超过即需确认；0 表示不按此项判断）",
			ui.HumanCount(cfg.Confirm.FileThreshold)),
		cfgLine("confirm.danger_file_threshold", "%s（超过即升为危险级）",
			ui.HumanCount(cfg.Confirm.DangerFileThreshold)),
		cfgLine("confirm.bytes_threshold", "%s / %d 字节",
			ui.HumanBytes(cfg.Confirm.BytesThreshold, false), cfg.Confirm.BytesThreshold),
		cfgLine("confirm.always_confirm_dir", "%s", yesNo(cfg.Confirm.AlwaysConfirmDir)),
		cfgLine("guard.protect_vcs_root", "%s", yesNo(cfg.Guard.ProtectVCSRoot)),
		cfgLine("guard.git_detect", "%s", yesNo(cfg.Guard.GitDetect)),
	)
	if len(cfg.Guard.ExtraProtected) == 0 {
		lines = append(lines, cfgLine("guard.extra_protected", "无（内置保护项始终生效，配置只能追加）"))
	} else {
		lines = append(lines, cfgLine("guard.extra_protected", "%s",
			strings.Join(cfg.Guard.ExtraProtected, " / ")))
	}
	return lines
}

// configKeyWidth 是生效配置里键名的列宽，保证值列对齐。
// 取最长键 confirm.danger_file_threshold（28 字符）再加两个空格。
const configKeyWidth = 30

func cfgLine(key, format string, args ...any) string {
	return fmt.Sprintf("%-*s%s", configKeyWidth, key, fmt.Sprintf(format, args...))
}

func orDefault(s, fallback string) string {
	if s == "" {
		return fallback
	}
	return s
}

func yesNo(b bool) string {
	if b {
		return "是"
	}
	return "否"
}
