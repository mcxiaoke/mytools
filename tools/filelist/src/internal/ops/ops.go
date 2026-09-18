// Package ops implements the optional operations panel: a browser-driven
// command runner with a one-shot exec mode and an interactive PTY mode.
//
// # Isolation
//
// This package deliberately knows nothing about filelist. It does not
// import the main package, the indexer, or the embedded web assets. The
// two capabilities it needs from its host — logging and virtual-to-real
// path mapping — are declared here as small interfaces and injected by
// the caller. That keeps the dependency arrow one-way (main → ops) and
// lets the package be tested without starting a server or touching a
// real root.
//
// # Security
//
// Running commands from a browser is the highest-risk feature in the
// project. The package is off by default and fails closed: the caller
// must both enable it and configure an access token before the handler
// is registered at all. See policy.go for the command boundary and
// ws.go for the WebSocket origin check, which is the defence against
// cross-site WebSocket hijacking.
package ops

import (
	"encoding/json"
	"net/http"
	"strings"
	"sync"
	"time"
)

// Logger is the minimal logging surface the panel needs. The host
// injects an adapter over its own logger.
type Logger interface {
	Info(format string, args ...any)
	Warn(format string, args ...any)
	Error(format string, args ...any)
}

// PathResolver maps virtual paths to disk paths and reports which disk
// roots may be used as a working directory. The host injects an adapter
// over its indexer; this package never imports it.
type PathResolver interface {
	// Resolve converts a virtual path (e.g. "/data/nginx") to a real
	// disk path. ok is false when no configured root matches.
	Resolve(virtualPath string) (realPath string, ok bool)

	// AllowedRoots returns the disk paths permitted as a working
	// directory. An empty result disables cwd sandboxing.
	AllowedRoots() []string
}

// Config is the resolved configuration for the panel. The host builds
// it from its own YAML config, applying fail-closed defaults.
type Config struct {
	Enabled        bool
	Mode           string // "allowlist" or "free"
	Allow          []string
	Deny           []string // nil means "use built-in dangerous set"
	TerminalToken  string
	OriginPatterns []string
	CwdRoots       []string

	// AccessToken is the host's global access token. It is required
	// explicitly on the WebSocket handshake so the upgrade never
	// depends on an automatically-sent cookie.
	AccessToken string

	Timeout        time.Duration
	MaxOutput      int64
	MaxSessions    int
	IdleTimeout    time.Duration
	ScrollbackSize int

	// KillGrace is how long a process group may take to exit on
	// SIGTERM before SIGKILL is sent.
	KillGrace time.Duration

	// PTYAvailable is false on platforms without pseudo-terminal
	// support (Windows). It is reported to the client so the terminal
	// entry point can be hidden.
	PTYAvailable bool
}

// Defaults returns a Config populated with the documented defaults.
// The host overrides fields read from YAML.
func Defaults() Config {
	return Config{
		Enabled:        false,
		Mode:           "allowlist",
		Timeout:        30 * time.Second,
		MaxOutput:      1 << 20, // 1 MiB
		MaxSessions:    2,
		IdleTimeout:    30 * time.Minute,
		ScrollbackSize: 256 << 10, // 256 KiB
		KillGrace:      3 * time.Second,
		PTYAvailable:   ptyAvailable(),
	}
}

// Panel is the assembled operations panel. Construct it with New and
// register Handler() on the host's mux.
type Panel struct {
	cfg         Config
	logger      Logger
	resolver    PathResolver
	policy      *Policy
	sessions    *SessionRegistry
	accessToken string

	// killGrace is how long a process group gets to exit on SIGTERM
	// before SIGKILL follows.
	killGrace time.Duration

	// procs maps a session id to its live one-shot process, so the
	// signal and kill frames can reach a command that is still
	// running. Without it the stop button had nothing to act on and
	// the panel stayed busy until the command finished on its own.
	procsMu sync.Mutex
	procs   map[string]*execHandle
}

// registerProc associates a running command with its session.
func (p *Panel) registerProc(sid string, h *execHandle) {
	p.procsMu.Lock()
	p.procs[sid] = h
	p.procsMu.Unlock()
}

// unregisterProc drops the association once the command is reaped.
func (p *Panel) unregisterProc(sid string) {
	p.procsMu.Lock()
	delete(p.procs, sid)
	p.procsMu.Unlock()
}

// lookupProc returns the live command for a session, if any.
func (p *Panel) lookupProc(sid string) *execHandle {
	p.procsMu.Lock()
	defer p.procsMu.Unlock()
	return p.procs[sid]
}

// stopProc terminates a one-shot command's process group.
func (p *Panel) stopProc(sid string) bool {
	h := p.lookupProc(sid)
	if h == nil {
		return false
	}
	h.Kill(p.killGrace)
	return true
}

// New builds a panel. It returns an error when the configuration is
// inconsistent (for example an uncompilable allow pattern).
func New(cfg Config, logger Logger, resolver PathResolver) (*Panel, error) {
	if logger == nil {
		logger = nopLogger{}
	}

	roots := cfg.CwdRoots
	if len(roots) == 0 && resolver != nil {
		roots = resolver.AllowedRoots()
	}

	policy, err := NewPolicy(PolicyConfig{
		Mode:  cfg.Mode,
		Allow: cfg.Allow,
		Deny:  cfg.Deny,
		Roots: roots,
	}, logger)
	if err != nil {
		return nil, err
	}

	if cfg.MaxSessions <= 0 {
		cfg.MaxSessions = 2
	}
	if cfg.MaxOutput <= 0 {
		cfg.MaxOutput = 1 << 20
	}
	if cfg.Timeout <= 0 {
		cfg.Timeout = 30 * time.Second
	}
	if cfg.IdleTimeout <= 0 {
		cfg.IdleTimeout = 30 * time.Minute
	}
	if cfg.ScrollbackSize <= 0 {
		cfg.ScrollbackSize = 256 << 10
	}

	p := &Panel{
		cfg:         cfg,
		logger:      logger,
		resolver:    resolver,
		policy:      policy,
		accessToken: strings.TrimSpace(cfg.AccessToken),
		killGrace:   cfg.KillGrace,
		procs:       make(map[string]*execHandle),
	}
	if p.killGrace <= 0 {
		p.killGrace = 3 * time.Second
	}
	p.sessions = NewSessionRegistry(cfg, logger, policy)
	return p, nil
}

// Handler returns the HTTP handler for everything under /api/ops/.
// The host mounts it at that prefix and nothing else is required.
func (p *Panel) Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/ops/ws", p.handleWS)
	mux.HandleFunc("/api/ops/info", p.handleInfo)
	return mux
}

// ClientConfig is the subset of settings exposed to the browser. The
// terminal token is deliberately absent: the client only learns that a
// token is required, never its value.
type ClientConfig struct {
	Enabled      bool   `json:"enabled"`
	Terminal     bool   `json:"terminal"`
	Mode         string `json:"mode"`
	PTYAvailable bool   `json:"ptyAvailable"`
	MaxOutput    int64  `json:"maxOutput"`
	// TimeoutSeconds lets the client arm its own fallback deadline, so
	// a lost frame cannot leave the panel permanently busy.
	TimeoutSeconds int `json:"timeoutSeconds"`
}

// ClientConfig returns the settings safe to send to the browser.
func (p *Panel) ClientConfig() ClientConfig {
	return ClientConfig{
		Enabled:        p.cfg.Enabled,
		Terminal:       p.cfg.Enabled && strings.TrimSpace(p.cfg.TerminalToken) != "",
		Mode:           p.cfg.Mode,
		PTYAvailable:   p.cfg.PTYAvailable,
		MaxOutput:      p.cfg.MaxOutput,
		TimeoutSeconds: int(p.cfg.Timeout / time.Second),
	}
}

// Authorize reports whether a request carries the panel's access token.
// The standalone page uses this because the panel owns its own
// credential, separate from the host's browsing token.
func (p *Panel) Authorize(r *http.Request) bool {
	return p.checkExplicitToken(r) == nil
}

// handleInfo reports the client-visible configuration.
func (p *Panel) handleInfo(w http.ResponseWriter, r *http.Request) {
	if err := p.checkExplicitToken(r); err != nil {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(p.ClientConfig())
}

// Close stops all sessions. The host calls it during shutdown.
func (p *Panel) Close() {
	p.sessions.CloseAll()
}

// nopLogger discards everything; used when the host passes nil.
type nopLogger struct{}

func (nopLogger) Info(string, ...any)  {}
func (nopLogger) Warn(string, ...any)  {}
func (nopLogger) Error(string, ...any) {}
