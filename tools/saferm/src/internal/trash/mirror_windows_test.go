//go:build windows

package trash

import (
	"path/filepath"
	"testing"
)

// 镜像规则在 Windows 上的形状：去掉盘符，保留其余目录结构。
func TestMirrorPathWindowsShapes(t *testing.T) {
	opDir := `D:\.saferm-trash\20261006-213000-4242`

	cases := []struct {
		name    string
		volRoot string
		src     string
		want    string
	}{
		{
			name:    "盘符根下的深路径",
			volRoot: `D:\`,
			src:     `D:\projects\mytools\tools\saferm`,
			want:    filepath.Join(opDir, "projects", "mytools", "tools", "saferm"),
		},
		{
			name:    "盘符根下的单文件",
			volRoot: `D:\`,
			src:     `D:\foo.txt`,
			want:    filepath.Join(opDir, "foo.txt"),
		},
		{
			name:    "盘符大小写不一致",
			volRoot: `D:\`,
			src:     `d:\a\b`,
			want:    filepath.Join(opDir, "a", "b"),
		},
		{
			name:    "UNC 共享",
			volRoot: `\\nas\share\`,
			src:     `\\nas\share\a\b`,
			want:    filepath.Join(opDir, "a", "b"),
		},
		{
			name:    "挂载到文件夹的卷",
			volRoot: `C:\Mount\Data\`,
			src:     `C:\Mount\Data\x\y`,
			want:    filepath.Join(opDir, "x", "y"),
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, err := MirrorPath(opDir, tc.volRoot, tc.src)
			if err != nil {
				t.Fatalf("MirrorPath(%q, %q): %v", tc.volRoot, tc.src, err)
			}
			if got != tc.want {
				t.Fatalf("MirrorPath = %q，期望 %q", got, tc.want)
			}
		})
	}
}

// 尾随点/空格、8.3 短名这些 Windows 特有的写法在比较前缀时不能出岔子。
func TestMirrorPathWindowsTrickyPrefixes(t *testing.T) {
	opDir := `C:\t\op`

	// 短名形式的卷根前缀也应能对上（大小写与短名都不影响前缀比较）
	if _, err := MirrorPath(opDir, `D:\`, `D:\PROGRA~1\thing`); err != nil {
		t.Fatalf("短名路径应能算出镜像落点：%v", err)
	}

	// 不同盘符必须报错
	if _, err := MirrorPath(opDir, `D:\`, `E:\x`); err == nil {
		t.Fatal("跨盘符的路径不能算出镜像落点")
	}
}
