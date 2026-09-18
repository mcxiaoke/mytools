package ops

import (
	"context"
	"encoding/base64"
	"errors"
	"time"

	"github.com/coder/websocket"
)

// ── Frame handlers ──────────────────────────────────────────────

// handleExecFrame runs a one-shot command and streams the result back.
func (p *Panel) handleExecFrame(ctx context.Context, conn *websocket.Conn, f clientFrame, clientIP string) {
	cwd := SanitizeCWD(f.CWD)
	if p.resolver != nil && f.CWD != "" {
		if real, ok := p.resolver.Resolve(f.CWD); ok {
			cwd = real
		}
	}

	// Audit every attempt, including rejected ones — the denial is
	// exactly what an operator would want to see afterwards.
	start := time.Now()
	res, err := p.RunExec(ctx, f.Cmd, cwd)
	if err != nil {
		code := classify(err)
		p.logger.Warn("ops: exec denied (client=%s cwd=%s cmd=%q): %v", clientIP, cwd, f.Cmd, err)
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: code, Msg: err.Error()})
		return
	}

	p.logger.Info("ops: exec (client=%s cwd=%s cmd=%q exit=%d dur=%dms truncated=%v)",
		clientIP, cwd, f.Cmd, res.ExitCode, res.DurationMS, res.Truncated)

	p.send(ctx, conn, serverFrame{Type: "out", Data: string(res.Output)})
	code := res.ExitCode
	p.send(ctx, conn, serverFrame{
		Type:       "exit",
		Code:       &code,
		DurationMS: res.DurationMS,
		Truncated:  res.Truncated,
	})
	_ = start
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

// handleSignal delivers a signal to the session's process group.
func (p *Panel) handleSignal(ctx context.Context, conn *websocket.Conn, f clientFrame) {
	s, err := p.sessions.Get(f.SID)
	if err != nil {
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: "no_session", Msg: err.Error()})
		return
	}
	pid := p.sessionPID(s)
	if pid <= 0 {
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: "no_process", Msg: "session has no live process"})
		return
	}
	if err := sendSignal(pid, f.Name); err != nil {
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: "signal_failed", Msg: err.Error()})
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
