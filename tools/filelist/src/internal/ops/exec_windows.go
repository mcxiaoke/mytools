//go:build windows

package ops

import (
	"errors"
	"os"
	"os/exec"
	"time"
)

// ── Windows process control ─────────────────────────────────────
//
// creack/pty targets Unix; its Windows build returns ErrUnsupported
// for every entry point. Rather than pull in a ConPTY dependency (and
// the hcsshim tree behind it) for a platform this project does not
// deploy to, the interactive terminal is reported as unavailable and
// the one-shot path — which works fine — is used instead.

func isWindows() bool { return true }

// ptyAvailable reports that interactive terminals are unavailable.
func ptyAvailable() bool { return false }

// setProcAttrs is a no-op on Windows. Process-group semantics differ
// (job objects would be the equivalent), and the kill path below
// degrades to terminating the shell directly.
func setProcAttrs(c *exec.Cmd) {}

// killProcessGroup terminates the process. Grandchildren are not
// reaped here; the one-shot path is the supported mode on Windows.
func killProcessGroup(pid int, grace time.Duration) {
	if pid <= 0 {
		return
	}
	proc, err := os.FindProcess(pid)
	if err != nil {
		return
	}
	_ = proc.Kill()
}

// sendSignal is unsupported on Windows; the client's stop button
// falls back to killing the session.
func sendSignal(pid int, name string) error {
	return errors.New("signals are not supported on this platform")
}

// defaultShell returns the Windows command interpreter.
func defaultShell() string {
	if sh := os.Getenv("COMSPEC"); sh != "" {
		return sh
	}
	return "cmd.exe"
}
