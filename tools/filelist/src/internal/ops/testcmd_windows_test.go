//go:build windows

package ops

import "os/exec"

// shellTestCommand builds a helper command for the tests themselves.
// These bypass the policy on purpose: they are test utilities, not
// commands submitted through the panel.
func shellTestCommand(cmd string) *exec.Cmd {
	return exec.Command("cmd.exe", "/C", cmd)
}
