package main

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"gopkg.in/yaml.v3"

	"filelist/internal/ops"
)

// Config is the application configuration loaded from YAML.
type Config struct {
	Log struct {
		Level string `yaml:"level"` // debug, info, warn, error
		File  string `yaml:"file"`  // log file path (empty = stdout)
	} `yaml:"log"`
	DataDir string `yaml:"dataDir"` // data directory for index files etc.
	Server  struct {
		Host     string `yaml:"host"`     // listen address
		Port     int    `yaml:"port"`     // listen port
		BasePath string `yaml:"basePath"` // sub-path prefix when behind a reverse proxy (e.g. /files)
		Token    string `yaml:"token"`    // optional access token; empty disables auth
	} `yaml:"server"`
	Index struct {
		Interval     string   `yaml:"interval"`     // re-index interval, e.g. "5m"
		Persist      string   `yaml:"persist"`      // index persistence file path
		MaxDepth     int      `yaml:"maxDepth"`     // max walk depth (0 = unlimited)
		ExcludeDirs  []string `yaml:"excludeDirs"`  // directory names to skip
		ExcludeFiles []string `yaml:"excludeFiles"` // file name patterns to skip (glob)
		Incremental  *bool    `yaml:"incremental"`  // true (default): diff by mtime; false: always full rebuild
	} `yaml:"index"`
	Security struct {
		AllowOutsideSymlinks bool  `yaml:"allowOutsideSymlinks"` // allow /raw to serve symlink targets outside the root
		BlockInlineHTML      *bool `yaml:"blockInlineHTML"`      // true (default): force html/svg download instead of inline render
	} `yaml:"security"`
	Upload struct {
		Enabled bool `yaml:"enabled"` // allow uploading files
	} `yaml:"upload"`
	Manage struct {
		Enabled     bool   `yaml:"enabled"`     // master switch for file management (mkdir, rename, edit, delete)
		AllowEdit   *bool  `yaml:"allowEdit"`   // allow editing text files (default true if manage.enabled)
		AllowMkdir  *bool  `yaml:"allowMkdir"`  // allow creating directories (default true if manage.enabled)
		AllowRename *bool  `yaml:"allowRename"` // allow renaming files/dirs (default true if manage.enabled)
		AllowDelete bool   `yaml:"allowDelete"` // allow deleting files/dirs (default false)
		DeleteToken string `yaml:"deleteToken"` // token required for deletion (must be non-empty if allowDelete is true)
	} `yaml:"manage"`
	Ops struct {
		Enabled        bool     `yaml:"enabled"`        // master switch
		Token          string   `yaml:"token"`          // dedicated access token for the panel
		Mode           string   `yaml:"mode"`           // allowlist (default) | free
		TerminalToken  string   `yaml:"terminalToken"`  // second factor for the interactive terminal
		CwdRoots       []string `yaml:"cwdRoots"`       // allowed working-directory roots (empty = reuse roots)
		OriginPatterns []string `yaml:"originPatterns"` // WebSocket Origin allowlist (empty = derive from Host)
		Timeout        string   `yaml:"timeout"`        // per-command timeout
		MaxOutput      string   `yaml:"maxOutput"`      // per-session output cap
		MaxSessions    int      `yaml:"maxSessions"`    // concurrent session cap
		IdleTimeout    string   `yaml:"idleTimeout"`    // idle session reaping
		Scrollback     string   `yaml:"scrollbackBytes"`
		Allow          []string `yaml:"allow"` // extra patterns appended to the built-in read-only set
		Deny           []string `yaml:"deny"`  // empty list clears the built-in dangerous set
	} `yaml:"ops"`
	Roots []RootMapping `yaml:"roots"`

	// configDir is the directory of the config file, used for resolving
	// relative paths. Not read from YAML.
	configDir string
}

// RootMapping maps a virtual URL path to a real disk path.
type RootMapping struct {
	URL  string `yaml:"url"`  // virtual web path, e.g. /data
	Path string `yaml:"path"` // real disk path, e.g. /mnt/data
}

// Default exclude patterns, used when index.excludeDirs / index.excludeFiles
// are not set in the config file.
var (
	defaultExcludeDirs = []string{
		".git", "node_modules", "__pycache__", "$RECYCLE.BIN", "System Volume Information",
	}
	defaultExcludeFiles = []string{
		".env", ".env.*", ".htpasswd", ".htaccess",
		"id_rsa", "id_rsa.*", "id_ed25519", "id_ed25519.*",
		"*.pem", "*.key", "*.pfx", "*.p12",
		".DS_Store", "Thumbs.db",
	}
)

// IndexIncremental reports whether incremental indexing is enabled (default true).
func (c *Config) IndexIncremental() bool {
	return c.Index.Incremental == nil || *c.Index.Incremental
}

// InlineHTMLBlocked reports whether inline html/svg rendering is blocked (default true).
func (c *Config) InlineHTMLBlocked() bool {
	return c.Security.BlockInlineHTML == nil || *c.Security.BlockInlineHTML
}

// ManageEnabled reports whether file management is enabled (default false).
func (c *Config) ManageEnabled() bool {
	return c.Manage.Enabled
}

// ManageEdit reports whether text editing is allowed (default true when manage is enabled).
func (c *Config) ManageEdit() bool {
	if !c.Manage.Enabled {
		return false
	}
	return c.Manage.AllowEdit == nil || *c.Manage.AllowEdit
}

// ManageMkdir reports whether creating directories is allowed (default true when manage is enabled).
func (c *Config) ManageMkdir() bool {
	if !c.Manage.Enabled {
		return false
	}
	return c.Manage.AllowMkdir == nil || *c.Manage.AllowMkdir
}

// ManageRename reports whether renaming files/directories is allowed (default true when manage is enabled).
func (c *Config) ManageRename() bool {
	if !c.Manage.Enabled {
		return false
	}
	return c.Manage.AllowRename == nil || *c.Manage.AllowRename
}

// ManageDelete reports whether deleting files/directories is allowed.
// Safe by default: Delete is ONLY allowed when allowDelete is true AND deleteToken is non-empty.
func (c *Config) ManageDelete() bool {
	if !c.Manage.Enabled {
		return false
	}
	return c.Manage.AllowDelete && strings.TrimSpace(c.Manage.DeleteToken) != ""
}

// ManageDeleteToken returns the configured deletion token.
func (c *Config) ManageDeleteToken() string {
	return strings.TrimSpace(c.Manage.DeleteToken)
}

// ── Ops panel accessors ─────────────────────────────────────────

// OpsEnabled reports whether the operations panel is genuinely
// available. Fail-closed: the feature must be explicitly enabled AND a
// dedicated ops token must be configured.
//
// The panel uses its own token rather than reusing server.token. Two
// reasons: browsing files should stay simple (a shared token pushes
// people toward leaving server.token empty), and the panel is the
// highest-risk feature in the project, so it deserves a credential
// that can be rotated or revoked without disturbing normal browsing.
func (c *Config) OpsEnabled() bool {
	return c.Ops.Enabled && strings.TrimSpace(c.Ops.Token) != ""
}

// OpsTerminalEnabled reports whether the interactive terminal may be
// used. It needs its own second factor: an interactive shell is a full
// shell, whereas one-shot commands are bounded by the allowlist.
func (c *Config) OpsTerminalEnabled() bool {
	return c.OpsEnabled() && strings.TrimSpace(c.Ops.TerminalToken) != ""
}

// OpsFreeMode reports whether allowlist enforcement is disabled.
func (c *Config) OpsFreeMode() bool {
	return c.OpsEnabled() && strings.EqualFold(strings.TrimSpace(c.Ops.Mode), "free")
}

// OpsConfig builds the panel configuration from the YAML settings,
// applying the documented defaults for anything left unset.
func (c *Config) OpsConfig() ops.Config {
	cfg := ops.Defaults()
	cfg.Enabled = c.OpsEnabled()
	cfg.AccessToken = strings.TrimSpace(c.Ops.Token)
	cfg.TerminalToken = strings.TrimSpace(c.Ops.TerminalToken)
	cfg.OriginPatterns = c.Ops.OriginPatterns
	cfg.CwdRoots = c.Ops.CwdRoots
	cfg.Allow = c.Ops.Allow

	if c.Ops.Mode != "" {
		cfg.Mode = strings.ToLower(strings.TrimSpace(c.Ops.Mode))
	}

	// Deny is nil-aware: a nil slice means "use the built-in dangerous
	// set", while an explicitly empty list clears it. yaml.v3 gives an
	// empty sequence as a non-nil empty slice, which is exactly the
	// distinction needed, so the mapping is direct.
	cfg.Deny = c.Ops.Deny

	if d, ok := parseByteSize(c.Ops.MaxOutput); ok {
		cfg.MaxOutput = d
	}
	if d, ok := parseByteSize(c.Ops.Scrollback); ok {
		cfg.ScrollbackSize = int(d)
	}
	if d, err := time.ParseDuration(c.Ops.Timeout); err == nil && d > 0 {
		cfg.Timeout = d
	}
	if d, err := time.ParseDuration(c.Ops.IdleTimeout); err == nil && d > 0 {
		cfg.IdleTimeout = d
	}
	if c.Ops.MaxSessions > 0 {
		cfg.MaxSessions = c.Ops.MaxSessions
	}

	return cfg
}

// parseByteSize parses human-readable sizes such as "1MB", "256KB".
// A bare number is treated as bytes.
func parseByteSize(s string) (int64, bool) {
	s = strings.TrimSpace(s)
	if s == "" {
		return 0, false
	}
	upper := strings.ToUpper(s)

	mult := int64(1)
	for _, suffix := range []struct {
		suffix string
		mult   int64
	}{
		{"TB", 1 << 40}, {"GB", 1 << 30}, {"MB", 1 << 20}, {"KB", 1 << 10}, {"B", 1},
	} {
		if strings.HasSuffix(upper, suffix.suffix) {
			mult = suffix.mult
			upper = strings.TrimSuffix(upper, suffix.suffix)
			break
		}
	}

	n, err := strconv.ParseInt(strings.TrimSpace(upper), 10, 64)
	if err != nil || n <= 0 {
		return 0, false
	}
	return n * mult, true
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

// normalizeBasePath validates and normalizes a base path for sub-directory
// deployment. Accepted forms: "" (root), "/files", "/files/", "files".
// Returns "" when the result equals "/".
func normalizeBasePath(p string) (string, error) {
	p = strings.TrimSpace(p)
	if p == "" {
		return "", nil
	}
	if !strings.HasPrefix(p, "/") {
		p = "/" + p
	}
	p = strings.TrimRight(p, "/")
	if p == "" {
		// input was "/" — treat as root deployment
		return "", nil
	}
	// reject anything that could escape or inject into the HTML template.
	// Trim leading/trailing slashes first so "/files" is not seen as an
	// empty segment; interior empty segments (a//b) are still rejected.
	segs := strings.Split(strings.Trim(p, "/"), "/")
	for _, seg := range segs {
		if seg == "" || seg == "." || seg == ".." {
			return "", fmt.Errorf("server.basePath %q contains an empty or relative segment", p)
		}
		for _, r := range seg {
			ok := (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') ||
				r == '-' || r == '_' || r == '.' || r == '~'
			if !ok {
				return "", fmt.Errorf("server.basePath %q contains unsupported character %q", p, string(r))
			}
		}
	}
	return p, nil
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
	cfg.configDir = baseDir

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
	if cfg.Index.ExcludeDirs == nil {
		cfg.Index.ExcludeDirs = defaultExcludeDirs
	}
	if cfg.Index.ExcludeFiles == nil {
		cfg.Index.ExcludeFiles = defaultExcludeFiles
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

	// normalize base path (sub-directory reverse proxy deployment)
	cfg.Server.BasePath, err = normalizeBasePath(cfg.Server.BasePath)
	if err != nil {
		return nil, err
	}
	cfg.Server.Token = strings.TrimSpace(cfg.Server.Token)

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

	if err := cfg.validateOps(); err != nil {
		return nil, err
	}

	return &cfg, nil
}

// validateOps checks the ops panel settings. It warns rather than
// fails where a setting is merely unwise, and errors only where the
// configuration could not work at all.
func (c *Config) validateOps() error {
	if !c.Ops.Enabled {
		return nil
	}

	// The panel needs its own credential for the WebSocket handshake.
	// Without one it would be a browser-reachable command runner with
	// no lock, so refuse to enable it and say why.
	if strings.TrimSpace(c.Ops.Token) == "" {
		fmt.Println("WARNING: ops.enabled is true but ops.token is empty — " +
			"the ops panel will stay DISABLED. Set ops.token to enable it.")
		logger.Warn("config: ops.enabled=true but ops.token is empty; panel disabled")
	}

	mode := strings.ToLower(strings.TrimSpace(c.Ops.Mode))
	if mode != "" && mode != "allowlist" && mode != "free" {
		return fmt.Errorf("ops.mode %q must be \"allowlist\" or \"free\"", c.Ops.Mode)
	}

	if mode == "free" {
		fmt.Println("WARNING: ops.mode is \"free\" — arbitrary commands may be executed " +
			"through the browser. Only the built-in deny list applies.")
		logger.Warn("config: ops.mode=free — arbitrary command execution is permitted")
	}

	// Reusing the same secret for the panel and for browsing defeats
	// the point of having a separate token.
	opsToken := strings.TrimSpace(c.Ops.Token)
	if opsToken != "" && opsToken == strings.TrimSpace(c.Server.Token) {
		fmt.Println("WARNING: ops.token equals server.token — " +
			"the panel token should be a distinct secret so it can be revoked independently.")
		logger.Warn("config: ops.token duplicates server.token")
	}

	// The delete token is a different kind of secret from the panel
	// token: it is typed into a browser dialog, so it tends to be
	// shorter and easier to observe. Sharing it with the panel means
	// one leaked value grants both file deletion and shell access.
	if opsToken != "" && opsToken == c.ManageDeleteToken() {
		fmt.Println("WARNING: ops.token equals manage.deleteToken — " +
			"these guard different capabilities (shell access vs file deletion) " +
			"and should not share a secret.")
		logger.Warn("config: ops.token duplicates manage.deleteToken")
	}

	if strings.TrimSpace(c.Ops.TerminalToken) != "" &&
		c.Ops.TerminalToken == c.Ops.Token {
		fmt.Println("WARNING: ops.terminalToken equals ops.token — " +
			"the second factor should be a distinct secret to be meaningful.")
		logger.Warn("config: ops.terminalToken duplicates ops.token")
	}

	// An interactive terminal without its own token is disabled; say so
	// rather than letting the operator discover it in the UI.
	if strings.TrimSpace(c.Ops.TerminalToken) == "" {
		logger.Info("config: ops interactive terminal disabled (no ops.terminalToken set)")
	}

	for _, root := range c.Ops.CwdRoots {
		if _, err := os.Stat(root); err != nil {
			logger.Warn("config: ops.cwdRoots entry %s is not accessible: %v", root, err)
		}
	}

	// Validate the patterns early so a typo surfaces at startup rather
	// than on the first command.
	if _, err := ops.New(c.OpsConfig(), nil, nil); err != nil {
		return fmt.Errorf("ops configuration: %w", err)
	}

	return nil
}
