package main

import (
	"context"
	"flag"
	"fmt"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"text/tabwriter"
	"time"
)

func main() {
	configPath := flag.String("config", "config.yaml", "path to config file")
	flag.Parse()

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

	// initialize logger from config (file output or stdout, level filtering)
	l, cleanup, err := initLogger(cfg)
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed to init logger: %v\n", err)
		os.Exit(1)
	}
	logger = l
	defer cleanup()

	// startup banner — always printed to stdout regardless of log file config
	printStartup(cfg, *configPath)

	// create and start indexer
	idx := NewIndexer(cfg)
	idx.Start()

	// create HTTP server
	srv := NewServer(cfg, idx)
	httpSrv := &http.Server{
		Addr:              fmt.Sprintf("%s:%d", cfg.Server.Host, cfg.Server.Port),
		Handler:           loggingMiddleware(srv.Routes()),
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

const version = "0.1.0"

// printStartup prints a startup banner to stdout.
// This goes to the console regardless of whether log file is configured,
// so the user always sees basic info on launch.
func printStartup(cfg *Config, configPath string) {
	w := tabwriter.NewWriter(os.Stdout, 0, 0, 2, ' ', 0)
	fmt.Fprintf(w, "FileList v%s\n\n", version)
	fmt.Fprintf(w, "Config\t%s\n", configPath)
	fmt.Fprintf(w, "Listen\thttp://%s:%d\n", cfg.Server.Host, cfg.Server.Port)
	fmt.Fprintf(w, "DataDir\t%s\n", cfg.DataDir)
	logDest := cfg.Log.File
	if logDest == "" {
		logDest = "stdout"
	}
	fmt.Fprintf(w, "Log\t%s (%s)\n", logDest, cfg.Log.Level)
	fmt.Fprintf(w, "Index\tinterval=%s, persist=%s\n", cfg.Index.Interval, func() string {
		if cfg.Index.Persist != "" {
			return cfg.Index.Persist
		}
		return "auto"
	}())
	fmt.Fprintf(w, "Roots:\n")
	for _, r := range cfg.Roots {
		fmt.Fprintf(w, "  %s\t-> %s\n", r.URL, r.Path)
	}
	w.Flush()
	fmt.Println()
}

// writeDefaultConfig generates a starter config.yaml with example mappings.
func writeDefaultConfig(path string) error {
	content := `# FileList config
# Edit roots below to match your directories, then restart.
# See config.sample.yaml for full documentation of all fields.

server:
  host: 0.0.0.0
  port: 8080

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
