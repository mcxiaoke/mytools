package volume

import (
	"bytes"
	"strings"
	"testing"
	"time"
)

// where 的输出要能回答两个问题：各卷的回收目录是否可用；上次有没有没搬完的东西。
func TestProbeSummaryRender(t *testing.T) {
	sum := ProbeSummary{
		Version: "v1.2.3",
		Probes: []Probe{
			{
				VolumeRoot: "/data", Key: "/data", TrashRoot: "/data/.saferm-trash",
				Source: "default", Exists: true, HasMarker: true, Writable: true, Usable: true,
			},
			{
				VolumeRoot: "/boot", Key: "/boot", TrashRoot: "/boot/.saferm-trash",
				Source: "config", Problem: "目录非空且没有 .saferm-trash-root 标记",
			},
		},
		Unfinished: []UnfinishedOp{{
			OpID: "20261006-213000-4242", State: "partial", TrashRoot: "/data/.saferm-trash",
			StartedAt: time.Date(2026, 10, 6, 21, 30, 0, 0, time.Local),
			Items:     3, Done: 2, Failed: 1,
		}},
		BadManifests: 1,
		Notes:        []string{"说明：本命令只枚举本地卷。"},
	}

	var buf bytes.Buffer
	if err := sum.Render(&buf); err != nil {
		t.Fatalf("Render: %v", err)
	}
	out := buf.String()

	for _, want := range []string{
		"v1.2.3",
		"/data",
		"就绪",
		"/boot",
		"不可用",
		"目录非空且没有",
		"未正常收尾的操作",
		"20261006-213000-4242",
		"partial",
		"清单文件无法解析",
		"只枚举本地卷",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("输出里缺少 %q：\n%s", want, out)
		}
	}

	// 不可用的卷也必须给出路径，否则用户不知道去修哪个配置
	if !strings.Contains(out, "/boot/.saferm-trash") {
		t.Errorf("不可用的卷也要报出打算使用的路径：\n%s", out)
	}
}

// 一切正常时不应输出"未完成/损坏清单"这些噪音段落。
func TestProbeSummaryRenderQuietWhenHealthy(t *testing.T) {
	sum := ProbeSummary{
		Version: "dev",
		Probes: []Probe{{
			VolumeRoot: "C:\\", Key: "C", TrashRoot: "C:\\.saferm-trash",
			Source: "default", NeedCreate: true, Writable: true, Usable: true,
		}},
	}

	var buf bytes.Buffer
	if err := sum.Render(&buf); err != nil {
		t.Fatalf("Render: %v", err)
	}
	out := buf.String()
	if strings.Contains(out, "未正常收尾") {
		t.Errorf("没有未完成操作时不该出现该段落:\n%s", out)
	}
	if !strings.Contains(out, "待创建") {
		t.Errorf("尚不存在的回收目录应显示为待创建:\n%s", out)
	}
}
