package volume

import (
	"os"

	"saferm/internal/platform"
)

// Probe 是一条卷级自检结果，供 `saferm where` 展示（设计文档 §8.1）。
type Probe struct {
	VolumeRoot string
	Key        string
	TrashRoot  string
	Source     string
	Exists     bool
	HasMarker  bool
	NeedCreate bool
	Writable   bool
	Usable     bool
	Fallback   bool
	Problem    string
}

// ProbeVolumes 枚举本机卷，逐个解析回收根，并报告存在性/标记/可写性。
//
// 只读：不创建目录、不写标记。可写性用 platform.IsWritableDir 这种无副作用的
// 方式探测，绝不用"创建探测文件再删掉"——那既违背只读承诺，也违背 C8。
func ProbeVolumes(cfg Config) ([]Probe, error) {
	volumes, err := platform.ListVolumes()
	if err != nil {
		return nil, err
	}

	out := make([]Probe, 0, len(volumes))
	for _, vol := range volumes {
		choice := resolveRootForVolume(vol, cfg)
		p := Probe{
			VolumeRoot: vol,
			Key:        Key(vol),
			TrashRoot:  choice.root,
			Source:     choice.source,
			Fallback:   choice.fallback,
			NeedCreate: choice.needCreate,
			Problem:    choice.problem,
		}

		if info, statErr := os.Lstat(choice.root); statErr == nil {
			p.Exists = true
			if _, ok, _ := ReadMarker(choice.root); ok {
				p.HasMarker = true
			}
			if info.IsDir() {
				p.Writable, _ = platform.IsWritableDir(choice.root)
			}
		} else if target, derr := platform.DeepestExisting(choice.root); derr == nil {
			// 目录还不存在：报告"它将被创建在哪里、那里能不能写"
			p.Writable, _ = platform.IsWritableDir(target)
		}

		p.Usable = choice.problem == ""
		out = append(out, p)
	}
	return out, nil
}
