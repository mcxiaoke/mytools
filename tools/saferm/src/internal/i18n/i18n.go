// Package i18n 提供中英双语的用户可见文案。
//
// 源语言是中文：调用点写 &goi18n.Message{ID: ..., Other: "中文默认文案"}
// （goi18n 是 go-i18n 本体包的 import 别名）并交给本包的 T 本地化；
// `goi18n extract` 会把全部消息抽取到 locales/active.zh-CN.toml，
// 英文翻译维护在 locales/en.toml。两份语言文件经 go:embed 内嵌进二进制，
// 运行时不依赖任何外部文件。
//
// 之所以要求调用点直接 import go-i18n 本体：goi18n extract 只在 import 了
// 该包的文件里识别 Message 字面量（按 import 路径判定），本包的别名包装
// 无法被它识别。
//
// 语言选择（main 启动时调用 Init）：
//  1. SAFERM_LANG 环境变量（zh / en / zh-CN / en-US 等；用于测试与强制指定）；
//  2. Windows：用户首选 UI 语言（显示语言），中文系统 → 中文；
//  3. 其它平台：LANGUAGE / LC_ALL / LC_MESSAGES / LANG；
//  4. 非中文一律英文。
//
// 未调用 Init（例如单元测试直接驱动各包）时默认中文，即源语言。
package i18n

import (
	"embed"
	"errors"
	"os"
	"strings"
	"sync"

	gi18n "github.com/nicksnyder/go-i18n/v2/i18n"
	"github.com/pelletier/go-toml/v2"
	"golang.org/x/text/language"
)

// Data 是消息模板数据的简写：T(m, i18n.Data{"Count": "1,024"})。
type Data = map[string]any

//go:embed locales/*.toml
var localeFS embed.FS

const defaultLang = "zh-CN"

var (
	mu        sync.Mutex
	bundle    *gi18n.Bundle
	localizer *gi18n.Localizer
	lang      = defaultLang
)

func init() {
	bundle = gi18n.NewBundle(language.MustParse(defaultLang))
	// go-i18n v2 只内置 JSON 解码，TOML 需显式注册（go-toml 已是项目依赖）
	bundle.RegisterUnmarshalFunc("toml", toml.Unmarshal)
	// 解析失败只可能是内嵌资源本身损坏（构建期就已确定），panic 合理
	for _, name := range []string{"locales/active.zh-CN.toml", "locales/en.toml"} {
		if _, err := bundle.LoadMessageFileFS(localeFS, name); err != nil {
			panic(err)
		}
	}
	resetLocalizer(defaultLang)
}

func resetLocalizer(l string) {
	matcher := language.NewMatcher(bundle.LanguageTags())
	tag, _, _ := matcher.Match(language.MustParse(l))
	localizer = gi18n.NewLocalizer(bundle, tag.String())
	lang = tag.String()
}

// SetLang 显式指定语言（zh-CN / en 等，大小写与变体不敏感）。
func SetLang(l string) {
	mu.Lock()
	defer mu.Unlock()
	l = strings.TrimSpace(l)
	if l == "" {
		l = defaultLang
	}
	if strings.HasPrefix(strings.ToLower(l), "zh") {
		l = "zh-CN"
	}
	resetLocalizer(l)
}

// Lang 返回当前语言标签。
func Lang() string {
	mu.Lock()
	defer mu.Unlock()
	return lang
}

// Init 按环境自动选择语言：SAFERM_LANG 优先，否则检测系统语言。
func Init() {
	if v := strings.TrimSpace(os.Getenv("SAFERM_LANG")); v != "" && !strings.EqualFold(v, "auto") {
		SetLang(v)
		return
	}
	SetLang(Detect())
}

// T 把消息本地化为当前语言。
//
// args 里可以混传：
//   - Data（模板数据，消息里用 {{.Field}} 引用）；
//   - int / int64（作为复数规则计数，配合消息里的 one/other 形式）。
//
// 本地化失败（缺少键、模板字段不匹配等）时回退到消息自带的中文原文，
// 绝不 panic：报错文案的失败不能变成程序崩溃。
func T(m *gi18n.Message, args ...any) string {
	cfg := &gi18n.LocalizeConfig{DefaultMessage: m}
	for _, a := range args {
		switch v := a.(type) {
		case Data:
			cfg.TemplateData = v
		case int:
			cfg.PluralCount = v
		case int64:
			cfg.PluralCount = v
		}
	}
	mu.Lock()
	loc := localizer
	mu.Unlock()
	s, err := loc.Localize(cfg)
	if err != nil {
		return m.Other
	}
	return s
}

// E 是 T 的 error 便捷形式，用于 fmt.Errorf 的替换场景。
//
// 注意它不保留 %w 包装链：现有调用方只展示错误、从不对这些文案做
// errors.Is/As 解包，因此丢失包装链不影响行为。
func E(m *gi18n.Message, args ...any) error {
	return errors.New(T(m, args...))
}

// LocalizedError 把一条消息包成动态本地化的 error：
// Error() 在渲染时才取当前语言。同一实例可被 errors.Is 识别。
func LocalizedError(m *gi18n.Message) error {
	return &localizedError{m: m}
}

type localizedError struct {
	m *gi18n.Message
}

func (e *localizedError) Error() string { return T(e.m) }
