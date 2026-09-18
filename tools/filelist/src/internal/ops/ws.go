package ops

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/coder/websocket"
)

// ── WebSocket transport ─────────────────────────────────────────
//
// Origin validation here is the single defence against cross-site
// WebSocket hijacking. The host authenticates with a cookie, and the
// cookie is SameSite=Lax — which does not stop a cross-site WebSocket
// handshake. Without the checks below, any page the operator visits
// could silently open /api/ops/ws against their intranet filelist and
// obtain a root shell.
//
// Two independent conditions are therefore required to upgrade:
//   1. the Origin header must match an allowed pattern, and
//   2. a token must be presented explicitly in the request.
// Neither relies on the cookie being sent automatically.

// clientFrame is a message from the browser.
type clientFrame struct {
	Type string `json:"type"`
	SID  string `json:"sid,omitempty"`
	Cmd  string `json:"cmd,omitempty"`
	CWD  string `json:"cwd,omitempty"`
	Data string `json:"data,omitempty"`
	Cols int    `json:"cols,omitempty"`
	Rows int    `json:"rows,omitempty"`
	Name string `json:"name,omitempty"`
	// Token carries the terminal token for openpty. It is never
	// logged and never echoed back.
	Token string `json:"token,omitempty"`
}

// serverFrame is a message to the browser.
type serverFrame struct {
	Type       string `json:"type"`
	SID        string `json:"sid,omitempty"`
	Kind       string `json:"kind,omitempty"`
	CWD        string `json:"cwd,omitempty"`
	Data       string `json:"data,omitempty"`
	Code       *int   `json:"code,omitempty"`
	DurationMS int64  `json:"durationMs,omitempty"`
	Truncated  bool   `json:"truncated,omitempty"`
	TimedOut   bool   `json:"timedOut,omitempty"`
	Killed     bool   `json:"killed,omitempty"`
	ErrCode    string `json:"errCode,omitempty"`
	Msg        string `json:"msg,omitempty"`
}

// wsWriteTimeout bounds a single frame write.
const wsWriteTimeout = 10 * time.Second

// handleWS upgrades the connection and services frames.
func (p *Panel) handleWS(w http.ResponseWriter, r *http.Request) {
	// ── Condition 1: Origin must be allowed ──
	if err := p.checkOrigin(r); err != nil {
		p.logger.Warn("ops: rejected websocket upgrade from %s: %v", r.RemoteAddr, err)
		http.Error(w, "forbidden", http.StatusForbidden)
		return
	}

	// ── Condition 2: an explicit token must be presented ──
	// The cookie is deliberately not consulted: it would be attached
	// automatically by the browser and defeats the origin check.
	if err := p.checkExplicitToken(r); err != nil {
		p.logger.Warn("ops: websocket upgrade without explicit token from %s", r.RemoteAddr)
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}

	conn, err := websocket.Accept(w, r, &websocket.AcceptOptions{
		// Origin is validated above; skipping the library's own check
		// avoids rejecting legitimate reverse-proxy host mismatches.
		InsecureSkipVerify: true,
	})
	if err != nil {
		p.logger.Warn("ops: websocket accept failed: %v", err)
		return
	}
	defer conn.CloseNow()

	ctx := r.Context()
	p.serveConn(ctx, conn, r)
}

// checkOrigin validates the Origin header against the configured
// patterns, falling back to the request host when none are set.
func (p *Panel) checkOrigin(r *http.Request) error {
	origin := strings.TrimSpace(r.Header.Get("Origin"))

	// A missing Origin is refused by default: a legitimate browser
	// WebSocket client always sends one, so its absence suggests a
	// non-browser client that should be using the token path.
	if origin == "" {
		if len(p.cfg.OriginPatterns) == 0 {
			return errNoOrigin
		}
		// Explicit patterns present: allow empty origin only if the
		// operator listed the sentinel "*".
		for _, pat := range p.cfg.OriginPatterns {
			if pat == "*" {
				return nil
			}
		}
		return errNoOrigin
	}

	u, err := url.Parse(origin)
	if err != nil {
		return errBadOrigin
	}
	originHost := u.Host

	// Operator-supplied patterns win when present.
	if len(p.cfg.OriginPatterns) > 0 {
		for _, pat := range p.cfg.OriginPatterns {
			if pat == "*" {
				return nil
			}
			if hostMatch(pat, originHost) {
				return nil
			}
		}
		return errOriginMismatch
	}

	// Default: the origin must match the host the client connected to.
	// This is what stops a page served from evil.example.com from
	// opening a socket to the intranet host.
	if hostMatch(r.Host, originHost) {
		return nil
	}
	return errOriginMismatch
}

// hostMatch compares two host[:port] values, tolerating a pattern that
// omits the port.
func hostMatch(pattern, host string) bool {
	pattern = strings.ToLower(strings.TrimSpace(pattern))
	host = strings.ToLower(strings.TrimSpace(host))
	if pattern == "" || host == "" {
		return false
	}
	if pattern == host {
		return true
	}
	// Pattern without a port matches any port.
	if !strings.Contains(pattern, ":") {
		if h, _, ok := strings.Cut(host, ":"); ok {
			return pattern == h
		}
	}
	return false
}

// checkExplicitToken requires the access token in the query string or
// an Authorization header, and compares it in constant time.
func (p *Panel) checkExplicitToken(r *http.Request) error {
	want := strings.TrimSpace(p.accessToken)
	if want == "" {
		// No token configured means the host should not have enabled
		// the panel at all; fail closed rather than allowing open
		// access to a root shell.
		return errNoTokenConfigured
	}

	got := strings.TrimSpace(r.URL.Query().Get("token"))
	if got == "" {
		if h := r.Header.Get("Authorization"); strings.HasPrefix(h, "Bearer ") {
			got = strings.TrimSpace(strings.TrimPrefix(h, "Bearer "))
		}
	}
	if got == "" {
		return errNoToken
	}
	if subtle.ConstantTimeCompare([]byte(got), []byte(want)) != 1 {
		return errBadToken
	}
	return nil
}

// checkTerminalToken validates the second factor required for
// interactive terminals.
func (p *Panel) checkTerminalToken(got string) error {
	want := strings.TrimSpace(p.cfg.TerminalToken)
	if want == "" {
		return errTerminalDisabled
	}
	got = strings.TrimSpace(got)
	if got == "" {
		return errNoTerminalToken
	}
	if subtle.ConstantTimeCompare([]byte(got), []byte(want)) != 1 {
		return errBadTerminalToken
	}
	return nil
}

// serveConn reads frames until the connection closes.
func (p *Panel) serveConn(ctx context.Context, conn *websocket.Conn, r *http.Request) {
	clientIP := clientIP(r)
	p.logger.Info("ops: websocket connected from %s", clientIP)

	// Track sessions opened on this connection so they can be torn
	// down when it closes.
	active := map[string]context.CancelFunc{}

	defer func() {
		for sid, cancel := range active {
			cancel()
			p.sessions.Remove(sid)
		}
		p.logger.Info("ops: websocket disconnected from %s", clientIP)
	}()

	for {
		typ, data, err := conn.Read(ctx)
		if err != nil {
			return
		}
		if typ != websocket.MessageText {
			continue
		}

		var f clientFrame
		if err := json.Unmarshal(data, &f); err != nil {
			p.send(ctx, conn, serverFrame{Type: "err", ErrCode: "bad_frame", Msg: "malformed frame"})
			continue
		}

		switch f.Type {
		case "ping":
			p.send(ctx, conn, serverFrame{Type: "pong"})

		case "exec":
			p.handleExecFrame(ctx, conn, f, clientIP)

		case "attach":
			p.handleAttach(ctx, conn, f)

		case "openpty":
			p.handleOpenPTY(ctx, conn, f, clientIP, active)

		case "input":
			p.handleInput(ctx, conn, f)

		case "resize":
			p.handleResize(f)

		case "signal":
			p.handleSignal(ctx, conn, f)

		case "close":
			if cancel, ok := active[f.SID]; ok {
				cancel()
				delete(active, f.SID)
			}
			p.sessions.Remove(f.SID)

		default:
			p.send(ctx, conn, serverFrame{Type: "err", ErrCode: "bad_frame", Msg: "unknown frame type"})
		}
	}
}

// send writes a frame, ignoring failures (the reader will notice).
func (p *Panel) send(ctx context.Context, conn *websocket.Conn, f serverFrame) {
	_ = p.sendRaw(ctx, conn, f)
}

// sendRaw writes a frame and reports the error, for callers that need
// to know whether the peer is still there (the streaming exec pump
// stops when a write fails).
func (p *Panel) sendRaw(ctx context.Context, conn *websocket.Conn, f serverFrame) error {
	b, err := json.Marshal(f)
	if err != nil {
		return err
	}
	wctx, cancel := context.WithTimeout(ctx, wsWriteTimeout)
	defer cancel()
	return conn.Write(wctx, websocket.MessageText, b)
}

// clientIP extracts the peer address for the audit log.
func clientIP(r *http.Request) string {
	if fwd := r.Header.Get("X-Forwarded-For"); fwd != "" {
		if i := strings.Index(fwd, ","); i > 0 {
			return strings.TrimSpace(fwd[:i])
		}
		return strings.TrimSpace(fwd)
	}
	return r.RemoteAddr
}
