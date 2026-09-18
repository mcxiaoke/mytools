package main

import (
	"strings"

	"filelist/internal/ops"
)

// ── Adapters from filelist to the ops package ───────────────────
//
// The ops package declares the interfaces it needs (ops.Logger,
// ops.PathResolver) and this file satisfies them. That keeps the
// dependency arrow one-way: internal/ops never imports the indexer,
// the Server, or the embedded assets, so the whole panel can be
// deleted without touching anything below this file.

// opsLogger adapts the package-level logger to ops.Logger.
//
// The ops package calls Info/Warn/Error with printf-style format and
// args; the host logger has the same shape, so the adapter is a thin
// forward. This is deliberately the only place the two loggers meet.
type opsLogger struct{}

func (opsLogger) Info(format string, args ...any)  { logger.Info(format, args...) }
func (opsLogger) Warn(format string, args ...any)  { logger.Warn(format, args...) }
func (opsLogger) Error(format string, args ...any) { logger.Error(format, args...) }

// opsPaths adapts the indexer's virtual-to-real mapping to
// ops.PathResolver.
type opsPaths struct {
	idx *Indexer
	cfg *Config
}

// Resolve maps a virtual path such as /data/nginx to its disk path.
func (p opsPaths) Resolve(virtualPath string) (string, bool) {
	return p.idx.MapVirtualToReal(virtualPath)
}

// AllowedRoots reports which disk paths may serve as a working
// directory. An explicit ops.cwdRoots wins; otherwise the configured
// roots are used, so the sandbox matches what the operator can browse.
func (p opsPaths) AllowedRoots() []string {
	if len(p.cfg.Ops.CwdRoots) > 0 {
		return p.cfg.Ops.CwdRoots
	}
	roots := make([]string, 0, len(p.cfg.Roots))
	for _, r := range p.cfg.Roots {
		roots = append(roots, r.Path)
	}
	return roots
}

// newOpsPanel builds the panel from configuration. It returns nil when
// the feature is disabled, in which case the caller registers no
// routes at all — an unregistered path is a 404, so a disabled panel
// presents no attack surface and does not advertise itself.
func newOpsPanel(cfg *Config, idx *Indexer) (*ops.Panel, error) {
	if !cfg.OpsEnabled() {
		if cfg.Ops.Enabled {
			// Enabled but unusable. Say precisely what is missing
			// rather than naming a setting that is no longer involved:
			// the panel has its own token, so pointing at server.token
			// sends the operator looking in the wrong place.
			logger.Warn("ops: ops.enabled is true but ops.token is empty; " +
				"panel stays disabled. Set ops.token to enable it.")
			if strings.TrimSpace(cfg.Ops.TerminalToken) != "" {
				// A common configuration mistake: the terminal's second
				// factor is set, which looks like the panel is
				// configured, but it cannot enable anything on its own.
				logger.Warn("ops: ops.terminalToken is set but ops.token is not; " +
					"ops.token is the credential that enables the panel")
			}
		}
		return nil, nil
	}

	panel, err := ops.New(cfg.OpsConfig(), opsLogger{}, opsPaths{idx: idx, cfg: cfg})
	if err != nil {
		return nil, err
	}

	// Report the resolved configuration, not the raw YAML: the settings
	// have defaults applied by OpsConfig(), so reading cfg.Ops directly
	// would miss them.
	resolved := cfg.OpsConfig()
	logger.Info("ops: panel enabled (blacklist=%d keyword(s), terminal=%v, cwdRoots=%d)",
		len(resolved.Blacklist), cfg.OpsTerminalEnabled(), len(resolved.CwdRoots))
	return panel, nil
}
