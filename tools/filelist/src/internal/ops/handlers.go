package ops

import (
	"bytes"
	"context"
	"encoding/base64"
	"errors"

	"github.com/coder/websocket"
)

// ── Frame handlers ──────────────────────────────────────────────

// resolveCWD turns a client-supplied working directory into a real
// disk path.
//
// The client sends a VIRTUAL path (/data/nginx). An empty or
// unresolvable value falls back to the first allowed root rather than
// the process's home directory: defaulting to the home directory would
// sit outside the sandbox and make every command fail with a confusing
// policy error.
func (p *Panel) resolveCWD(virtual string) string {
	if p.resolver != nil {
		if virtual != "" {
			if real, ok := p.resolver.Resolve(virtual); ok {
				return real
			}
		}
		// No usable virtual path: start at an allowed root.
		if roots := p.policy.AllowedRoots(); len(roots) > 0 {
			return roots[0]
		}
	}
	return SanitizeCWD(virtual)
}

// handleExecFrame runs a one-shot command, streaming its output as it
// arrives.
//
// The command is registered as a session and its id is sent to the
// client immediately, so the stop button has something to act on while
// the command is still running. Previously no id was sent for exec
// sessions at all, which is why the panel could become permanently
// busy: the client waited for an exit frame that a long-running
// command would never produce.
func (p *Panel) handleExecFrame(ctx context.Context, conn *websocket.Conn, f clientFrame, clientIP string) {
	cwd := p.resolveCWD(f.CWD)

	// Reject before creating a session so a denied command does not
	// consume a concurrency slot.
	if err := p.policy.CheckCommand(f.Cmd); err != nil {
		p.logger.Warn("ops: exec denied (client=%s cwd=%s cmd=%q): %v", clientIP, cwd, f.Cmd, err)
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: classify(err), Msg: err.Error()})
		return
	}

	sess, err := p.sessions.Create(KindExec, cwd)
	if err != nil {
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: classify(err), Msg: err.Error()})
		return
	}
	defer p.sessions.Remove(sess.ID)

	h, err := p.StartExec(ctx, f.Cmd, cwd)
	if err != nil {
		p.logger.Warn("ops: exec failed (client=%s cwd=%s cmd=%q): %v", clientIP, cwd, f.Cmd, err)
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: classify(err), Msg: err.Error()})
		return
	}
	defer h.Close()

	p.registerProc(sess.ID, h)
	defer p.unregisterProc(sess.ID)

	// Tell the client which session this is, so it can stop it.
	p.send(ctx, conn, serverFrame{
		Type: "ready",
		SID:  sess.ID,
		Kind: string(KindExec),
		CWD:  cwd,
	})

	output, truncated := p.streamExec(ctx, conn, sess, h)
	info := h.Wait()
	info.Truncated = truncated

	sess.SetExit(info)

	p.logger.Info("ops: exec (client=%s cwd=%s cmd=%q exit=%d dur=%dms truncated=%v timedOut=%v killed=%v)",
		clientIP, cwd, f.Cmd, info.Code, info.DurationMS, info.Truncated, info.TimedOut, info.Killed)

	code := info.Code
	p.send(ctx, conn, serverFrame{
		Type:       "exit",
		SID:        sess.ID,
		Code:       &code,
		DurationMS: info.DurationMS,
		Truncated:  info.Truncated,
		TimedOut:   info.TimedOut,
		Killed:     info.Killed,
	})
	_ = output
}

// streamExec pumps the command's output to the client as it arrives,
// mirroring it into the session's ring buffer so a reconnect can replay
// what was missed.
//
// It returns the collected output (bounded by MaxOutput) and whether
// truncation occurred. Reading continues past the cap — the bytes are
// discarded rather than the read being abandoned, because a child
// writing to a full pipe would block forever.
func (p *Panel) streamExec(ctx context.Context, conn *websocket.Conn, sess *Session, h *execHandle) ([]byte, bool) {
	var collected bytes.Buffer
	truncated := false
	chunk := make([]byte, 8192)

	stream := h.Stream()
	for {
		n, err := stream.Read(chunk)
		if n > 0 {
			data := chunk[:n]
			sess.Ring.Write(data)

			remaining := p.cfg.MaxOutput - int64(collected.Len())
			switch {
			case remaining <= 0:
				truncated = true
			case int64(n) > remaining:
				collected.Write(data[:remaining])
				truncated = true
			default:
				collected.Write(data)
			}

			if werr := p.sendRaw(ctx, conn, serverFrame{Type: "out", Data: string(data)}); werr != nil {
				// The client is gone. Stop pumping; the deferred Kill
				// in the caller tears the command down.
				return collected.Bytes(), truncated
			}
		}
		if err != nil {
			return collected.Bytes(), truncated
		}
	}
}

// handleAttach replays a session's scrollback after a reconnect.
func (p *Panel) handleAttach(ctx context.Context, conn *websocket.Conn, f clientFrame) {
	if f.SID == "" {
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: "bad_frame", Msg: "attach requires sid"})
		return
	}
	s, err := p.sessions.Get(f.SID)
	if err != nil {
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: "no_session", Msg: err.Error()})
		return
	}
	s.Touch()

	p.send(ctx, conn, serverFrame{
		Type: "ready",
		SID:  s.ID,
		Kind: string(s.Kind),
		CWD:  s.CWD,
	})

	if buf := s.Ring.Bytes(); len(buf) > 0 {
		p.send(ctx, conn, serverFrame{Type: "out", Data: string(buf)})
	}
	if e := s.Exit(); e != nil {
		code := e.Code
		p.send(ctx, conn, serverFrame{
			Type:       "exit",
			Code:       &code,
			DurationMS: e.DurationMS,
			Truncated:  e.Truncated,
		})
	}
}

// handleInput forwards keystrokes to a PTY session.
func (p *Panel) handleInput(ctx context.Context, conn *websocket.Conn, f clientFrame) {
	s, err := p.sessions.Get(f.SID)
	if err != nil {
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: "no_session", Msg: err.Error()})
		return
	}
	if s.Kind != KindPTY {
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: "wrong_kind", Msg: errSessionWrongKind.Error()})
		return
	}
	s.Touch()

	// Input arrives base64-encoded so arbitrary control bytes survive
	// the JSON round trip.
	data, err := base64.StdEncoding.DecodeString(f.Data)
	if err != nil {
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: "bad_frame", Msg: "input must be base64"})
		return
	}
	if err := p.ptyWrite(s, data); err != nil {
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: "write_failed", Msg: err.Error()})
	}
}

// handleResize applies a terminal size change.
func (p *Panel) handleResize(f clientFrame) {
	s, err := p.sessions.Get(f.SID)
	if err != nil || s.Kind != KindPTY {
		return
	}
	s.Touch()
	_ = p.ptyResize(s, f.Cols, f.Rows)
}

// handleSignal delivers a signal to a session's process group.
//
// Both session kinds are supported. For exec the signal goes to the
// shell's process group; the "INT" case is what the stop button sends.
// If the group no longer exists the command has already finished, so
// the client is told rather than left waiting.
func (p *Panel) handleSignal(ctx context.Context, conn *websocket.Conn, f clientFrame) {
	s, err := p.sessions.Get(f.SID)
	if err != nil {
		p.send(ctx, conn, serverFrame{Type: "err", SID: f.SID, ErrCode: "no_session", Msg: err.Error()})
		return
	}

	// A one-shot command has its own handle; signalling the session's
	// pid is not enough because exec sessions keep no pid on the
	// Session itself.
	if s.Kind == KindExec {
		if !p.stopProc(s.ID) {
			p.send(ctx, conn, serverFrame{
				Type: "err", SID: s.ID, ErrCode: "no_process",
				Msg: "command has already finished",
			})
			return
		}
		p.logger.Info("ops: exec stop requested (sid=%s signal=%s)", s.ID, f.Name)
		return
	}

	pid := p.sessionPID(s)
	if pid <= 0 {
		p.send(ctx, conn, serverFrame{Type: "err", SID: s.ID, ErrCode: "no_process", Msg: "session has no live process"})
		return
	}
	if err := sendSignal(pid, f.Name); err != nil {
		p.send(ctx, conn, serverFrame{Type: "err", SID: s.ID, ErrCode: "signal_failed", Msg: err.Error()})
	}
}

// classify maps an error to a stable client-facing code.
func classify(err error) string {
	var rejected *ErrPolicyRejected
	if errors.As(err, &rejected) {
		return "denied_by_policy"
	}
	switch {
	case errors.Is(err, ErrTooManySessions):
		return "concurrency_limit"
	case errors.Is(err, ErrSessionNotFound):
		return "no_session"
	case errors.Is(err, errTerminalDisabled):
		return "terminal_disabled"
	case errors.Is(err, errNoTerminalToken), errors.Is(err, errBadTerminalToken):
		return "terminal_token"
	case errors.Is(err, errPTYUnsupported):
		return "pty_unavailable"
	default:
		return "exec_failed"
	}
}
