package main

import (
	"fmt"
	"io"
	"log"
	"os"
	"path/filepath"
	"strings"
)

// LogLevel controls the verbosity of log output.
type LogLevel int

const (
	LevelDebug LogLevel = iota
	LevelInfo
	LevelWarn
	LevelError
)

// parseLevel converts a string to LogLevel. Unknown values default to info.
func parseLevel(s string) LogLevel {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "debug":
		return LevelDebug
	case "warn", "warning":
		return LevelWarn
	case "error":
		return LevelError
	default:
		return LevelInfo
	}
}

// Logger wraps the standard logger with level filtering and optional file output.
type Logger struct {
	level  LogLevel
	logger *log.Logger
}

// NewLogger creates a Logger that writes to w at the given level.
func NewLogger(level LogLevel, w io.Writer) *Logger {
	return &Logger{
		level:  level,
		logger: log.New(w, "", log.LstdFlags|log.Lmicroseconds),
	}
}

// initLogger configures the global logger from config.
// If log file is empty, output goes to stdout.
func initLogger(cfg *Config) (*Logger, func(), error) {
	level := parseLevel(cfg.Log.Level)

	var w io.Writer = os.Stdout
	var closers []io.Closer

	if cfg.Log.File != "" {
		// ensure parent dir exists
		if err := os.MkdirAll(filepath.Dir(cfg.Log.File), 0755); err != nil {
			return nil, nil, fmt.Errorf("create log dir: %w", err)
		}
		f, err := os.OpenFile(cfg.Log.File, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
		if err != nil {
			return nil, nil, fmt.Errorf("open log file %s: %w", cfg.Log.File, err)
		}
		closers = append(closers, f)
		w = f
	}

	l := NewLogger(level, w)

	cleanup := func() {
		for _, c := range closers {
			c.Close()
		}
	}

	return l, cleanup, nil
}

func (l *Logger) Debug(format string, args ...any) {
	if l.level <= LevelDebug {
		l.logger.Printf("[DEBUG] "+format, args...)
	}
}

func (l *Logger) Info(format string, args ...any) {
	if l.level <= LevelInfo {
		l.logger.Printf("[INFO] "+format, args...)
	}
}

func (l *Logger) Warn(format string, args ...any) {
	if l.level <= LevelWarn {
		l.logger.Printf("[WARN] "+format, args...)
	}
}

func (l *Logger) Error(format string, args ...any) {
	if l.level <= LevelError {
		l.logger.Printf("[ERROR] "+format, args...)
	}
}

// Fatal logs at error level and exits.
func (l *Logger) Fatal(format string, args ...any) {
	l.logger.Printf("[FATAL] "+format, args...)
	os.Exit(1)
}

// logger is the package-level logger instance, set in main().
// Before main() calls initLogger, it defaults to stdout at info level.
var logger = NewLogger(LevelInfo, os.Stdout)
