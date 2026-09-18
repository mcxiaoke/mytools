package ops

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// ── Execution tests ─────────────────────────────────────────────

func TestRunExec_Success(t *testing.T) {
	p := newTestPanel(t, Config{Allow: []string{`^echo .*$`}})
	dir := t.TempDir()

	res, err := p.RunExec(context.Background(), "echo hello", dir)
	if err != nil {
		t.Fatalf("RunExec: %v", err)
	}
	if res.ExitCode != 0 {
		t.Errorf("ExitCode = %d, want 0", res.ExitCode)
	}
	if !strings.Contains(string(res.Output), "hello") {
		t.Errorf("Output = %q, want it to contain hello", res.Output)
	}
}

// TestRunExec_ExitCodeIsReal is why one-shot mode uses pipes instead of
// a PTY: the real exit status is available without a sentinel.
func TestRunExec_ExitCodeIsReal(t *testing.T) {
	if isWindows() {
		t.Skip("uses a POSIX shell to produce a specific exit code")
	}
	p := newTestPanel(t, Config{Allow: []string{`^sh -c exit \d+$`}})
	dir := t.TempDir()

	res, err := p.RunExec(context.Background(), "sh -c exit 3", dir)
	if err != nil {
		t.Fatalf("RunExec: %v", err)
	}
	if res.ExitCode != 3 {
		t.Errorf("ExitCode = %d, want 3", res.ExitCode)
	}
}

func TestRunExec_DeniedByPolicy(t *testing.T) {
	p := newTestPanel(t, Config{})
	dir := t.TempDir()

	_, err := p.RunExec(context.Background(), "rm -rf /", dir)
	if err == nil {
		t.Fatal("expected denial for rm -rf /")
	}
	if classify(err) != "denied_by_policy" {
		t.Errorf("classify = %q, want denied_by_policy", classify(err))
	}
}

// TestRunExec_DoesNotLeakEnv is a credential-leak guard. The server's
// environment holds access tokens; a child inheriting it would let a
// single `env` print them.
func TestRunExec_DoesNotLeakEnv(t *testing.T) {
	const secret = "FILELIST_TEST_SECRET_VALUE"
	t.Setenv("FILELIST_TEST_SECRET", secret)

	p := newTestPanel(t, Config{Allow: []string{`^env$`}})
	dir := t.TempDir()

	res, err := p.RunExec(context.Background(), "env", dir)
	if err != nil {
		t.Fatalf("RunExec: %v", err)
	}
	if strings.Contains(string(res.Output), secret) {
		t.Errorf("child inherited a non-allowlisted env var:\n%s", res.Output)
	}
	if strings.Contains(string(res.Output), "FILELIST_TEST_SECRET") {
		t.Errorf("child env contains the secret's name:\n%s", res.Output)
	}
}

func TestRunExec_OutputTruncated(t *testing.T) {
	p := newTestPanel(t, Config{
		MaxOutput: 512,
		Allow:     []string{`^sh -c yes$`},
	})
	dir := t.TempDir()

	res, err := p.RunExec(context.Background(), "sh -c yes", dir)
	if err != nil {
		t.Fatalf("RunExec: %v", err)
	}
	if !res.Truncated {
		t.Error("Truncated = false, want true for unbounded output")
	}
	if int64(len(res.Output)) > 512 {
		t.Errorf("Output length = %d, want <= 512", len(res.Output))
	}
}

func TestRunExec_TimeoutKillsProcessGroup(t *testing.T) {
	if isWindows() {
		t.Skip("process-group semantics are Unix-only")
	}

	p := newTestPanel(t, Config{
		Timeout: 1 * time.Second,
		Allow:   []string{`^sleep 60$`},
	})
	dir := t.TempDir()

	start := time.Now()
	res, err := p.RunExec(context.Background(), "sleep 60", dir)
	elapsed := time.Since(start)

	if err != nil {
		t.Fatalf("RunExec: %v", err)
	}
	if elapsed > 5*time.Second {
		t.Errorf("command took %v, want it killed near the 1s timeout", elapsed)
	}
	if res.ExitCode != -1 {
		t.Errorf("ExitCode = %d, want -1 to signal a timeout", res.ExitCode)
	}
}

// TestRunExec_NoOrphanedChildren is the process-group test. Without
// Setpgid and a group-directed kill, the `sleep` spawned by the shell
// survives and leaks.
func TestRunExec_NoOrphanedChildren(t *testing.T) {
	if isWindows() {
		t.Skip("process-group semantics are Unix-only")
	}

	marker := "ops-orphan-check-" + newID()
	p := newTestPanel(t, Config{
		Timeout: 1 * time.Second,
		Allow:   []string{`^sh -c .*$`},
	})
	dir := t.TempDir()

	// The shell backgrounds a long sleep whose command line carries a
	// unique marker, then waits. Killing only the shell would leave
	// the sleep running.
	cmd := "sh -c 'sleep 300 # " + marker + " & wait'"
	_, err := p.RunExec(context.Background(), cmd, dir)
	if err != nil {
		t.Fatalf("RunExec: %v", err)
	}

	// Give the group a moment to be reaped.
	time.Sleep(500 * time.Millisecond)

	if pid := findProcessByMarker(marker); pid != "" {
		t.Errorf("orphaned child survived the timeout (pid %s)", pid)
		_ = killPID(pid)
	}
}

func TestRunExec_CWDOutsideSandboxRejected(t *testing.T) {
	root := t.TempDir()
	p, err := New(Config{
		Mode:        "allowlist",
		AccessToken: "t",
		CwdRoots:    []string{root},
		Timeout:     5 * time.Second,
		MaxOutput:   4096,
		MaxSessions: 2,
		IdleTimeout: time.Minute,
	}, nopLogger{}, fakeResolver{root: root})
	if err != nil {
		t.Fatal(err)
	}
	defer p.Close()

	if _, err := p.RunExec(context.Background(), "ls", "/etc"); err == nil {
		t.Error("expected rejection for a cwd outside the sandbox")
	}
}

// ── Session registry tests ──────────────────────────────────────

func TestSessionRegistry_ConcurrencyCap(t *testing.T) {
	reg := NewSessionRegistry(Config{MaxSessions: 2, IdleTimeout: time.Minute, ScrollbackSize: 1024}, nopLogger{}, nil)
	defer reg.CloseAll()

	s1, err := reg.Create(KindExec, "/tmp")
	if err != nil {
		t.Fatalf("first Create: %v", err)
	}
	if _, err := reg.Create(KindExec, "/tmp"); err != nil {
		t.Fatalf("second Create: %v", err)
	}
	if _, err := reg.Create(KindExec, "/tmp"); err != ErrTooManySessions {
		t.Errorf("third Create error = %v, want ErrTooManySessions", err)
	}

	// Finishing a session frees a slot.
	s1.SetExit(ExitInfo{Code: 0})
	if _, err := reg.Create(KindExec, "/tmp"); err != nil {
		t.Errorf("Create after completion: %v", err)
	}
}

func TestSessionRegistry_GetAndRemove(t *testing.T) {
	reg := NewSessionRegistry(Config{MaxSessions: 2, IdleTimeout: time.Minute, ScrollbackSize: 1024}, nopLogger{}, nil)
	defer reg.CloseAll()

	s, err := reg.Create(KindExec, "/tmp")
	if err != nil {
		t.Fatal(err)
	}
	got, err := reg.Get(s.ID)
	if err != nil || got.ID != s.ID {
		t.Fatalf("Get returned %v, %v", got, err)
	}

	reg.Remove(s.ID)
	if _, err := reg.Get(s.ID); err != ErrSessionNotFound {
		t.Errorf("Get after Remove = %v, want ErrSessionNotFound", err)
	}
}

func TestSession_ExitIsRecordedOnce(t *testing.T) {
	s := &Session{ID: "x", Ring: NewRingBuffer(64), LastSeen: time.Now()}
	if s.Finished() {
		t.Error("a fresh session should not be finished")
	}

	s.SetExit(ExitInfo{Code: 0})
	s.SetExit(ExitInfo{Code: 99}) // must not overwrite

	if e := s.Exit(); e == nil || e.Code != 0 {
		t.Errorf("Exit = %+v, want the first recorded code 0", e)
	}
	if !s.Finished() {
		t.Error("Finished = false after SetExit")
	}
}

func TestSessionRegistry_IdleReaping(t *testing.T) {
	reg := NewSessionRegistry(Config{
		MaxSessions:    4,
		IdleTimeout:    50 * time.Millisecond,
		ScrollbackSize: 1024,
	}, nopLogger{}, nil)
	defer reg.CloseAll()

	if _, err := reg.Create(KindExec, "/tmp"); err != nil {
		t.Fatal(err)
	}
	if reg.Count() != 1 {
		t.Fatalf("Count = %d, want 1", reg.Count())
	}

	time.Sleep(150 * time.Millisecond)
	if n := reg.Count(); n != 0 {
		t.Errorf("Count after idle timeout = %d, want 0", n)
	}
}

// ── helpers ─────────────────────────────────────────────────────

// findProcessByMarker looks for a process whose command line contains
// the marker. Used to prove no orphan survived a kill.
func findProcessByMarker(marker string) string {
	out, err := runShell("ps -eo pid=,args=")
	if err != nil {
		return ""
	}
	for _, line := range strings.Split(out, "\n") {
		if strings.Contains(line, marker) && !strings.Contains(line, "ps -eo") {
			fields := strings.Fields(line)
			if len(fields) > 0 {
				return fields[0]
			}
		}
	}
	return ""
}

func killPID(pid string) error {
	_, err := runShell("kill -9 " + pid)
	return err
}

// runShell runs a helper command for the tests themselves, bypassing
// the policy (these are test utilities, not panel commands).
func runShell(cmd string) (string, error) {
	c := shellTestCommand(cmd)
	out, err := c.Output()
	return string(out), err
}

// ── SanitizeCWD ─────────────────────────────────────────────────

func TestSanitizeCWD(t *testing.T) {
	if got := SanitizeCWD("/data"); got != "/data" {
		t.Errorf("SanitizeCWD(/data) = %q", got)
	}
	got := SanitizeCWD("")
	if got == "" {
		t.Error("SanitizeCWD(\"\") returned empty; want a usable default")
	}
	if !filepath.IsAbs(got) {
		t.Errorf("SanitizeCWD default = %q, want an absolute path", got)
	}
	_ = os.Getenv // keep the os import meaningful across platforms
}
