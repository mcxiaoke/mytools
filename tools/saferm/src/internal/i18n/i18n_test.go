package i18n

import (
	"os"
	"testing"

	gi18n "github.com/nicksnyder/go-i18n/v2/i18n"
	"github.com/pelletier/go-toml/v2"
)

// TestSetLangSwitchesMessages 验证语言切换后 T 输出对应语言。
func TestSetLangSwitchesMessages(t *testing.T) {
	m := &gi18n.Message{ID: "KindFile", Other: "文件"}

	SetLang("zh-CN")
	if got := T(m); got != "文件" {
		t.Fatalf("zh-CN: KindFile = %q, want 文件", got)
	}

	SetLang("en")
	if got := T(m); got != "File" {
		t.Fatalf("en: KindFile = %q, want File", got)
	}

	SetLang("zh-TW") // zh 系变体统一映射到 zh-CN
	if got := T(m); got != "文件" {
		t.Fatalf("zh-TW: KindFile = %q, want 文件", got)
	}

	SetLang(defaultLang)
}

// TestFallbackToSourceMessage 验证翻译缺失时回退到消息自带的中文原文，
// 且绝不 panic。
func TestFallbackToSourceMessage(t *testing.T) {
	SetLang("en")
	defer SetLang(defaultLang)

	m := &gi18n.Message{ID: "KeyThatDoesNotExistAnywhere", Other: "中文原文"}
	if got := T(m); got != "中文原文" {
		t.Fatalf("missing key: T = %q, want 中文原文", got)
	}
}

// TestPluralForms 验证英文复数形式随计数切换。
func TestPluralForms(t *testing.T) {
	SetLang("en")
	defer SetLang(defaultLang)

	m := &gi18n.Message{ID: "SummaryFiles", Other: "{{.Count}} 个文件"}
	if got := T(m, Data{"Count": "1"}, 1); got != "1 file" {
		t.Fatalf("plural one: T = %q, want %q", got, "1 file")
	}
	if got := T(m, Data{"Count": "2"}, 2); got != "2 files" {
		t.Fatalf("plural other: T = %q, want %q", got, "2 files")
	}

	SetLang("zh-CN")
	if got := T(m, Data{"Count": "1"}, 1); got != "1 个文件" {
		t.Fatalf("zh plural: T = %q, want %q", got, "1 个文件")
	}
}

// TestTemplateData 验证模板数据插值。
func TestTemplateData(t *testing.T) {
	SetLang("en")
	defer SetLang(defaultLang)

	m := &gi18n.Message{ID: "MovedTo", Other: "已移动 {{.Target}}\n     → {{.Dest}}"}
	got := T(m, Data{"Target": "a.txt", "Dest": "/trash/a.txt"})
	want := "Moved a.txt\n     → /trash/a.txt"
	if got != want {
		t.Fatalf("T = %q, want %q", got, want)
	}
}

// TestLocaleFilesHaveSameKeys 保证两个语言文件的键集一致：
// 抽取生成的 zh-CN 与手工维护的 en 一旦漂移，这里直接失败。
func TestLocaleFilesHaveSameKeys(t *testing.T) {
	keys := map[string]map[string]bool{}
	for _, name := range []string{"locales/active.zh-CN.toml", "locales/en.toml"} {
		data, err := os.ReadFile(name)
		if err != nil {
			t.Fatalf("read %s: %v", name, err)
		}
		var doc map[string]any
		if err := toml.Unmarshal(data, &doc); err != nil {
			t.Fatalf("unmarshal %s: %v", name, err)
		}
		keys[name] = flattenKeys(doc)
	}

	zh := keys["locales/active.zh-CN.toml"]
	en := keys["locales/en.toml"]
	for k := range zh {
		if !en[k] {
			t.Errorf("key %q exists in zh-CN but missing in en", k)
		}
	}
	for k := range en {
		if !zh[k] {
			t.Errorf("key %q exists in en but missing in zh-CN", k)
		}
	}
}

// flattenKeys 返回消息 ID 的集合（只看顶层键）：
// 复数消息（[Key] 表 with one/other）与其简单形式一样，都只算一个 ID ——
// 两个语言文件对同一条消息允许分别用简单形式与复数形式（zh 无复数）。
func flattenKeys(doc map[string]any) map[string]bool {
	out := map[string]bool{}
	for k := range doc {
		out[k] = true
	}
	return out
}
