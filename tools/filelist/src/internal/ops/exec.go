package ops

import (
	"bytes"
	"context"
	"io"
	"os"
	"os/exec"
	"strings"
	"time"
)

// ── One-shot execution ──────────────────────────────────────────
//
// One-shot mode runs a command with plain pipes rather than a PTY.
// That keeps output free of shell echo and carriage-return noise, and
// — most importantly — makes the real exit code available without the
// `; echo $?` sentinel trick a PTY would require.

// ExecResult is the outcome of a one-shot command.
type ExecResult struct {
	ExitCode   int
	DurationMS int64
	Truncated  bool
	Output     []byte
}

// execEnvAllowlist limits which environment variables reach the child.
// The server's own environment holds access tokens and the delete
// token; inheriting it wholesale would let a single `env` command leak
// every credential. Only what a shell genuinely needs is passed.
var execEnvAllowlist = []string{"PATH", "HOME", "LANG", "LC_ALL", "TERM", "USER", "SHELL", "TMPDIR"}

// buildEnv constructs the child environment from the allowlist.
func buildEnv() []string {
	env := make([]string, 0, len(execEnvAllowlist))
	for _, k := range execEnvAllowlist {
		if v, ok := os.LookupEnv(k); ok {
			env = append(env, k+"="+v)
		}
	}
	// A predictable TERM avoids colour codes leaking into one-shot
	// output when the variable happens to be unset.
	env = append(env, "TERM=dumb")
	return env
}

// RunExec executes cmd once in dir and returns its combined output.
//
// The command is validated by the policy before anything is spawned.
// Output is capped at cfg.MaxOutput; anything beyond that is dropped
// and flagged, so `yes` cannot exhaust memory.
func (p *Panel) RunExec(ctx context.Context, cmd, dir string) (*ExecResult, error) {
	if err := p.policy.CheckCommand(cmd); err != nil {
		return nil, err
	}
	if err := p.policy.CheckCWD(dir); err != nil {
		return nil, err
	}

	ctx, cancel := context.WithTimeout(ctx, p.cfg.Timeout)
	defer cancel()

	shell, flag := shellCommand()
	c := exec.CommandContext(ctx, shell, flag, cmd)
	c.Dir = dir
	c.Env = buildEnv()
	setProcAttrs(c)

	var buf bytes.Buffer
	lim := &limitedWriter{w: &buf, remaining: p.cfg.MaxOutput}
	c.Stdout = lim
	c.Stderr = lim
	c.Stdin = nil

	start := time.Now()
	err := c.Run()
	dur := time.Since(start)

	res := &ExecResult{
		DurationMS: dur.Milliseconds(),
		Truncated:  lim.truncated,
		Output:     buf.Bytes(),
	}

	// A non-zero exit is a normal outcome, not an error: the caller
	// wants the code. Only a failure to start is surfaced as an error.
	if err != nil {
		var exitErr *exec.ExitError
		if !asExitError(err, &exitErr) {
			return nil, err
		}
		res.ExitCode = exitErr.ExitCode()
	}

	// A timeout kills the process group; report it distinctly so the
	// client can say "timed out" rather than showing a bare exit code.
	if ctx.Err() == context.DeadlineExceeded {
		res.ExitCode = -1
		res.Truncated = true
	}

	return res, nil
}

// asExitError is a small wrapper so exec.go stays free of the errors
// package import churn.
func asExitError(err error, target **exec.ExitError) bool {
	if e, ok := err.(*exec.ExitError); ok {
		*target = e
		return true
	}
	return false
}

// limitedWriter caps how much output is retained and records whether
// truncation occurred.
type limitedWriter struct {
	w         io.Writer
	remaining int64
	truncated bool
}

func (l *limitedWriter) Write(p []byte) (int, error) {
	if l.remaining <= 0 {
		l.truncated = true
		return len(p), nil // pretend success so the child keeps running
	}
	if int64(len(p)) > l.remaining {
		p = p[:l.remaining]
		l.truncated = true
	}
	n, err := l.w.Write(p)
	l.remaining -= int64(n)
	return n, err
}

// shellCommand returns the shell and its command flag for this platform.
func shellCommand() (string, string) {
	if isWindows() {
		if sh, ok := os.LookupEnv("COMSPEC"); ok && sh != "" {
			return sh, "/C"
		}
		return "cmd.exe", "/C"
	}
	if sh, ok := os.LookupEnv("SHELL"); ok && sh != "" {
		return sh, "-c"
	}
	return "/bin/sh", "-c"
}

// SanitizeCWD returns a cleaned working directory, defaulting to the
// user's home when the supplied path is empty or unusable.
func SanitizeCWD(dir string) string {
	dir = strings.TrimSpace(dir)
	if dir == "" {
		if home, err := os.UserHomeDir(); err == nil {
			return home
		}
		return string(os.PathSeparator)
	}
	return dir
}
