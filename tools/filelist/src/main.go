package main

import (
	"context"
	"flag"
	"fmt"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"runtime/debug"
	"strings"
	"syscall"
	"text/tabwriter"
	"time"
)

func main() {
	versionFlag := flag.Bool("version", false, "print version and exit")
	vFlag := flag.Bool("v", false, "print version and exit (shorthand)")
	configPath := flag.String("config", "config.yaml", "path to config file")
	flag.Parse()

	if *versionFlag || *vFlag {
		fmt.Printf("FileList v%s (%s, built %s)\n", version, gitCommit, buildTime)
		os.Exit(0)
	}

	// generate default config if not found
	if _, err := os.Stat(*configPath); os.IsNotExist(err) {
		logger.Info("config file %s not found, generating default...", *configPath)
		if writeErr := writeDefaultConfig(*configPath); writeErr != nil {
			logger.Fatal("cannot write default config: %v\nPlease create %s manually.", writeErr, *configPath)
		}
		logger.Info("default config written to %s — edit it and restart.", *configPath)
		os.Exit(0)
	}

	cfg, err := LoadConfig(*configPath)
	if err != nil {
		logger.Fatal("failed to load config: %v", err)
	}

	// runtime data dir (index cache) — created on startup so persistence
	// never fails silently
	if err := os.MkdirAll(cfg.DataDir, 0755); err != nil {
		logger.Fatal("cannot create dataDir %s: %v", cfg.DataDir, err)
	}

	// initialize logger from config (file output or stdout, level filtering)
	l, cleanup, err := initLogger(cfg)
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed to init logger: %v\n", err)
		os.Exit(1)
	}
	logger = l
	defer cleanup()

	logger.Info("starting FileList v%s (%s, built %s)", version, gitCommit, buildTime)

	// startup banner — always printed to stdout regardless of log file config
	printStartup(cfg, *configPath)

	// create and start indexer
	idx := NewIndexer(cfg)
	idx.Start()

	// create HTTP server
	srv := NewServer(cfg, idx)

	// ops panel — optional, and only mounted when it is genuinely
	// usable. A nil panel registers no routes, so a disabled feature
	// is indistinguishable from one that does not exist.
	opsPanel, err := newOpsPanel(cfg, idx)
	if err != nil {
		logger.Fatal("failed to initialise ops panel: %v", err)
	}
	if opsPanel != nil {
		srv.SetOps(opsPanel)
		defer opsPanel.Close()
	}

	httpSrv := &http.Server{
		Addr:              fmt.Sprintf("%s:%d", cfg.Server.Host, cfg.Server.Port),
		Handler:           loggingMiddleware(srv.Handler()),
		ReadHeaderTimeout: 10 * time.Second,
	}

	// graceful shutdown
	go func() {
		sigCh := make(chan os.Signal, 1)
		signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
		<-sigCh
		logger.Info("shutting down...")
		idx.Stop()
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		httpSrv.Shutdown(ctx)
	}()

	logger.Info("listening on http://%s:%d", cfg.Server.Host, cfg.Server.Port)
	if err := httpSrv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		logger.Fatal("server error: %v", err)
	}
	logger.Info("server stopped")
}

var (
	version   = "0.2.0"
	gitCommit = ""
	buildTime = ""
)

func init() {
	initBuildInfo()
}

func initBuildInfo() {
	if gitCommit == "" || buildTime == "" {
		if info, ok := debug.ReadBuildInfo(); ok {
			var rev, t, modified string
			for _, s := range info.Settings {
				switch s.Key {
				case "vcs.revision":
					rev = s.Value
				case "vcs.time":
					t = s.Value
				case "vcs.modified":
					modified = s.Value
				}
			}
			if gitCommit == "" && rev != "" {
				if len(rev) > 7 {
					gitCommit = rev[:7]
				} else {
					gitCommit = rev
				}
				if modified == "true" {
					gitCommit += "-dirty"
				}
			}
			if buildTime == "" && t != "" {
				if parsed, err := time.Parse(time.RFC3339, t); err == nil {
					buildTime = parsed.Local().Format("2006-01-02 15:04:05")
				} else {
					buildTime = t
				}
			}
		}
	}
	if gitCommit == "" {
		gitCommit = "dev"
	}
	if buildTime == "" {
		buildTime = time.Now().Format("2006-01-02 15:04:05")
	}
}

// printStartup prints a startup banner to stdout.
// This goes to the console regardless of whether log file is configured,
// so the user always sees basic info on launch.
func printStartup(cfg *Config, configPath string) {
	w := tabwriter.NewWriter(os.Stdout, 0, 0, 2, ' ', 0)
	fmt.Fprintf(w, "FileList v%s (%s, built %s)\n\n", version, gitCommit, buildTime)
	fmt.Fprintf(w, "Config\t%s\n", configPath)
	fmt.Fprintf(w, "Listen\thttp://%s:%d\n", cfg.Server.Host, cfg.Server.Port)
	if cfg.Server.BasePath != "" {
		fmt.Fprintf(w, "BasePath\t%s (sub-directory deployment)\n", cfg.Server.BasePath)
	}
	fmt.Fprintf(w, "DataDir\t%s\n", cfg.DataDir)
	logDest := cfg.Log.File
	if logDest == "" {
		logDest = "stdout"
	}
	fmt.Fprintf(w, "Log\t%s (%s)\n", logDest, cfg.Log.Level)
	fmt.Fprintf(w, "Index\tinterval=%s, persist=%s, incremental=%v, maxDepth=%s\n",
		cfg.Index.Interval, func() string {
			if cfg.Index.Persist != "" {
				return cfg.Index.Persist
			}
			return "auto"
		}(), cfg.IndexIncremental(), func() string {
			if cfg.Index.MaxDepth <= 0 {
				return "unlimited"
			}
			return fmt.Sprint(cfg.Index.MaxDepth)
		}())
	fmt.Fprintf(w, "Auth\t%s\n", func() string {
		if cfg.Server.Token != "" {
			return "token enabled"
		}
		return "disabled (open access)"
	}())
	fmt.Fprintf(w, "Security\tsymlink-escape=%s, inline-html=%s\n",
		onOff(!cfg.Security.AllowOutsideSymlinks, "blocked", "allowed"),
		onOff(cfg.InlineHTMLBlocked(), "download-only", "rendered"))
	fmt.Fprintf(w, "Roots:\n")
	for _, r := range cfg.Roots {
		fmt.Fprintf(w, "  %s\t-> %s\n", r.URL, r.Path)
	}
	w.Flush()
	fmt.Println()

	// warn when a root points at the config directory itself — the usual
	// result of keeping the default "path: ./" in a system location
	for _, r := range cfg.Roots {
		if rootInsideConfigDir(r.Path, cfg.configDir) {
			msg := fmt.Sprintf("root %s -> %s points at the config directory itself; "+
				"it will be served over HTTP. Set an explicit absolute path.", r.URL, r.Path)
			fmt.Println("WARNING: " + msg)
			logger.Warn("config: %s", msg)
		}
	}
}

// onOff renders a boolean as one of two labels.
func onOff(cond bool, yes, no string) string {
	if cond {
		return yes
	}
	return no
}

// rootInsideConfigDir reports whether root is the config dir or below it.
func rootInsideConfigDir(root, configDir string) bool {
	if root == "" || configDir == "" {
		return false
	}
	root = filepath.Clean(root)
	dir := filepath.Clean(configDir)
	if strings.EqualFold(root, dir) {
		return true
	}
	return strings.HasPrefix(strings.ToLower(root), strings.ToLower(dir)+string(filepath.Separator))
}

// writeDefaultConfig generates a starter config.yaml with example mappings.
func writeDefaultConfig(path string) error {
	content := `# FileList config
# Edit roots below to match your directories, then restart.
# See config.sample.yaml for full documentation of all fields.

server:
  host: 0.0.0.0
  port: 8080
  # basePath: /files      # set when served behind a reverse proxy sub-path
  # token: my-secret      # optional; enables a simple access token

log:
  level: info              # debug | info | warn | error
  file: ""                 # log file path (empty = stdout)

dataDir: ./data            # directory for index files and other runtime data

index:
  interval: 5m              # re-index interval (30s, 5m, 1h)
  persist: ""                # index cache file (empty = auto: dataDir/filelist.idx)
  maxDepth: 0                # max walk depth (0 = unlimited)
  excludeDirs:               # directory names to skip during indexing
    - .git
    - node_modules
    - __pycache__
    - $RECYCLE.BIN
    - System Volume Information

# Path mappings: URL path -> real disk path
# Use forward slashes in disk paths for cross-platform compatibility.
# NOTE: a relative path resolves against THIS file's directory. If this config
# lives in /etc/filelist, "path: ./" would publish /etc itself — use an
# explicit absolute path for anything shared on a network.
roots:
  # Linux examples:
  # - url: /data
  #   path: /mnt/data
  # - url: /usb
  #   path: /mnt/usb

  # Windows examples:
  - url: /files
    path: ./
`
	return os.WriteFile(path, []byte(content), 0644)
}
