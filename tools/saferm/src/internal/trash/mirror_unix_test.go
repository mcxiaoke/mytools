//go:build !windows

package trash

import (
	"path/filepath"
	"testing"
)

// 镜像规则在 Linux 上的形状：去掉挂载点前缀，保留其余目录结构。
func TestMirrorPathUnixShapes(t *testing.T) {
	opDir := "/data/.saferm-trash/20261006-213000-4242"

	cases := []struct {
		name    string
		volRoot string
		src     string
		want    string
	}{
		{
			name:    "挂载点 /data",
			volRoot: "/data",
			src:     "/data/projects/a/b",
			want:    filepath.Join(opDir, "projects", "a", "b"),
		},
		{
			name:    "挂载点 /data 的尾随斜杠",
			volRoot: "/data/",
			src:     "/data/projects",
			want:    filepath.Join(opDir, "projects"),
		},
		{
			name:    "根挂载点 /",
			volRoot: "/",
			src:     "/etc/fstab",
			want:    filepath.Join(opDir, "etc", "fstab"),
		},
		{
			name:    "根挂载点下的深层路径",
			volRoot: "/",
			src:     "/home/me/x/y/z",
			want:    filepath.Join(opDir, "home", "me", "x", "y", "z"),
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

// Linux 上大小写敏感：不同前缀必须报错，不能被当成同一个卷。
func TestMirrorPathUnixIsCaseSensitive(t *testing.T) {
	opDir := "/t/op"
	if _, err := MirrorPath(opDir, "/Data", "/data/x"); err == nil {
		t.Fatal("Linux 上 /Data 与 /data 是不同路径，必须报错")
	}
}
