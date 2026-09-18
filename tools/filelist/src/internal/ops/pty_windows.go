//go:build windows

package ops

import (
	"context"

	"github.com/coder/websocket"
)

// ── Interactive terminal stub (Windows) ─────────────────────────
//
// creack/pty is a Unix-only library; on Windows every function returns
// ErrUnsupported. Rather than pull in a ConPTY dependency (and the
// hcsshim tree behind it) for a platform this project does not deploy
// to, the panel reports PTYAvailable=false and the client hides the
// terminal entry point. One-shot commands work normally here.
//
// These stubs keep the package compiling on Windows so `go build` and
// `go test ./...` both succeed on the development machine.
// errPTYUnsupported itself lives in errors.go so both platforms see it.

// handleOpenPTY reports that terminals are unavailable.
func (p *Panel) handleOpenPTY(_ context.Context, conn *websocket.Conn, _ clientFrame, _ string, _ map[string]context.CancelFunc) {
	p.send(context.Background(), conn, serverFrame{
		Type:    "err",
		ErrCode: "pty_unavailable",
		Msg:     errPTYUnsupported.Error(),
	})
}

// ptyWrite is unreachable on Windows.
func (p *Panel) ptyWrite(*Session, []byte) error { return errPTYUnsupported }

// ptyResize is unreachable on Windows.
func (p *Panel) ptyResize(*Session, int, int) error { return errPTYUnsupported }

// sessionPID reports no process, so signal handling is a no-op.
func (p *Panel) sessionPID(*Session) int { return 0 }
