package ops

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"sync"
	"time"
)

// ── Sessions and their scrollback buffer ────────────────────────

// SessionKind distinguishes the two execution backends.
type SessionKind string

const (
	KindExec SessionKind = "exec"
	KindPTY  SessionKind = "pty"
)

// ExitInfo records how a command finished.
type ExitInfo struct {
	Code       int   `json:"code"`
	DurationMS int64 `json:"durationMs"`
	Truncated  bool  `json:"truncated"`
	// TimedOut is set when the deadline stopped the command, so the
	// client can say "timed out" rather than showing a bare code.
	TimedOut bool `json:"timedOut,omitempty"`
	// Killed is set when the operator stopped the command.
	Killed bool `json:"killed,omitempty"`
}

// Session is one running (or recently finished) command.
type Session struct {
	ID        string
	Kind      SessionKind
	CWD       string
	CreatedAt time.Time
	LastSeen  time.Time

	// Ring holds recent output so a reconnecting client can replay
	// what it missed.
	Ring *RingBuffer

	// mu guards Exit and LastSeen.
	mu   sync.Mutex
	exit *ExitInfo
	done bool
}

// newID returns a short random identifier.
func newID() string {
	var b [8]byte
	if _, err := rand.Read(b[:]); err != nil {
		// rand.Read failing is not recoverable in any useful way here;
		// fall back to a timestamp so we still produce a unique-ish id.
		return hex.EncodeToString([]byte(time.Now().Format(time.RFC3339Nano)))
	}
	return hex.EncodeToString(b[:])
}

// SetExit records the exit status. Only the first call takes effect.
func (s *Session) SetExit(e ExitInfo) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.done {
		return
	}
	s.exit = &e
	s.done = true
}

// Exit returns the exit status, or nil while still running.
func (s *Session) Exit() *ExitInfo {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.exit
}

// Finished reports whether the command has exited.
func (s *Session) Finished() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.done
}

// Touch updates the last-seen time, used for idle reaping.
func (s *Session) Touch() {
	s.mu.Lock()
	s.LastSeen = time.Now()
	s.mu.Unlock()
}

// IdleFor reports how long the session has been untouched.
func (s *Session) IdleFor() time.Duration {
	s.mu.Lock()
	defer s.mu.Unlock()
	return time.Since(s.LastSeen)
}

// ── RingBuffer ──────────────────────────────────────────────────

// RingBuffer keeps the most recent bytes written to it, discarding the
// oldest once the limit is reached. It is used for scrollback replay.
type RingBuffer struct {
	mu   sync.Mutex
	buf  []byte
	max  int
	head int
	full bool
}

// NewRingBuffer creates a buffer holding at most max bytes.
func NewRingBuffer(max int) *RingBuffer {
	if max <= 0 {
		max = 256 << 10
	}
	return &RingBuffer{buf: make([]byte, max), max: max}
}

// Write appends p, overwriting the oldest data when full.
func (r *RingBuffer) Write(p []byte) {
	if len(p) == 0 {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()

	// A single write larger than the buffer keeps only its tail.
	if len(p) >= r.max {
		copy(r.buf, p[len(p)-r.max:])
		r.head = 0
		r.full = true
		return
	}

	n := copy(r.buf[r.head:], p)
	if n < len(p) {
		copy(r.buf, p[n:])
	}
	r.head = (r.head + len(p)) % r.max
	if r.head < len(p) || r.full {
		r.full = true
	}
}

// Bytes returns the buffered content in chronological order.
func (r *RingBuffer) Bytes() []byte {
	r.mu.Lock()
	defer r.mu.Unlock()

	if !r.full {
		out := make([]byte, r.head)
		copy(out, r.buf[:r.head])
		return out
	}
	out := make([]byte, 0, r.max)
	out = append(out, r.buf[r.head:]...)
	out = append(out, r.buf[:r.head]...)
	return out
}

// ── SessionRegistry ─────────────────────────────────────────────

var (
	// ErrTooManySessions is returned when the concurrency cap is hit.
	ErrTooManySessions = errors.New("too many concurrent sessions")
	// ErrSessionNotFound is returned for an unknown session id.
	ErrSessionNotFound = errors.New("session not found")
)

// SessionRegistry tracks live sessions and enforces the concurrency
// cap and idle timeout.
type SessionRegistry struct {
	mu       sync.Mutex
	sessions map[string]*Session

	cfg      Config
	logger   Logger
	policy   *Policy
	stopOnce sync.Once
	stopCh   chan struct{}
}

// NewSessionRegistry creates a registry and starts its reaper.
func NewSessionRegistry(cfg Config, logger Logger, policy *Policy) *SessionRegistry {
	r := &SessionRegistry{
		sessions: make(map[string]*Session),
		cfg:      cfg,
		logger:   logger,
		policy:   policy,
		stopCh:   make(chan struct{}),
	}
	go r.reapLoop()
	return r
}

// Create registers a new session, enforcing the concurrency cap.
func (r *SessionRegistry) Create(kind SessionKind, cwd string) (*Session, error) {
	r.mu.Lock()
	defer r.mu.Unlock()

	live := 0
	for _, s := range r.sessions {
		if !s.Finished() {
			live++
		}
	}
	if live >= r.cfg.MaxSessions {
		return nil, ErrTooManySessions
	}

	now := time.Now()
	s := &Session{
		ID:        newID(),
		Kind:      kind,
		CWD:       cwd,
		CreatedAt: now,
		LastSeen:  now,
		Ring:      NewRingBuffer(r.cfg.ScrollbackSize),
	}
	r.sessions[s.ID] = s
	return s, nil
}

// Get returns a session by id.
func (r *SessionRegistry) Get(id string) (*Session, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	s, ok := r.sessions[id]
	if !ok {
		return nil, ErrSessionNotFound
	}
	return s, nil
}

// Remove drops a session from the registry.
func (r *SessionRegistry) Remove(id string) {
	r.mu.Lock()
	delete(r.sessions, id)
	r.mu.Unlock()
}

// reapLoop removes sessions that have been idle past the timeout.
// The sweep interval is capped so that a short configured timeout is
// still honoured promptly; the 1s floor keeps an aggressive setting
// from spinning the CPU.
func (r *SessionRegistry) reapLoop() {
	interval := r.cfg.IdleTimeout / 4
	if interval > time.Minute {
		interval = time.Minute
	}
	if interval < 20*time.Millisecond {
		interval = 20 * time.Millisecond
	}
	t := time.NewTicker(interval)
	defer t.Stop()

	for {
		select {
		case <-r.stopCh:
			return
		case <-t.C:
			r.reapIdle()
		}
	}
}

func (r *SessionRegistry) reapIdle() {
	r.mu.Lock()
	var expired []*Session
	for id, s := range r.sessions {
		if s.IdleFor() > r.cfg.IdleTimeout {
			expired = append(expired, s)
			delete(r.sessions, id)
		}
	}
	r.mu.Unlock()

	for _, s := range expired {
		r.logger.Info("ops: reaping idle session %s (kind=%s)", s.ID, s.Kind)
	}
}

// CloseAll stops the reaper and drops every session. The host calls
// this during shutdown.
func (r *SessionRegistry) CloseAll() {
	r.stopOnce.Do(func() { close(r.stopCh) })

	r.mu.Lock()
	r.sessions = make(map[string]*Session)
	r.mu.Unlock()
}

// Count returns the number of tracked sessions, for tests.
func (r *SessionRegistry) Count() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.sessions)
}
