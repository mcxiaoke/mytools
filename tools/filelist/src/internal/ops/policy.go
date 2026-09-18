package ops

import (
	"fmt"
	"path/filepath"
	"regexp"
	"strings"
)

// ── Policy: command allow/deny matching and cwd sandboxing ──────
//
// This file is the security boundary of the ops package. Two rules
// govern the design:
//
//  1. Shell-metacharacter scanning runs BEFORE any allowlist matching.
//     A command containing a separator, redirect or substitution is
//     rejected outright rather than being matched against patterns.
//     Pattern-by-pattern defence is brittle; a single scan is not.
//
//  2. deny always wins over allow. When deny is empty the built-in
//     dangerous-command set applies, mirroring how the main package
//     treats defaultExcludeFiles.

// ErrPolicyRejected is returned when a command is not permitted.
type ErrPolicyRejected struct {
	Reason string
}

func (e *ErrPolicyRejected) Error() string { return "command rejected: " + e.Reason }

func reject(reason string, args ...any) *ErrPolicyRejected {
	return &ErrPolicyRejected{Reason: fmt.Sprintf(reason, args...)}
}

// shellMetachars are characters that let one command turn into several,
// redirect output, or substitute other commands. Their presence makes
// allowlist matching meaningless, so any command containing one is
// refused before patterns are consulted.
var shellMetachars = []string{
	";", "&&", "||", "|", "`", "$(", "${", ">", "<", "&",
	"\n", "\r", "\\\n",
}

// scanMetachars reports the first shell metacharacter found in cmd.
// It returns "" when the command is free of them.
func scanMetachars(cmd string) string {
	for _, m := range shellMetachars {
		if strings.Contains(cmd, m) {
			return m
		}
	}
	return ""
}

// normalizeCommand collapses internal whitespace and trims the ends so
// that "systemctl   reload   nginx" matches the same pattern as the
// single-spaced form. Case is preserved: shell commands are case
// sensitive, and folding it would let "RM" through a pattern for "rm".
func normalizeCommand(cmd string) string {
	return strings.Join(strings.Fields(cmd), " ")
}

// builtinAllowlists are read-only commands enabled by default so the
// panel is useful out of the box without the operator having to write
// a long list of regexes. They are additive: user-configured allow
// patterns are appended to these rather than replacing them.
//
// Deliberately excluded: pagers and interactive tools (less, more,
// top, vi). They block waiting for input and belong in PTY mode, not
// in one-shot execution.
var builtinAllowlists = []string{
	// directory listing
	`^ls( -[a-zA-Z]+)*( [\w./\-*?]+)*$`,
	// file inspection
	`^(cat|head|tail|wc|stat|file|du|df)( -[a-zA-Z0-9]+)*( [\w./\-]+)+$`,
	`^tree( -[a-zA-Z]+)*( [\w./\-]+)*$`,
	// text search — no metacharacters reach here, already scanned
	`^(grep|rg)( -[a-zA-Z0-9]+)*( [\w./\-=*?]+)+$`,
	`^find [\w./\-]+( -maxdepth \d+)?( -type [fd])?( -name [\w.*?\-]+)?$`,
	// system information
	`^(ps|uptime|free|uname|hostname|whoami|id|date|env)\b[^;&|` + "`" + `]*$`,
	`^uname -[a-z]+$`,
	// network inspection
	`^(ss|netstat)( -[a-zA-Z0-9]+)*$`,
	`^ip (addr|route|link|neigh)( show)?( [\w./:]+)?$`,
	// service inspection (read-only subcommands only)
	`^systemctl (status|is-active|is-enabled|is-failed|list-units|list-unit-files|show|cat)( [\w@.\-]+)?( --no-pager)?$`,
	`^journalctl( -u [\w@.\-]+)?( -n \d+)?( --no-pager)?( --since [\w:.\-]+)?$`,
	// container inspection (read-only)
	`^docker (ps|images|logs|inspect|stats|version|info)( [\w\-./]+)*$`,
	`^docker compose (ps|logs|config|version)( [\w\-./]+)*$`,
	// version control (read-only)
	`^git (status|log|diff|show|branch|remote|tag|describe)( [\w\-./=^~:]+)*$`,
}

// defaultDenyPatterns are always rejected, even in free mode. Writing
// `deny: []` explicitly in the config clears them, matching the
// convention used by index.excludeFiles in the main package.
//
// These are anchored to command position (the start of the string)
// rather than matching anywhere. An unanchored `\bmv\b` would reject
// `ls /data/mvtest`, and `\bmount\b` would reject `cat /etc/mount.conf`
// — turning a safety net into a usability problem. Anchoring keeps the
// rule about *running* the command, not about mentioning its name.
//
// Arguments are still reachable for patterns that need them because
// the metacharacter scan has already removed every separator: a
// command that survives that scan is a single command with plain
// arguments.
var defaultDenyPatterns = []string{
	// destructive filesystem operations
	`^\s*rm\b`,
	`^\s*mkfs(\.\w+)?\b`,
	`^\s*dd\b`,
	`^\s*fdisk\b`,
	`^\s*parted\b`,
	`^\s*mv\b`,
	`^\s*truncate\b`,
	`^\s*shred\b`,
	// permission and ownership changes
	`^\s*chmod\b`,
	`^\s*chown\b`,
	`^\s*chattr\b`,
	// mounts and filesystem structure
	`^\s*mount\b`,
	`^\s*umount\b`,
	`^\s*swapoff\b`,
	// account and credential changes
	`^\s*useradd\b`,
	`^\s*userdel\b`,
	`^\s*usermod\b`,
	`^\s*passwd\b`,
	`^\s*groupadd\b`,
	`^\s*groupdel\b`,
	`^\s*visudo\b`,
	// host power state
	`^\s*(shutdown|reboot|halt|poweroff|init)\b`,
	// process and service control that can take the box down
	`^\s*kill(all)?\b`,
	`^\s*pkill\b`,
	// firewall and network reconfiguration
	`^\s*iptables\b`,
	`^\s*nft\b`,
	`^\s*ufw\b`,
	`^\s*ifconfig\b`,
	// scheduled jobs
	`^\s*crontab\b`,
	`^\s*at\b`,
	// package management (can rewrite the system)
	`^\s*(apt|apt-get|yum|dnf|pacman|zypper)\b`,
	// fork bomb, in the shape that survives a metacharacter scan
	`\(\)\s*\{\s*:\s*\|\s*:\s*&\s*\}\s*;\s*:`,
	// writes to raw block devices
	`/dev/(sd|nvme|hd|vd)`,
}

// Policy evaluates whether a command may run and where.
type Policy struct {
	mode    string // "allowlist" | "free"
	allow   []*regexp.Regexp
	deny    []*regexp.Regexp
	roots   []string // allowed cwd roots, already absolute
	logger  Logger
	freeMod bool
}

// PolicyConfig carries the raw policy settings.
type PolicyConfig struct {
	Mode  string   // allowlist | free
	Allow []string // extra patterns appended to the built-ins
	Deny  []string // empty slice means "use defaults"; nil also means defaults
	Roots []string // allowed cwd roots
}

// NewPolicy compiles the configured patterns. Deny is nil-aware: a nil
// slice falls back to the built-in dangerous set, while an explicitly
// empty (non-nil) slice disables those defaults.
func NewPolicy(pc PolicyConfig, logger Logger) (*Policy, error) {
	p := &Policy{
		mode:   strings.ToLower(strings.TrimSpace(pc.Mode)),
		roots:  pc.Roots,
		logger: logger,
	}
	if p.mode == "" {
		p.mode = "allowlist"
	}
	if p.mode != "allowlist" && p.mode != "free" {
		return nil, fmt.Errorf("ops.mode %q must be allowlist or free", pc.Mode)
	}
	p.freeMod = p.mode == "free"

	// allow: built-ins plus operator-supplied extras
	patterns := append([]string{}, builtinAllowlists...)
	patterns = append(patterns, pc.Allow...)
	for _, pat := range patterns {
		re, err := regexp.Compile(pat)
		if err != nil {
			return nil, fmt.Errorf("ops.allow pattern %q: %w", pat, err)
		}
		p.allow = append(p.allow, re)
	}

	// deny: defaults unless the operator wrote an explicit empty list
	denyPatterns := defaultDenyPatterns
	if pc.Deny != nil {
		denyPatterns = pc.Deny
	}
	for _, pat := range denyPatterns {
		re, err := regexp.Compile(pat)
		if err != nil {
			return nil, fmt.Errorf("ops.deny pattern %q: %w", pat, err)
		}
		p.deny = append(p.deny, re)
	}

	return p, nil
}

// FreeMode reports whether allowlist enforcement is disabled.
func (p *Policy) FreeMode() bool { return p.freeMod }

// CheckCommand validates a command against the metacharacter scan,
// the deny set and (in allowlist mode) the allow set.
//
// Order matters. The metacharacter scan runs on the RAW string, before
// normalization: collapsing whitespace would turn a newline separator
// into a harmless space and let `ls\nrm -rf /` through. Only after the
// scan proves the command is a single command does normalization make
// sense, so that `systemctl   reload   nginx` matches the same pattern
// as the single-spaced form.
func (p *Policy) CheckCommand(cmd string) error {
	// 1. Raw metacharacter scan — must precede normalization.
	if m := scanMetachars(cmd); m != "" {
		return reject("shell metacharacter %q is not allowed", m)
	}

	norm := normalizeCommand(cmd)
	if norm == "" {
		return reject("empty command")
	}

	// 2. Deny wins over allow, in every mode.
	stripped := stripPrivilegePrefix(norm)
	if err := p.checkDeny(stripped); err != nil {
		return err
	}

	if p.freeMod {
		return nil
	}

	// 3. Allowlist. Matching also uses the privilege-stripped form so
	// that `sudo systemctl restart nginx` is judged by the command it
	// will actually run rather than being rejected for its prefix.
	// Deny has already been consulted above, so this cannot be used to
	// smuggle a denied command through.
	for _, re := range p.allow {
		if re.MatchString(stripped) {
			return nil
		}
	}
	return reject("not in allowlist (ops.mode=allowlist)")
}

// checkDeny applies the deny patterns to a privilege-stripped command.
func (p *Policy) checkDeny(stripped string) error {
	for _, re := range p.deny {
		if re.MatchString(stripped) {
			return reject("matches deny rule %s", re.String())
		}
	}
	return nil
}

// privilegePrefixes are wrappers that run the rest of the line with
// elevated rights. They are removed before deny matching so the rule
// sees the command that will actually execute.
var privilegePrefixes = []string{"sudo ", "doas ", "pkexec ", "su -c ", "nohup ", "setsid ", "time ", "env "}

// stripPrivilegePrefix removes leading privilege-escalation wrappers,
// repeating so `sudo env rm -rf /` is reduced as well.
func stripPrivilegePrefix(cmd string) string {
	for {
		trimmed := strings.TrimSpace(cmd)
		matched := false
		for _, pfx := range privilegePrefixes {
			if strings.HasPrefix(trimmed, pfx) {
				cmd = strings.TrimSpace(strings.TrimPrefix(trimmed, pfx))
				matched = true
				break
			}
		}
		if !matched {
			return cmd
		}
	}
}

// CheckCWD verifies that a working directory sits inside one of the
// allowed roots. This bounds where commands run; it does NOT bound what
// they can read, which is why CheckCommand is the real boundary.
//
// An empty roots list means "no sandbox configured", and the check
// passes — the operator has opted out.
func (p *Policy) CheckCWD(dir string) error {
	if len(p.roots) == 0 {
		return nil
	}
	if dir == "" {
		return reject("empty working directory")
	}

	abs, err := filepath.Abs(dir)
	if err != nil {
		return reject("cannot resolve working directory: %v", err)
	}

	// Resolve symlinks so a link inside a root cannot point outside it.
	// Failure to resolve (missing path) falls back to the lexical form.
	resolved := abs
	if r, err := filepath.EvalSymlinks(abs); err == nil {
		resolved = r
	}

	for _, root := range p.roots {
		rootAbs, err := filepath.Abs(root)
		if err != nil {
			continue
		}
		rootResolved := rootAbs
		if r, err := filepath.EvalSymlinks(rootAbs); err == nil {
			rootResolved = r
		}
		if withinDir(resolved, rootResolved) {
			return nil
		}
	}
	return reject("working directory %q is outside the allowed roots", dir)
}

// withinDir reports whether target equals base or lives beneath it.
// Both paths must already be absolute and symlink-resolved.
func withinDir(target, base string) bool {
	target = filepath.Clean(target)
	base = filepath.Clean(base)
	if target == base {
		return true
	}
	return strings.HasPrefix(target, base+string(filepath.Separator))
}

// AllowedRoots returns a copy of the configured roots, for diagnostics.
func (p *Policy) AllowedRoots() []string {
	out := make([]string, len(p.roots))
	copy(out, p.roots)
	return out
}
