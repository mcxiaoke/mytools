//go:build !windows

package ops

import (
	"errors"
	"os"
	"os/exec"
	"syscall"
	"time"
)

// ── Unix process control ────────────────────────────────────────

// isWindows reports the platform. Kept as a function so the shared
// files do not need build tags just to branch on it.
func isWindows() bool { return false }

// ptyAvailable reports whether the interactive terminal can work here.
func ptyAvailable() bool { return true }

// setProcAttrs puts the child in its own process group. Without this,
// killing the shell leaves grandchildren from a pipeline (`a | b`)
// orphaned and running.
func setProcAttrs(c *exec.Cmd) {
	if c.SysProcAttr == nil {
		c.SysProcAttr = &syscall.SysProcAttr{}
	}
	c.SysProcAttr.Setpgid = true
}

// killProcessGroup signals the whole group led by pid. A negative pid
// addresses the group, which is what stops pipelines cleanly.
//
// SIGTERM goes first so well-behaved programs can clean up; if the
// group is still alive after grace, SIGKILL follows.
func killProcessGroup(pid int, grace time.Duration) {
	if pid <= 0 {
		return
	}
	_ = syscall.Kill(-pid, syscall.SIGTERM)

	deadline := time.Now().Add(grace)
	for time.Now().Before(deadline) {
		// Signal 0 probes for existence without delivering anything.
		if err := syscall.Kill(-pid, 0); err != nil {
			return // group is gone
		}
		time.Sleep(50 * time.Millisecond)
	}
	_ = syscall.Kill(-pid, syscall.SIGKILL)
}

// sendSignal delivers a named signal to a process group.
func sendSignal(pid int, name string) error {
	if pid <= 0 {
		return errors.New("invalid pid")
	}
	sig, ok := signalByName(name)
	if !ok {
		return errors.New("unsupported signal: " + name)
	}
	return syscall.Kill(-pid, sig)
}

func signalByName(name string) (syscall.Signal, bool) {
	switch name {
	case "INT":
		return syscall.SIGINT, true
	case "TERM":
		return syscall.SIGTERM, true
	case "KILL":
		return syscall.SIGKILL, true
	case "HUP":
		return syscall.SIGHUP, true
	case "QUIT":
		return syscall.SIGQUIT, true
	default:
		return 0, false
	}
}

// defaultShell returns the shell used for interactive sessions.
func defaultShell() string {
	if sh := os.Getenv("SHELL"); sh != "" {
		return sh
	}
	return "/bin/bash"
}
