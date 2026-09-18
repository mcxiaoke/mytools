package ops

import (
	"fmt"
	"path/filepath"
	"strings"
)

// ── Policy: keyword blacklist and cwd sandboxing ────────────────
//
// The command boundary is one deliberately simple rule: a command is
// refused when it begins with a blacklisted keyword. There is no
// allowlist, no regular expression and no mode switch.
//
// The previous design required the operator to enumerate every safe
// command before anything could run, which made the panel awkward for
// the task it exists for (restarting a service after editing a config)
// and pushed people toward `mode: free`, where nothing but a regex
// deny list applied. A short blacklist of destructive keywords plus a
// permissive default is easier to reason about and, in practice,
// safer.
//
// Two supporting rules keep prefix matching honest:
//
//  1. Shell-metacharacter scanning runs BEFORE matching. A command
//     containing a separator, redirect or substitution is refused
//     outright: otherwise `ls; rm -rf /` would be judged by its
//     harmless first word while the shell runs the rest.
//
//  2. Privilege wrappers (sudo, doas, ...) are stripped first, so
//     `sudo rm -rf /` is judged by the command it will actually run.

// ErrPolicyRejected is returned when a command is not permitted.
type ErrPolicyRejected struct {
	Reason string
}

func (e *ErrPolicyRejected) Error() string { return "command rejected: " + e.Reason }

func reject(reason string, args ...any) *ErrPolicyRejected {
	return &ErrPolicyRejected{Reason: fmt.Sprintf(reason, args...)}
}

// shellMetachars are characters that let one command turn into
// several, redirect output, or substitute other commands. Their
// presence makes any prefix comparison meaningless.
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
// that "  rm   -rf  /  " is compared as "rm -rf /".
func normalizeCommand(cmd string) string {
	return strings.Join(strings.Fields(cmd), " ")
}

// builtinBlacklist is the minimal keyword set refused out of the box.
// It lists commands that destroy data, change ownership or permissions,
// reconfigure the host, or rewrite the package database — the things a
// mistyped command in a browser panel must never do.
//
// Matching is a plain prefix test, so "rm" covers "rmdir" and
// "rm -rf /" alike. The list is deliberately short: whatever an
// operator needs beyond it goes in ops.blacklist.
//
// "mv" is intentionally absent: as a prefix it would also refuse
// "mvn", and moving files is a routine operation. Add it through
// ops.blacklist when a stricter set is wanted.
var builtinBlacklist = []string{
	// destructive filesystem operations
	"rm", "dd", "mkfs", "shred", "truncate",
	// permission and ownership changes
	"chmod", "chown", "chattr",
	// mounts and filesystem structure
	"mount", "umount",
	// host power state
	"shutdown", "reboot", "halt", "poweroff",
	// process control that can take the box down
	"kill", "pkill",
	// accounts and credentials
	"useradd", "userdel", "passwd", "crontab",
	// firewall
	"iptables", "ufw",
	// package management
	"apt", "yum", "dnf", "pacman",
}

// matchBlacklist reports whether cmd begins with one of the keywords,
// returning the keyword that matched. It is a pure function: no state,
// no regular expressions, no I/O.
//
// The comparison is case-insensitive and a plain prefix test, so
// "RM -rf /" matches "rm" and "rmdir old" matches it as well. Only the
// START of the command is examined: "grep rm /var/log/syslog" is a
// legitimate read and is not refused.
func matchBlacklist(cmd string, keywords []string) (string, bool) {
	lower := strings.ToLower(cmd)
	for _, kw := range keywords {
		if kw == "" {
			continue
		}
		if strings.HasPrefix(lower, kw) {
			return kw, true
		}
	}
	return "", false
}

// Policy evaluates whether a command may run and where.
type Policy struct {
	blacklist []string
	roots     []string // allowed cwd roots, already absolute
}

// PolicyConfig carries the raw policy settings.
type PolicyConfig struct {
	// Blacklist holds extra keywords, appended to the built-ins.
	Blacklist []string
	// Roots are the allowed cwd roots.
	Roots []string
}

// NewPolicy builds a policy. The built-in keyword set always applies;
// the configured entries are appended to it.
func NewPolicy(pc PolicyConfig) *Policy {
	list := make([]string, 0, len(builtinBlacklist)+len(pc.Blacklist))
	list = append(list, builtinBlacklist...)
	for _, kw := range pc.Blacklist {
		kw = strings.ToLower(strings.TrimSpace(kw))
		if kw != "" {
			list = append(list, kw)
		}
	}
	return &Policy{blacklist: list, roots: pc.Roots}
}

// CheckCommand validates a command against the metacharacter scan and
// the keyword blacklist.
//
// Order matters. The metacharacter scan runs on the RAW string, before
// normalization: collapsing whitespace would turn a newline separator
// into a harmless space and let `ls\nrm -rf /` through. Only after the
// scan proves the command is a single command does normalization make
// sense, so that `rm   -rf   /` matches the same keyword as the
// single-spaced form.
func (p *Policy) CheckCommand(cmd string) error {
	// 1. Raw metacharacter scan — must precede normalization.
	if m := scanMetachars(cmd); m != "" {
		return reject("shell metacharacter %q is not allowed", m)
	}

	norm := normalizeCommand(cmd)
	if norm == "" {
		return reject("empty command")
	}

	// 2. Blacklist. The privilege-stripped form is matched so that
	// `sudo rm -rf /` is judged by the command it will actually run
	// rather than slipping past on the "sudo" prefix.
	if kw, ok := matchBlacklist(stripPrivilegePrefix(norm), p.blacklist); ok {
		return reject("matches blacklist keyword %q", kw)
	}

	return nil
}

// privilegePrefixes are wrappers that run the rest of the line with
// elevated rights. They are removed before blacklist matching so the
// rule sees the command that will actually execute.
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
