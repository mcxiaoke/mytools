package ops

import "errors"

// WebSocket handshake failures. Each is distinct so the tests can
// assert precisely which check refused the upgrade.
var (
	errNoOrigin          = errors.New("missing Origin header")
	errBadOrigin         = errors.New("malformed Origin header")
	errOriginMismatch    = errors.New("Origin does not match the request host")
	errNoToken           = errors.New("no token supplied")
	errBadToken          = errors.New("token mismatch")
	errNoTokenConfigured = errors.New("no access token configured; refusing open access")
	errTerminalDisabled  = errors.New("interactive terminal is disabled")
	errNoTerminalToken   = errors.New("terminal token required")
	errBadTerminalToken  = errors.New("terminal token mismatch")
	errSessionWrongKind  = errors.New("session kind mismatch")
	errSessionNotRunning = errors.New("session is not running")

	// errPTYUnsupported is reported when interactive terminals are not
	// available on this platform (Windows) or have been compiled out.
	errPTYUnsupported = errors.New("interactive terminal is not supported on this platform")
)
