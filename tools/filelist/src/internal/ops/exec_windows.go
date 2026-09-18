//go:build windows

package ops

import (
	"errors"
	"os"
	"os/exec"
	"strconv"
	"syscall"
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

// killProcessGroup terminates the process and its descendants.
//
// os.Process.Kill only reaches the direct child. The command runs as
// `cmd.exe /C ...`, so the program the operator actually started is a
// grandchild and would survive — `tail -f` in particular would keep
// running after the panel reported it stopped. taskkill with /T walks
// the tree, which is the closest Windows equivalent to signalling a
// Unix process group.
func killProcessGroup(pid int, grace time.Duration) {
	if pid <= 0 {
		return
	}

	// /T includes the child tree, /F skips the polite request. There is
	// no SIGTERM equivalent to try first: Windows has no signal that a
	// console application can catch for a graceful shutdown.
	cmd := exec.Command("taskkill", "/F", "/T", "/PID", strconv.Itoa(pid))
	// Suppress the console window and any output; a failure here is
	// not actionable (the process may already be gone).
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	_ = cmd.Run()

	// Fall back to a direct kill in case taskkill is unavailable.
	if proc, err := os.FindProcess(pid); err == nil {
		_ = proc.Kill()
	}
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
