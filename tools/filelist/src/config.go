package main

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"gopkg.in/yaml.v3"
)

// Config is the application configuration loaded from YAML.
type Config struct {
	Log struct {
		Level string `yaml:"level"` // debug, info, warn, error
		File  string `yaml:"file"`  // log file path (empty = stdout)
	} `yaml:"log"`
	DataDir string `yaml:"dataDir"` // data directory for index files etc.
	Server  struct {
		Host string `yaml:"host"`
		Port int    `yaml:"port"`
	} `yaml:"server"`
	Index struct {
		Interval    string   `yaml:"interval"`    // re-index interval, e.g. "5m"
		Persist     string   `yaml:"persist"`     // index persistence file path
		MaxDepth    int      `yaml:"maxDepth"`    // max walk depth (0 = unlimited)
		ExcludeDirs []string `yaml:"excludeDirs"` // directory names to skip
	} `yaml:"index"`
	Roots []RootMapping `yaml:"roots"`
}

// RootMapping maps a virtual URL path to a real disk path.
type RootMapping struct {
	URL  string `yaml:"url"`  // virtual web path, e.g. /data
	Path string `yaml:"path"` // real disk path, e.g. /mnt/data
}

// resolvePath resolves a path to an absolute path.
// - empty returns empty
// - ~ is expanded to home directory
// - absolute paths are returned as-is (cleaned)
// - relative paths are resolved relative to baseDir
func resolvePath(p, baseDir string) string {
	if p == "" {
		return ""
	}
	// expand ~ to home directory
	if strings.HasPrefix(p, "~") {
		home, _ := os.UserHomeDir()
		p = strings.Replace(p, "~", home, 1)
		return filepath.Clean(p)
	}
	// already absolute
	if filepath.IsAbs(p) {
		return filepath.Clean(p)
	}
	// resolve relative to baseDir
	return filepath.Clean(filepath.Join(baseDir, p))
}

// LoadConfig reads and parses the YAML config file.
func LoadConfig(path string) (*Config, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read config %s: %w", path, err)
	}
	var cfg Config
	if err := yaml.Unmarshal(data, &cfg); err != nil {
		return nil, fmt.Errorf("parse config %s: %w", path, err)
	}

	// baseDir: config file's directory, used to resolve relative paths
	baseDir, _ := filepath.Abs(filepath.Dir(path))

	// apply defaults
	if cfg.Log.Level == "" {
		cfg.Log.Level = "info"
	}
	if cfg.DataDir == "" {
		cfg.DataDir = "./data"
	}
	if cfg.Server.Host == "" {
		cfg.Server.Host = "0.0.0.0"
	}
	if cfg.Server.Port == 0 {
		cfg.Server.Port = 8080
	}
	if cfg.Index.Interval == "" {
		cfg.Index.Interval = "5m"
	}

	// resolve relative paths to absolute (relative to config file directory)
	cfg.DataDir = resolvePath(cfg.DataDir, baseDir)
	cfg.Log.File = resolvePath(cfg.Log.File, baseDir)

	// resolve persist: if empty, default to dataDir/filelist.idx (already resolved)
	if cfg.Index.Persist == "" {
		cfg.Index.Persist = filepath.Join(cfg.DataDir, "filelist.idx")
	} else {
		cfg.Index.Persist = resolvePath(cfg.Index.Persist, baseDir)
	}

	// normalize root URLs: ensure leading slash, no trailing slash.
	// root "/" is NOT allowed — it conflicts with the roots view entry point.
	// all roots must be subpaths like /data, /downloads, /资料, etc.
	for i := range cfg.Roots {
		r := &cfg.Roots[i]
		if r.URL == "" {
			return nil, fmt.Errorf("root[%d]: url is empty", i)
		}
		if !strings.HasPrefix(r.URL, "/") {
			r.URL = "/" + r.URL
		}
		// trim trailing slashes
		r.URL = strings.TrimRight(r.URL, "/")
		if r.URL == "" {
			// URL was "/" — reject it
			return nil, fmt.Errorf("root[%d]: url \"/\" is not allowed, use a subpath like /data", i)
		}
		if r.Path == "" {
			return nil, fmt.Errorf("root[%d]: path is empty", i)
		}
		// resolve path (expand ~, resolve relative to config dir)
		r.Path = resolvePath(r.Path, baseDir)
	}

	if len(cfg.Roots) == 0 {
		return nil, fmt.Errorf("no roots configured")
	}

	// sort roots by URL length (longest first) so that overlapping prefixes
	// like /data and /data/archive always match the most specific root.
	sort.SliceStable(cfg.Roots, func(i, j int) bool {
		return len(cfg.Roots[i].URL) > len(cfg.Roots[j].URL)
	})

	return &cfg, nil
}
