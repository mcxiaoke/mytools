package config

import (
	"reflect"
	"strings"
	"testing"

	"github.com/pelletier/go-toml/v2"
)

// FuzzParse 喂随机字节，断言三件事（设计文档 §11）：
//
//  1. **绝不 panic**（fuzz 会把 panic 直接算作失败）；
//  2. 要么解析成功、要么给出**明确的报错**——不存在"悄悄半解析"的中间态；
//  3. 报错时返回值必须是 Default()：这条在随机输入下几乎每轮都会被执行，
//     正好把"配置读不进去也不能退化成最不设防的零值配置"这个契约钉死。
//
// 解析成功时再补一条往返不变量：Marshal → Parse 必须成功且与原值相等。
// 它能发现"解析成功但结构是半截的"这类静默错误。
func FuzzParse(f *testing.F) {
	seeds := []string{
		"",
		"# 注释\n",
		// 设计文档 §7.2 的样例
		`[trash]
default_root = "auto"

[trash.roots]
C = "C:\\Users\\me\\AppData\\Local\\saferm\\trash\\C"
D = "D:\\.saferm-trash"
"/home" = "/data/.saferm-trash"

[confirm]
file_threshold       = 50
danger_file_threshold = 5000
bytes_threshold      = 1073741824
always_confirm_dir   = true

[guard]
protect_vcs_root = true
git_detect       = true
extra_protected  = []
`,
		// 典型错误
		"[tras]\nx = 1\n",
		"[confirm]\nfile_threshold = \"五十\"\n",
		"[trash\ndefault_root = \"auto\"\n",
		"[trash.roots]\n\"a.b\" = \"c\"\n",
		"[guard]\nextra_protected = [\"C:\\\\Windows\"]\n",
	}
	for _, s := range seeds {
		f.Add(s)
	}

	f.Fuzz(func(t *testing.T, src string) {
		cfg, err := Parse(strings.NewReader(src))

		if err != nil {
			if err.Error() == "" {
				t.Fatal("报错信息不能为空：用户必须知道哪里出了问题")
			}
			// 出错时必须给默认值，而不是零值配置（零值等于关掉确认）
			if !reflect.DeepEqual(cfg, Default()) {
				t.Fatalf("出错时应返回 Default()，实际 %+v\n输入：%q", cfg, src)
			}
			return
		}

		// 解析成功：同样的输入解析两次必须完全一致（不允许有隐藏状态）
		again, err := Parse(strings.NewReader(src))
		if err != nil {
			t.Fatalf("同一份输入两次解析结果不一致（第二次报错）：%v\n输入：%q", err, src)
		}
		if !reflect.DeepEqual(cfg, again) {
			t.Fatalf("同一份输入两次解析结果不一致\n第一次 %+v\n第二次 %+v\n输入：%q", cfg, again, src)
		}

		// 往返不变量：把解析结果写回 TOML 再解析，必须仍能解析且相等。
		// 解析成功但语义丢失（例如某个键被读成空）会在这里暴露。
		encoded, err := toml.Marshal(cfg)
		if err != nil {
			t.Fatalf("解析成功却无法回写 TOML：%v\n输入：%q", err, src)
		}
		roundTripped, err := Parse(strings.NewReader(string(encoded)))
		if err != nil {
			t.Fatalf("回写的 TOML 反而解析失败：%v\n回写内容：\n%s\n输入：%q", err, encoded, src)
		}
		if !reflect.DeepEqual(normalize(cfg), normalize(roundTripped)) {
			t.Fatalf("往返后配置发生了变化\n原始 %+v\n往返 %+v\n回写内容：\n%s", cfg, roundTripped, encoded)
		}
	})
}

// normalize 抹平 nil 与空集合的差异。
//
// 这不是掩盖问题：nil slice/map 与长度为 0 的 slice/map 在语义上完全相同
// （都表示"没有配置项"），但 reflect.DeepEqual 会判为不等——
// "空文档"解析出 nil，而回写时库会写出 `extra_protected = []`，再解析就变成空切片。
// 这个差异与"配置有没有被读进去"无关，属于 Go 的集合零值噪音，比较前先归一。
func normalize(c Config) Config {
	if c.Trash.Roots == nil {
		c.Trash.Roots = map[string]string{}
	}
	if c.Guard.ExtraProtected == nil {
		c.Guard.ExtraProtected = []string{}
	}
	return c
}
