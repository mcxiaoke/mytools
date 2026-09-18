package ops

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/coder/websocket"
)

// ── WebSocket handshake security tests ──────────────────────────
//
// These are the highest-value tests in the package. The panel is
// authenticated by a cookie that is SameSite=Lax, and Lax does not
// prevent a cross-site WebSocket handshake. Origin validation is
// therefore the only thing standing between a page the operator
// happens to visit and a root shell on their intranet host.

// newTestPanel builds a panel with a resolver that maps a virtual
// root onto a temp directory, so tests never touch a real path.
func newTestPanel(t *testing.T, cfg Config) *Panel {
	t.Helper()
	if cfg.AccessToken == "" {
		cfg.AccessToken = "test-token"
	}
	if cfg.Timeout == 0 {
		cfg.Timeout = 5 * time.Second
	}
	if cfg.MaxSessions == 0 {
		cfg.MaxSessions = 2
	}
	if cfg.MaxOutput == 0 {
		cfg.MaxOutput = 1 << 16
	}
	if cfg.IdleTimeout == 0 {
		cfg.IdleTimeout = time.Minute
	}
	if cfg.ScrollbackSize == 0 {
		cfg.ScrollbackSize = 4096
	}
	p, err := New(cfg, nopLogger{}, fakeResolver{root: t.TempDir()})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	t.Cleanup(p.Close)
	return p
}

// fakeResolver stands in for the host's indexer. Its existence is the
// point of the interface: the package is testable without filelist.
type fakeResolver struct{ root string }

// testCookieName mirrors the host's session cookie name. The ops
// package never reads that cookie itself; it appears here only so the
// tests can prove that a cookie alone does not authorise an upgrade.
const testCookieName = "filelist_token"

func (f fakeResolver) Resolve(virtualPath string) (string, bool) {
	if virtualPath == "" {
		return f.root, true
	}
	return f.root, true
}

func (f fakeResolver) AllowedRoots() []string { return nil }

// dialAttempt performs a WebSocket handshake with the given headers and
// query, returning the HTTP response.
func dialAttempt(t *testing.T, srv *httptest.Server, origin, token string, useCookie bool) *http.Response {
	t.Helper()

	url := "ws" + strings.TrimPrefix(srv.URL, "http") + "/api/ops/ws"
	if token != "" {
		url += "?token=" + token
	}

	hdr := http.Header{}
	if origin != "" {
		hdr.Set("Origin", origin)
	}
	if useCookie {
		// Simulates the browser attaching the session cookie
		// automatically — the exact behaviour that makes the origin
		// check necessary.
		hdr.Set("Cookie", testCookieName+"=test-token")
	}

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	conn, resp, err := websocket.Dial(ctx, url, &websocket.DialOptions{HTTPHeader: hdr})
	if err == nil {
		conn.CloseNow()
	}
	if resp == nil {
		t.Fatalf("dial returned no response (err=%v)", err)
	}
	return resp
}

func TestWS_MissingOriginRejected(t *testing.T) {
	p := newTestPanel(t, Config{})
	srv := httptest.NewServer(p.Handler())
	defer srv.Close()

	resp := dialAttempt(t, srv, "", "test-token", false)
	if resp.StatusCode != http.StatusForbidden {
		t.Errorf("missing Origin: status = %d, want 403", resp.StatusCode)
	}
}

// TestWS_CrossSiteOriginRejected is the CSWSH test. A page served from
// evil.example.com must not be able to open the socket even though the
// browser would happily attach the session cookie.
func TestWS_CrossSiteOriginRejected(t *testing.T) {
	p := newTestPanel(t, Config{})
	srv := httptest.NewServer(p.Handler())
	defer srv.Close()

	resp := dialAttempt(t, srv, "http://evil.example.com", "test-token", true)
	if resp.StatusCode != http.StatusForbidden {
		t.Errorf("cross-site Origin: status = %d, want 403", resp.StatusCode)
	}
}

// TestWS_CookieAloneIsNotEnough verifies the second condition: even
// with a valid cookie attached automatically, the upgrade fails
// without an explicitly presented token.
func TestWS_CookieAloneIsNotEnough(t *testing.T) {
	p := newTestPanel(t, Config{})
	srv := httptest.NewServer(p.Handler())
	defer srv.Close()

	resp := dialAttempt(t, srv, srv.URL, "", true)
	if resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("cookie without explicit token: status = %d, want 401", resp.StatusCode)
	}
}

func TestWS_WrongTokenRejected(t *testing.T) {
	p := newTestPanel(t, Config{})
	srv := httptest.NewServer(p.Handler())
	defer srv.Close()

	resp := dialAttempt(t, srv, srv.URL, "wrong-token", false)
	if resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("wrong token: status = %d, want 401", resp.StatusCode)
	}
}

func TestWS_NoTokenConfiguredFailsClosed(t *testing.T) {
	// A panel with no access token must refuse everything rather than
	// expose an unauthenticated root shell.
	p, err := New(Config{AccessToken: ""}, nopLogger{}, fakeResolver{root: t.TempDir()})
	if err != nil {
		t.Fatal(err)
	}
	defer p.Close()

	srv := httptest.NewServer(p.Handler())
	defer srv.Close()

	resp := dialAttempt(t, srv, srv.URL, "", false)
	if resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("no token configured: status = %d, want 401", resp.StatusCode)
	}
}

func TestWS_ValidOriginAndTokenAccepted(t *testing.T) {
	p := newTestPanel(t, Config{})
	srv := httptest.NewServer(p.Handler())
	defer srv.Close()

	resp := dialAttempt(t, srv, srv.URL, "test-token", false)
	if resp.StatusCode != http.StatusSwitchingProtocols {
		t.Errorf("valid handshake: status = %d, want 101", resp.StatusCode)
	}
}

func TestWS_ExplicitOriginPatternOverrides(t *testing.T) {
	p := newTestPanel(t, Config{OriginPatterns: []string{"allowed.example.com"}})
	srv := httptest.NewServer(p.Handler())
	defer srv.Close()

	// The configured pattern is honoured even though it differs from
	// the request host — this is what makes reverse-proxy deployments
	// workable.
	resp := dialAttempt(t, srv, "http://allowed.example.com", "test-token", false)
	if resp.StatusCode != http.StatusSwitchingProtocols {
		t.Errorf("configured origin pattern: status = %d, want 101", resp.StatusCode)
	}

	// Anything else is still refused.
	resp2 := dialAttempt(t, srv, "http://evil.example.com", "test-token", false)
	if resp2.StatusCode != http.StatusForbidden {
		t.Errorf("unlisted origin: status = %d, want 403", resp2.StatusCode)
	}
}

func TestWS_WildcardOriginAllowsMissingOrigin(t *testing.T) {
	p := newTestPanel(t, Config{OriginPatterns: []string{"*"}})
	srv := httptest.NewServer(p.Handler())
	defer srv.Close()

	resp := dialAttempt(t, srv, "", "test-token", false)
	if resp.StatusCode != http.StatusSwitchingProtocols {
		t.Errorf("wildcard origin with no Origin header: status = %d, want 101", resp.StatusCode)
	}
}

func TestWS_BearerHeaderAccepted(t *testing.T) {
	p := newTestPanel(t, Config{})
	srv := httptest.NewServer(p.Handler())
	defer srv.Close()

	hdr := http.Header{}
	hdr.Set("Origin", srv.URL)
	hdr.Set("Authorization", "Bearer test-token")

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	conn, resp, err := websocket.Dial(ctx,
		"ws"+strings.TrimPrefix(srv.URL, "http")+"/api/ops/ws",
		&websocket.DialOptions{HTTPHeader: hdr})
	if err == nil {
		conn.CloseNow()
	}
	if resp == nil || resp.StatusCode != http.StatusSwitchingProtocols {
		t.Fatalf("bearer handshake failed: %v", resp)
	}
}

// ── /api/ops/info ───────────────────────────────────────────────

// infoRequest fetches the info endpoint with a token.
func infoRequest(t *testing.T, base, token string) string {
	t.Helper()
	url := base + "/api/ops/info"
	if token != "" {
		url += "?token=" + token
	}
	resp, err := http.Get(url)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()

	buf := make([]byte, 4096)
	n, _ := resp.Body.Read(buf)
	return string(buf[:n])
}

func TestInfoEndpointHidesTerminalToken(t *testing.T) {
	// Enabled=true so the terminal flag reflects the token being set.
	p := newTestPanel(t, Config{Enabled: true, TerminalToken: "super-secret"})
	srv := httptest.NewServer(p.Handler())
	defer srv.Close()

	body := infoRequest(t, srv.URL, "test-token")

	if strings.Contains(body, "super-secret") {
		t.Errorf("info endpoint leaked the terminal token: %s", body)
	}
	if !strings.Contains(body, `"terminal":true`) {
		t.Errorf("info endpoint should report terminal availability: %s", body)
	}
}

// TestInfoEndpointRequiresToken verifies the info endpoint is not a
// way to probe the panel's configuration without a credential.
func TestInfoEndpointRequiresToken(t *testing.T) {
	p := newTestPanel(t, Config{Enabled: true, TerminalToken: "super-secret"})
	srv := httptest.NewServer(p.Handler())
	defer srv.Close()

	body := infoRequest(t, srv.URL, "")
	if strings.Contains(body, `"terminal"`) {
		t.Errorf("info endpoint answered without a token: %s", body)
	}
}

// TestInfoEndpointTerminalFalseWithoutToken verifies the second factor
// is reported as unavailable when no terminal token is configured,
// even though the panel itself is enabled.
func TestInfoEndpointTerminalFalseWithoutToken(t *testing.T) {
	p := newTestPanel(t, Config{Enabled: true, TerminalToken: ""})
	srv := httptest.NewServer(p.Handler())
	defer srv.Close()

	body := infoRequest(t, srv.URL, "test-token")
	if !strings.Contains(body, `"terminal":false`) {
		t.Errorf("terminal should be false without a terminal token: %s", body)
	}
}

// ── Authorize (used by the standalone /ops page) ────────────────

func TestAuthorize(t *testing.T) {
	p := newTestPanel(t, Config{Enabled: true})

	req := httptest.NewRequest("GET", "/ops?token=test-token", nil)
	if !p.Authorize(req) {
		t.Error("Authorize rejected a valid token")
	}

	req = httptest.NewRequest("GET", "/ops", nil)
	if p.Authorize(req) {
		t.Error("Authorize accepted a request without a token")
	}

	req = httptest.NewRequest("GET", "/ops?token=wrong", nil)
	if p.Authorize(req) {
		t.Error("Authorize accepted a wrong token")
	}
}

// TestAuthorizeIgnoresCookie documents that the panel never trusts the
// host's session cookie: only an explicitly presented token counts.
func TestAuthorizeIgnoresCookie(t *testing.T) {
	p := newTestPanel(t, Config{Enabled: true})

	req := httptest.NewRequest("GET", "/ops", nil)
	req.AddCookie(&http.Cookie{Name: testCookieName, Value: "test-token"})
	if p.Authorize(req) {
		t.Error("Authorize accepted a cookie without an explicit token")
	}
}

// ── Terminal token checks ───────────────────────────────────────

func TestCheckTerminalToken(t *testing.T) {
	p := newTestPanel(t, Config{TerminalToken: "term-secret"})

	if err := p.checkTerminalToken("term-secret"); err != nil {
		t.Errorf("correct terminal token rejected: %v", err)
	}
	if err := p.checkTerminalToken("wrong"); err == nil {
		t.Error("wrong terminal token accepted")
	}
	if err := p.checkTerminalToken(""); err == nil {
		t.Error("empty terminal token accepted")
	}
}

func TestCheckTerminalToken_DisabledWhenUnset(t *testing.T) {
	p := newTestPanel(t, Config{})
	if err := p.checkTerminalToken("anything"); err == nil {
		t.Error("terminal should be disabled when no terminal token is configured")
	}
}
