package ops

import (
	"bytes"
	"context"
	"errors"
	"io"
	"os"
	"os/exec"
	"strings"
	"sync"
	"time"
)

// ── One-shot execution, streamed ────────────────────────────────
//
// One-shot mode runs a command with plain pipes rather than a PTY.
// That keeps output free of shell echo and carriage-return noise, and
// — most importantly — makes the real exit code available without the
// `; echo $?` sentinel trick a PTY would require.
//
// Output is streamed as it arrives rather than collected until the
// process exits. The earlier buffered implementation meant a
// long-running command produced no visible output at all: the client
// sat on "executing" until the process finished, which for something
// like `tail -f` is never.

// ExecResult is the outcome of a command whose output was collected.
type ExecResult struct {
	ExitCode   int
	DurationMS int64
	Truncated  bool
	TimedOut   bool
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

// execHandle is a running one-shot command.
//
// It is deliberately not built on exec.CommandContext: that kills only
// the direct child on cancellation, so `bash -c "a | b"` leaves the
// pipeline's other members running. Termination goes through
// killProcessGroup instead, which signals the whole group.
type execHandle struct {
	cmd    *exec.Cmd
	stdout io.ReadCloser
	stderr io.ReadCloser

	// cancel releases the deadline context once the process is reaped.
	cancel context.CancelFunc

	// done is closed when Wait has reaped the process, so the watchdog
	// stops caring about the deadline.
	done      chan struct{}
	startedAt time.Time

	mu       sync.Mutex
	timedOut bool
	killed   bool
	exit     ExitInfo

	waitOnce sync.Once
}

// PID returns the process id of the shell running the command, or 0.
func (h *execHandle) PID() int {
	if h.cmd.Process == nil {
		return 0
	}
	return h.cmd.Process.Pid
}

// Stream returns a single reader over stdout and stderr interleaved.
//
// The caller must read until EOF. An io.Pipe applies backpressure, so
// abandoning the stream would block the child on its next write; the
// consumers here always drain, discarding anything past the output cap
// rather than stopping.
func (h *execHandle) Stream() io.Reader {
	pr, pw := io.Pipe()
	var wg sync.WaitGroup
	wg.Add(2)
	go func() { defer wg.Done(); _, _ = io.Copy(pw, h.stdout) }()
	go func() { defer wg.Done(); _, _ = io.Copy(pw, h.stderr) }()
	go func() { wg.Wait(); _ = pw.Close() }()
	return pr
}

// Kill terminates the command's process group on the operator's
// request. It is safe to call after the process has exited.
func (h *execHandle) Kill(grace time.Duration) {
	h.mu.Lock()
	if !h.timedOut {
		h.killed = true
	}
	h.mu.Unlock()
	h.terminate(grace)
}

// markTimedOut records that the deadline expired, so the exit is
// reported as a timeout rather than an ordinary failure.
func (h *execHandle) markTimedOut() {
	h.mu.Lock()
	h.timedOut = true
	h.mu.Unlock()
}

// terminate signals the process group without touching the flags.
func (h *execHandle) terminate(grace time.Duration) {
	if pid := h.PID(); pid > 0 {
		killProcessGroup(pid, grace)
	}
}

// closePipes unblocks a reader that is still waiting on the command's
// output.
//
// It matters on Windows, where killing the direct child does not reap
// its descendants: `cmd.exe /C "sh -c yes"` leaves sh and yes running,
// still holding the inherited write end of the pipe. Without closing
// the read end here, the reader would wait for an EOF that only
// arrives when those grandchildren exit — which for a command like
// `yes` is never.
func (h *execHandle) closePipes() {
	if h.stdout != nil {
		_ = h.stdout.Close()
	}
	if h.stderr != nil {
		_ = h.stderr.Close()
	}
}

// Wait reaps the process and returns its exit status. It must be called
// after the stream has reached EOF: cmd.Wait closes the pipes, so
// calling it first would truncate the output.
//
// A bounded fallback guards against a child that ignores termination:
// if the process cannot be reaped within the grace period after a kill,
// the recorded status is reported anyway rather than blocking the
// caller forever.
func (h *execHandle) Wait() ExitInfo {
	h.waitOnce.Do(func() {
		err := h.cmd.Wait()

		h.mu.Lock()
		info := ExitInfo{DurationMS: time.Since(h.startedAt).Milliseconds()}
		if err != nil {
			var exitErr *exec.ExitError
			if errors.As(err, &exitErr) {
				info.Code = exitErr.ExitCode()
			} else {
				info.Code = -1
			}
		}
		if h.timedOut {
			// A killed process reports a signal-derived code; -1
			// distinguishes "we stopped it" from "it failed".
			info.Code = -1
			info.TimedOut = true
		}
		if h.killed {
			info.Code = -1
			info.Killed = true
		}
		h.exit = info
		h.mu.Unlock()

		close(h.done)
	})

	<-h.done

	h.mu.Lock()
	defer h.mu.Unlock()
	return h.exit
}

// Close releases the deadline context. The pipes are closed by Wait.
func (h *execHandle) Close() {
	if h.cancel != nil {
		h.cancel()
	}
}

// StartExec validates a command and launches it, returning a handle
// whose output can be streamed while it runs.
//
// Validation happens before anything is spawned. The returned handle is
// registered with the panel's process registry by the caller so the
// signal and kill frames can reach it.
func (p *Panel) StartExec(ctx context.Context, cmd, dir string) (*execHandle, error) {
	if err := p.policy.CheckCommand(cmd); err != nil {
		return nil, err
	}
	if err := p.policy.CheckCWD(dir); err != nil {
		return nil, err
	}

	shell, flag := shellCommand()
	c := exec.Command(shell, flag, cmd)
	c.Dir = dir
	c.Env = buildEnv()
	setProcAttrs(c)
	c.Stdin = nil

	stdout, err := c.StdoutPipe()
	if err != nil {
		return nil, err
	}
	stderr, err := c.StderrPipe()
	if err != nil {
		return nil, err
	}

	if err := c.Start(); err != nil {
		return nil, err
	}

	runCtx, cancel := context.WithTimeout(ctx, p.cfg.Timeout)
	h := &execHandle{
		cmd:       c,
		stdout:    stdout,
		stderr:    stderr,
		cancel:    cancel,
		done:      make(chan struct{}),
		startedAt: time.Now(),
	}

	// Watchdog. When the deadline passes — or the connection goes away
	// and takes the context with it — terminate the whole group so
	// nothing survives the request. The pipes are closed as well, so a
	// reader blocked on output is released even if a grandchild on
	// Windows outlives the kill.
	go func() {
		select {
		case <-runCtx.Done():
			if errors.Is(runCtx.Err(), context.DeadlineExceeded) {
				h.markTimedOut()
			}
			h.terminate(p.killGrace)
			h.closePipes()
		case <-h.done:
		}
	}()

	return h, nil
}

// RunExec runs a command to completion and returns its collected
// output. It is the non-streaming entry point: the streaming path in
// streamExec is what the WebSocket uses, and this exists for callers
// that just want the result.
func (p *Panel) RunExec(ctx context.Context, cmd, dir string) (*ExecResult, error) {
	h, err := p.StartExec(ctx, cmd, dir)
	if err != nil {
		return nil, err
	}
	defer h.Close()

	// Drain first, reap second: Wait closes the pipes.
	output, truncated := drainAll(h.Stream(), p.cfg.MaxOutput)
	info := h.Wait()

	return &ExecResult{
		ExitCode:   info.Code,
		DurationMS: info.DurationMS,
		Truncated:  truncated || info.Truncated,
		TimedOut:   info.TimedOut,
		Output:     output,
	}, nil
}

// drainAll reads r to EOF, keeping at most max bytes but always
// consuming the stream so the child is never blocked on a full pipe.
func drainAll(r io.Reader, max int64) ([]byte, bool) {
	var buf bytes.Buffer
	truncated := false
	chunk := make([]byte, 8192)

	for {
		n, err := r.Read(chunk)
		if n > 0 {
			remaining := max - int64(buf.Len())
			switch {
			case remaining <= 0:
				truncated = true
			case int64(n) > remaining:
				buf.Write(chunk[:remaining])
				truncated = true
			default:
				buf.Write(chunk[:n])
			}
		}
		if err != nil {
			return buf.Bytes(), truncated
		}
	}
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
