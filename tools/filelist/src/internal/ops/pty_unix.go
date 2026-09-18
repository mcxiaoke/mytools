//go:build !windows

package ops

import (
	"context"
	"errors"
	"io"
	"os"
	"os/exec"
	"sync"
	"time"

	"github.com/coder/websocket"
	"github.com/creack/pty"
)

// ── Interactive PTY sessions (Unix) ─────────────────────────────
//
// A PTY session is a long-lived shell. Output is streamed to the
// client as it arrives and mirrored into the session's ring buffer so
// a reconnect can replay what was missed.

// ptySession holds the live state of an interactive shell.
type ptySession struct {
	cmd  *exec.Cmd
	ptmx *os.File
	once sync.Once
}

// handleOpenPTY starts an interactive shell for this connection.
func (p *Panel) handleOpenPTY(ctx context.Context, conn *websocket.Conn, f clientFrame, clientIP string, active map[string]context.CancelFunc) {
	if !p.cfg.PTYAvailable {
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: "pty_unavailable", Msg: errPTYUnsupported.Error()})
		return
	}
	if err := p.checkTerminalToken(f.Token); err != nil {
		p.logger.Warn("ops: terminal open denied (client=%s): %v", clientIP, err)
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: classify(err), Msg: err.Error()})
		return
	}

	cwd := p.resolveCWD(f.CWD)
	if err := p.policy.CheckCWD(cwd); err != nil {
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: classify(err), Msg: err.Error()})
		return
	}

	sess, err := p.sessions.Create(KindPTY, cwd)
	if err != nil {
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: classify(err), Msg: err.Error()})
		return
	}

	ps, err := startPTY(cwd, f.Cols, f.Rows)
	if err != nil {
		p.sessions.Remove(sess.ID)
		p.send(ctx, conn, serverFrame{Type: "err", ErrCode: "pty_failed", Msg: err.Error()})
		return
	}
	p.registerPTY(sess.ID, ps)

	p.logger.Info("ops: terminal opened (client=%s cwd=%s sid=%s)", clientIP, cwd, sess.ID)

	sessCtx, cancel := context.WithCancel(ctx)
	active[sess.ID] = cancel

	p.send(ctx, conn, serverFrame{
		Type: "ready",
		SID:  sess.ID,
		Kind: string(KindPTY),
		CWD:  cwd,
	})

	// Stream output until the shell exits or the connection drops.
	go func() {
		defer func() {
			ps.close()
			p.unregisterPTY(sess.ID)
			p.sessions.Remove(sess.ID)
			cancel()
		}()

		buf := make([]byte, 8192)
		for {
			n, err := ps.ptmx.Read(buf)
			if n > 0 {
				chunk := buf[:n]
				sess.Ring.Write(chunk)
				if werr := p.sendRaw(sessCtx, conn, serverFrame{Type: "out", Data: string(chunk)}); werr != nil {
					return
				}
			}
			if err != nil {
				if !errors.Is(err, io.EOF) {
					// A closed slave side surfaces as EIO on Linux and
					// simply means the shell exited.
					_ = err
				}
				break
			}
			select {
			case <-sessCtx.Done():
				return
			default:
			}
		}

		code := 0
		if ps.cmd.ProcessState != nil {
			code = ps.cmd.ProcessState.ExitCode()
		}
		sess.SetExit(ExitInfo{Code: code})
		c := code
		p.sendRaw(sessCtx, conn, serverFrame{Type: "exit", Code: &c})
	}()
}

// sendRaw writes a frame without a fresh timeout, used from the PTY
// reader goroutine where the session context already bounds it.
// ptySessions maps session id to live PTY state.
var (
	ptyMu       sync.Mutex
	ptySessions = map[string]*ptySession{}
)

func (p *Panel) registerPTY(id string, ps *ptySession) {
	ptyMu.Lock()
	ptySessions[id] = ps
	ptyMu.Unlock()
}

func (p *Panel) unregisterPTY(id string) {
	ptyMu.Lock()
	delete(ptySessions, id)
	ptyMu.Unlock()
}

func (p *Panel) lookupPTY(id string) *ptySession {
	ptyMu.Lock()
	defer ptyMu.Unlock()
	return ptySessions[id]
}

// startPTY launches an interactive shell attached to a new PTY.
func startPTY(dir string, cols, rows int) (*ptySession, error) {
	shell := defaultShell()
	cmd := exec.Command(shell, "-i")
	cmd.Dir = dir
	cmd.Env = append(buildEnv(), "PS1=\\w $ ")

	ws := &pty.Winsize{}
	if cols > 0 && rows > 0 {
		ws.Cols = uint16(cols)
		ws.Rows = uint16(rows)
	} else {
		ws.Cols, ws.Rows = 80, 24
	}

	ptmx, err := pty.StartWithSize(cmd, ws)
	if err != nil {
		return nil, err
	}
	return &ptySession{cmd: cmd, ptmx: ptmx}, nil
}

// ptyWrite sends keystrokes to the shell.
func (p *Panel) ptyWrite(s *Session, data []byte) error {
	ps := p.lookupPTY(s.ID)
	if ps == nil {
		return errors.New("pty session is gone")
	}
	_, err := ps.ptmx.Write(data)
	return err
}

// ptyResize updates the terminal dimensions.
func (p *Panel) ptyResize(s *Session, cols, rows int) error {
	ps := p.lookupPTY(s.ID)
	if ps == nil {
		return errors.New("pty session is gone")
	}
	if cols <= 0 || rows <= 0 {
		return nil
	}
	return pty.Setsize(ps.ptmx, &pty.Winsize{Cols: uint16(cols), Rows: uint16(rows)})
}

// sessionPID returns the process group leader pid for signalling.
func (p *Panel) sessionPID(s *Session) int {
	ps := p.lookupPTY(s.ID)
	if ps == nil || ps.cmd.Process == nil {
		return 0
	}
	return ps.cmd.Process.Pid
}

// close terminates the shell and releases the PTY.
func (ps *ptySession) close() {
	ps.once.Do(func() {
		if ps.cmd.Process != nil {
			killProcessGroup(ps.cmd.Process.Pid, 2*time.Second)
		}
		_ = ps.ptmx.Close()
		_ = ps.cmd.Wait()
	})
}
