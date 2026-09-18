package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ── Placeholder substitution tests ──────────────────────────────
//
// These guard a bug that reached production: the ops config was
// injected with a placeholder named __FILELIST_OPS__ that also appeared
// in the JavaScript property name `window.__FILELIST_OPS__`. ReplaceAll
// rewrote both, producing
//
//     window.{"enabled":false} = {"enabled":false};
//
// which is a syntax error. The whole <script> block failed to parse,
// so window.__FILELIST_CONFIG__ was never defined, the SPA never
// initialised, and the page showed a load error. No Go unit test and
// no WebSocket test could catch it — the page was simply never loaded
// in a browser.
//
// The lesson encoded here: assert on the SHAPE of the injected JSON,
// not merely that substitution happened.

// opsConfigFor builds the JSON the server would inject for a config.
func opsConfigFor(t *testing.T, cfg *Config) string {
	t.Helper()
	panel, err := newOpsPanel(cfg, nil)
	if err != nil {
		t.Fatalf("newOpsPanel: %v", err)
	}
	if panel == nil {
		return `{"enabled":false}`
	}
	defer panel.Close()

	b, err := json.Marshal(panel.ClientConfig())
	if err != nil {
		t.Fatalf("marshal client config: %v", err)
	}
	return string(b)
}

// renderPage performs the same substitutions pageHTML does, so the
// test exercises the real template rather than a copy.
func renderPage(t *testing.T, cfg *Config) string {
	t.Helper()
	srv := NewServer(cfg, nil)
	panel, err := newOpsPanel(cfg, nil)
	if err != nil {
		t.Fatalf("newOpsPanel: %v", err)
	}
	if panel != nil {
		defer panel.Close()
		srv.SetOps(panel)
	}
	return string(srv.pageHTML())
}

func baseTestConfig() *Config {
	cfg := &Config{}
	cfg.Server.BasePath = "/files"
	cfg.Server.Token = "browse-token"
	cfg.Ops.Enabled = true
	cfg.Ops.Token = "ops-token"
	cfg.Ops.TerminalToken = "term-token"
	cfg.Roots = []RootMapping{{URL: "/data", Path: "/tmp"}}
	return cfg
}

// TestPageHTML_NoUnsubstitutedPlaceholders ensures every injected
// marker is resolved. A leftover marker inside the JavaScript block is
// a syntax error, not a cosmetic problem.
//
// window.__FILELIST_CONFIG__ is a JavaScript identifier, not a marker,
// so it is expected to survive; the test looks only for the
// __FILELIST_*_ shaped markers the server substitutes.
func TestPageHTML_NoUnsubstitutedPlaceholders(t *testing.T) {
	cfg := baseTestConfig()
	out := renderPage(t, cfg)

	re := regexp.MustCompile(`__FILELIST_[A-Z_]+_?__`)
	found := re.FindAllString(out, -1)

	// Filter out the one legitimate identifier.
	var leftovers []string
	for _, f := range found {
		if f == "__FILELIST_CONFIG__" {
			continue
		}
		leftovers = append(leftovers, f)
	}
	if len(leftovers) > 0 {
		t.Errorf("page still contains unsubstituted placeholders: %v", leftovers)
	}
}

// TestPageHTML_OpsConfigIsValidJavaScript is the regression test for
// the production bug. It extracts the config script block and asserts
// the ops value is a well-formed JSON object on the RIGHT-HAND side of
// an assignment — the failure mode was the placeholder landing in the
// property name.
func TestPageHTML_OpsConfigIsValidJavaScript(t *testing.T) {
	cfg := baseTestConfig()
	out := renderPage(t, cfg)

	// The placeholder must never appear inside a property name.
	if strings.Contains(out, "window.{\"") {
		t.Fatalf("ops config was substituted into a property name:\n%s",
			extractConfigBlock(out))
	}

	// The assignment must read `ops: { ... }`.
	re := regexp.MustCompile(`ops:\s*(\{[^\n]*\})`)
	m := re.FindStringSubmatch(out)
	if m == nil {
		t.Fatalf("no `ops: {...}` assignment found in the page:\n%s",
			extractConfigBlock(out))
	}

	var parsed map[string]any
	if err := json.Unmarshal([]byte(m[1]), &parsed); err != nil {
		t.Fatalf("ops value is not valid JSON: %v\nvalue: %s", err, m[1])
	}
	if parsed["enabled"] != true {
		t.Errorf("ops.enabled = %v, want true", parsed["enabled"])
	}
}

// TestPageHTML_OpsDisabledStillValid verifies the disabled case emits
// the same shape, so a disabled panel cannot break the page either.
func TestPageHTML_OpsDisabledStillValid(t *testing.T) {
	cfg := baseTestConfig()
	cfg.Ops.Enabled = false

	out := renderPage(t, cfg)

	re := regexp.MustCompile(`ops:\s*(\{[^\n]*\})`)
	m := re.FindStringSubmatch(out)
	if m == nil {
		t.Fatalf("no ops assignment found when disabled:\n%s", extractConfigBlock(out))
	}
	var parsed map[string]any
	if err := json.Unmarshal([]byte(m[1]), &parsed); err != nil {
		t.Fatalf("disabled ops value is not valid JSON: %v", err)
	}
	if parsed["enabled"] != false {
		t.Errorf("ops.enabled = %v, want false", parsed["enabled"])
	}
}

// TestPageHTML_NoTerminalTokenLeak makes sure the second factor never
// reaches the browser.
func TestPageHTML_NoTerminalTokenLeak(t *testing.T) {
	cfg := baseTestConfig()
	cfg.Ops.TerminalToken = "super-secret-terminal-token"

	out := renderPage(t, cfg)
	if strings.Contains(out, "super-secret-terminal-token") {
		t.Error("page leaked the terminal token")
	}
	if strings.Contains(out, "ops-token") {
		t.Error("page leaked the ops access token")
	}
}

// extractConfigBlock returns the config script block for error output.
func extractConfigBlock(page string) string {
	start := strings.Index(page, "window.__FILELIST_CONFIG__")
	if start < 0 {
		return "(config block not found)"
	}
	end := strings.Index(page[start:], "</script>")
	if end < 0 {
		end = len(page) - start
	}
	return page[start : start+end]
}

// ── ops.html (standalone page) ──────────────────────────────────

// TestOpsPage_PlaceholdersResolved verifies the standalone page gets
// the same treatment as the main page. It previously served the raw
// template, so it rendered with literal placeholders and no config.
func TestOpsPage_PlaceholdersResolved(t *testing.T) {
	tmpl, err := webFS.ReadFile("web/static/ops/ops.html")
	if err != nil {
		t.Fatalf("ops.html missing: %v", err)
	}
	raw := string(tmpl)

	// The template must contain the JSON placeholder, and the property
	// name it assigns to must not itself be a placeholder.
	if !strings.Contains(raw, opsPlaceholder) {
		t.Errorf("ops.html does not contain %s", opsPlaceholder)
	}
	if strings.Contains(raw, "window."+opsPlaceholder) {
		t.Errorf("ops.html uses the placeholder as a property name, which "+
			"substitution would rewrite: %s", opsPlaceholder)
	}

	// Simulate the substitution handleOpsPage performs.
	out := strings.ReplaceAll(raw, opsPlaceholder, `{"enabled":true}`)
	re := regexp.MustCompile(`ops:\s*(\{[^\n]*\})`)
	m := re.FindStringSubmatch(out)
	if m == nil {
		t.Fatalf("ops.html has no ops assignment after substitution:\n%s", out)
	}
	var parsed map[string]any
	if err := json.Unmarshal([]byte(m[1]), &parsed); err != nil {
		t.Fatalf("ops.html ops value is not valid JSON: %v", err)
	}
}

// ── ops.js contract ─────────────────────────────────────────────

// TestOpsJS_ReadsConfigFromSinglePlace checks the module reads its
// config from the host's config object rather than a bare global, so
// the contract stays at one property.
func TestOpsJS_ReadsConfigFromSinglePlace(t *testing.T) {
	src, err := os.ReadFile(filepath.Join("web", "static", "ops", "ops.js"))
	if err != nil {
		t.Fatalf("ops.js missing: %v", err)
	}
	js := string(src)

	if !strings.Contains(js, "__FILELIST_CONFIG__") {
		t.Error("ops.js does not read window.__FILELIST_CONFIG__")
	}
	if strings.Contains(js, "window.__FILELIST_OPS__") {
		t.Error("ops.js still reads the old bare global")
	}
}

// ── token separation ────────────────────────────────────────────

// TestOpsEnabledRequiresOwnToken pins the separation between the
// browsing token and the panel token: browsing stays simple, and the
// panel's credential can be revoked on its own.
func TestOpsEnabledRequiresOwnToken(t *testing.T) {
	// server.token alone must NOT enable the panel.
	cfg := &Config{}
	cfg.Server.Token = "browse-token"
	cfg.Ops.Enabled = true
	if cfg.OpsEnabled() {
		t.Error("ops became enabled from server.token alone")
	}

	// A dedicated ops token enables it.
	cfg.Ops.Token = "ops-token"
	if !cfg.OpsEnabled() {
		t.Error("ops did not enable with its own token")
	}

	// Disabled master switch still wins.
	cfg.Ops.Enabled = false
	if cfg.OpsEnabled() {
		t.Error("ops enabled despite enabled=false")
	}
}

// TestOpsPathDetection covers the routes that skip the global auth
// middleware because the panel authenticates them itself.
func TestOpsPathDetection(t *testing.T) {
	yes := []string{"/api/ops/ws", "/api/ops/info", "/ops", "/ops.html"}
	no := []string{"/", "/api/list", "/api/stats", "/static/ops/ops.js", "/raw/data/x"}

	for _, p := range yes {
		if !isOpsPath(p) {
			t.Errorf("isOpsPath(%q) = false, want true", p)
		}
	}
	for _, p := range no {
		if isOpsPath(p) {
			t.Errorf("isOpsPath(%q) = true, want false", p)
		}
	}
}
