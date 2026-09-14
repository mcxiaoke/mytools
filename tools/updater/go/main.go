// Command updater 是一个通用、极简的 Windows 应用自动更新工具。
//
// 设计目标：解压 zip 覆盖目标目录，但绝不触碰保护清单内的路径（配置/数据），
// 不覆盖自身，先整体校验再原子替换，失败时写日志并重新拉起旧版本，
// 保证用户不会遇到"更新完应用消失了"。
//
// 用法：
//
//	updater.exe --pid <PID> --zip <ZIP> --target <DIR> --launch <EXE> [options]
//
// 完整参数见 README.md。
package main

import (
	"archive/zip"
	"bufio"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"os"
	"os/exec"
	"path"
	"path/filepath"
	"strings"
	"syscall"
	"time"
	"unsafe"
)

// ---------------------------------------------------------------- 常量

const (
	synchronize = 0x00100000

	waitObject0 = 0x00000000
	waitTimeout = 0x00000102

	detachedProcess    = 0x00000008
	createNewProcGroup = 0x00000200

	errAccessDenied  = syscall.Errno(5)
	errInvalidParam  = syscall.Errno(87)
	errAlreadyExists = syscall.Errno(183)
)

const (
	exitOK    = 0
	exitFail  = 1
	exitUsage = 2
)

const defaultKeepFile = ".updatekeep"

var (
	kernel32 = syscall.NewLazyDLL("kernel32.dll")
	shell32  = syscall.NewLazyDLL("shell32.dll")

	procOpenProcess         = kernel32.NewProc("OpenProcess")
	procWaitForSingleObject = kernel32.NewProc("WaitForSingleObject")
	procCloseHandle         = kernel32.NewProc("CloseHandle")
	procCreateMutexW        = kernel32.NewProc("CreateMutexW")

	procShellExecuteW = shell32.NewProc("ShellExecuteW")

	logger  *log.Logger
	logFile *os.File
)

// ---------------------------------------------------------------- 参数

type stringList []string

func (s *stringList) String() string { return strings.Join(*s, ",") }

func (s *stringList) Set(v string) error {
	*s = append(*s, v)
	return nil
}

type config struct {
	pid        int
	zipPath    string
	targetDir  string
	launchExe  string
	launchArgs string

	keepFile string
	keeps    stringList
	requires stringList
	strip    int

	deleteZip bool
	dryRun    bool
	silent    bool
	elevate   bool

	timeout    time.Duration
	logPath    string
	writeRetry int
	writeDelay time.Duration

	// 无条件保护的绝对路径（与保护清单解耦）
	selfRel string // 正在运行的 updater 相对 target 的路径，空表示不在 target 内
	keepRel string // 保护清单文件相对 target 的路径
}

func parseFlags() (*config, error) {
	var (
		pid        = flag.Int("pid", 0, "等待退出的主进程 PID")
		zipPath    = flag.String("zip", "", "更新包 zip 路径")
		targetDir  = flag.String("target", "", "被覆盖的目标目录")
		launchExe  = flag.String("launch", "", "更新完成后拉起的可执行文件（相对 target 或绝对路径）")
		launchArgs = flag.String("args", "", "拉起时附加的命令行参数")

		keepFile = flag.String("keep-file", defaultKeepFile, "保护清单文件名（位于 target 根目录）")
		strip    = flag.Int("strip", 0, "剥离 zip 包内前 N 层目录（应对打包时多一层顶层目录）")

		deleteZip = flag.Bool("delete-zip", false, "更新成功后删除 zip")
		dryRun    = flag.Bool("dry-run", false, "只输出计划，不写入任何文件")
		silent    = flag.Bool("silent", false, "不输出任何日志到控制台（仍写日志文件）")
		elevate   = flag.Bool("elevate", false, "目标目录不可写时尝试自提权")

		timeout    = flag.Int("timeout", 60, "等待主进程退出的超时秒数")
		logPath    = flag.String("log", "", "日志文件路径（默认写入 %TEMP%）")
		writeRetry = flag.Int("write-retries", 10, "单文件替换失败重试次数")
		writeDelay = flag.Int("write-delay-ms", 300, "单文件替换重试间隔毫秒")
	)

	var keeps, requires stringList
	flag.Var(&keeps, "keep", "额外保护的相对路径（可重复，支持 glob）")
	flag.Var(&requires, "require", "更新包中必须存在的相对路径（可重复）")

	flag.Usage = func() {
		fmt.Fprintf(os.Stderr, "用法: %s --pid <PID> --zip <ZIP> --target <DIR> --launch <EXE> [options]\n\n", filepath.Base(os.Args[0]))
		flag.PrintDefaults()
	}

	if err := flag.CommandLine.Parse(os.Args[1:]); err != nil {
		return nil, err
	}

	cfg := &config{
		pid:        *pid,
		zipPath:    *zipPath,
		targetDir:  *targetDir,
		launchExe:  *launchExe,
		launchArgs: *launchArgs,
		keepFile:   *keepFile,
		keeps:      keeps,
		requires:   requires,
		strip:      *strip,
		deleteZip:  *deleteZip,
		dryRun:     *dryRun,
		silent:     *silent,
		elevate:    *elevate,
		timeout:    time.Duration(*timeout) * time.Second,
		logPath:    *logPath,
		writeRetry: *writeRetry,
		writeDelay: time.Duration(*writeDelay) * time.Millisecond,
	}

	if err := cfg.validate(); err != nil {
		return nil, err
	}
	return cfg, nil
}

func (c *config) validate() error {
	if c.zipPath == "" {
		return errors.New("缺少 --zip")
	}
	if c.targetDir == "" {
		return errors.New("缺少 --target")
	}
	abs, err := filepath.Abs(c.targetDir)
	if err != nil {
		return fmt.Errorf("解析 --target 失败: %w", err)
	}
	c.targetDir = abs

	absZip, err := filepath.Abs(c.zipPath)
	if err != nil {
		return fmt.Errorf("解析 --zip 失败: %w", err)
	}
	c.zipPath = absZip

	if c.strip < 0 {
		return errors.New("--strip 不能为负数")
	}
	if c.writeRetry < 1 {
		c.writeRetry = 1
	}
	c.resolveSpecialPaths()
	return nil
}

// cleanAbs 归一化绝对路径，去掉 Windows 扩展长度前缀。
func cleanAbs(p string) string {
	p = filepath.Clean(p)
	return strings.TrimPrefix(p, `\\?\`)
}

// relWithin 返回 p 相对 dir 的路径（正斜杠分隔）；p 不在 dir 内时第二个返回值为 false。
func relWithin(dir, p string) (string, bool) {
	rel, err := filepath.Rel(cleanAbs(dir), cleanAbs(p))
	if err != nil {
		return "", false
	}
	if rel == "." || rel == ".." || strings.HasPrefix(rel, ".."+string(os.PathSeparator)) {
		return "", false
	}
	return filepath.ToSlash(rel), true
}

// resolveSpecialPaths 计算两个必须无条件保护的路径，避免依赖调用方把 updater 放在 target 之外。
//
// 运行中的 exe 在 Windows 下被独占锁住，写入必然失败；因此只要它位于 target 内，
// 就精确保护它自己那一个路径——既不误伤同名文件，也不需要调用方做任何约定。
func (c *config) resolveSpecialPaths() {
	if self, err := os.Executable(); err == nil {
		if rel, ok := relWithin(c.targetDir, self); ok {
			c.selfRel = rel
		}
	}
	if c.keepFile != "" {
		kp := c.keepFile
		if !filepath.IsAbs(kp) {
			kp = filepath.Join(c.targetDir, kp)
		}
		if rel, ok := relWithin(c.targetDir, kp); ok {
			c.keepRel = rel
		}
	}
}

// ---------------------------------------------------------------- 日志

func setupLogger(c *config) error {
	if c.logPath == "" {
		c.logPath = filepath.Join(os.TempDir(), "updater-"+time.Now().Format("20060102-150405")+".log")
	}
	f, err := os.OpenFile(c.logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return fmt.Errorf("打开日志文件失败: %w", err)
	}
	logFile = f

	writers := []io.Writer{f}
	if !c.silent {
		writers = append(writers, os.Stderr)
	}
	logger = log.New(io.MultiWriter(writers...), "", log.LstdFlags)
	return nil
}

// ---------------------------------------------------------------- Win32 封装

func errnoOf(err error) syscall.Errno {
	if err == nil {
		return 0
	}
	if e, ok := err.(syscall.Errno); ok {
		return e
	}
	return 0
}

// waitProcessExit 等待指定 PID 退出。返回 nil 表示进程已退出或本来就不存在。
func waitProcessExit(pid int, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	var lastErrno syscall.Errno

	for {
		handle, _, err := procOpenProcess.Call(synchronize, 0, uintptr(pid))
		if handle != 0 {
			remain := time.Until(deadline)
			if remain < 0 {
				remain = 0
			}
			ret, _, _ := procWaitForSingleObject.Call(handle, uintptr(remain.Milliseconds()))
			procCloseHandle.Call(handle)
			switch ret {
			case waitObject0:
				return nil
			case waitTimeout:
				return fmt.Errorf("等待进程退出超时（pid=%d，超过 %s）", pid, timeout)
			default:
				return fmt.Errorf("WaitForSingleObject 返回 %d", ret)
			}
		}

		lastErrno = errnoOf(err)
		if lastErrno == errInvalidParam {
			// 进程不存在，视为已退出
			return nil
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("无法获取进程句柄（pid=%d, errno=%d），等待超时", pid, lastErrno)
		}
		time.Sleep(200 * time.Millisecond)
	}
}

var errAlreadyRunning = errors.New("已有 updater 实例在运行")

func acquireMutex(name string) (uintptr, error) {
	p, err := syscall.UTF16PtrFromString(name)
	if err != nil {
		return 0, err
	}
	handle, _, callErr := procCreateMutexW.Call(0, 0, uintptr(unsafe.Pointer(p)))
	if handle == 0 {
		return 0, fmt.Errorf("CreateMutexW 失败: %v", callErr)
	}
	if errnoOf(callErr) == errAlreadyExists {
		procCloseHandle.Call(handle)
		return 0, errAlreadyRunning
	}
	return handle, nil
}

// selfElevate 以管理员身份重新启动自己，成功后调用方应立刻退出。
func selfElevate(args []string) error {
	exe, err := os.Executable()
	if err != nil {
		return err
	}
	verb, _ := syscall.UTF16PtrFromString("runas")
	file, _ := syscall.UTF16PtrFromString(exe)
	params, _ := syscall.UTF16PtrFromString(quoteArgs(args))
	dir, _ := syscall.UTF16PtrFromString(filepath.Dir(exe))

	ret, _, _ := procShellExecuteW.Call(
		0,
		uintptr(unsafe.Pointer(verb)),
		uintptr(unsafe.Pointer(file)),
		uintptr(unsafe.Pointer(params)),
		uintptr(unsafe.Pointer(dir)),
		1, // SW_SHOWNORMAL
	)
	if ret <= 32 {
		return fmt.Errorf("ShellExecuteW(runas) 失败，返回码 %d", ret)
	}
	return nil
}

// ---------------------------------------------------------------- 权限预检

func dirWritable(dir string) error {
	probe := filepath.Join(dir, ".updater-write-probe")
	f, err := os.OpenFile(probe, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o644)
	if err != nil {
		return err
	}
	f.Close()
	os.Remove(probe)
	return nil
}

// ---------------------------------------------------------------- 保护清单

type keepRule struct {
	pattern string // 已归一化：小写、正斜杠、无前后斜杠
	dirOnly bool   // 原样以 / 或 \ 结尾，仅按目录前缀匹配
	glob    bool
}

type keepSet struct {
	rules []keepRule
}

func newRule(raw string) (keepRule, bool) {
	line := strings.TrimSpace(raw)
	if line == "" || strings.HasPrefix(line, "#") {
		return keepRule{}, false
	}
	dirOnly := strings.HasSuffix(line, "/") || strings.HasSuffix(line, "\\")
	p := strings.ReplaceAll(line, "\\", "/")
	p = strings.TrimPrefix(p, "./")
	p = strings.Trim(p, "/")
	if p == "" {
		return keepRule{}, false
	}
	return keepRule{
		pattern: strings.ToLower(p),
		dirOnly: dirOnly,
		glob:    strings.ContainsAny(p, "*?["),
	}, true
}

func (k *keepSet) add(raw string) {
	if r, ok := newRule(raw); ok {
		k.rules = append(k.rules, r)
	}
}

func (k *keepSet) loadFromFile(p string) (int, error) {
	f, err := os.Open(p)
	if err != nil {
		if os.IsNotExist(err) {
			return 0, nil
		}
		return 0, err
	}
	defer f.Close()

	n := 0
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 0, 64*1024), 1024*1024)
	for sc.Scan() {
		before := len(k.rules)
		k.add(sc.Text())
		if len(k.rules) > before {
			n++
		}
	}
	return n, sc.Err()
}

// matches 判断相对路径（正斜杠分隔）是否命中保护规则。
func (k *keepSet) matches(rel string) bool {
	rel = strings.ToLower(strings.ReplaceAll(rel, "\\", "/"))
	base := path.Base(rel)
	var ancestors []string
	for d := path.Dir(rel); d != "." && d != "/" && d != ""; d = path.Dir(d) {
		ancestors = append(ancestors, d)
	}

	for _, r := range k.rules {
		switch {
		case r.glob:
			if ok, _ := path.Match(r.pattern, rel); ok {
				return true
			}
			if ok, _ := path.Match(r.pattern, base); ok {
				return true
			}
			for _, a := range ancestors {
				if ok, _ := path.Match(r.pattern, a); ok {
					return true
				}
			}
		case r.dirOnly:
			if rel == r.pattern || strings.HasPrefix(rel, r.pattern+"/") {
				return true
			}
		default:
			// 字面量：精确匹配自身，或作为目录前缀匹配（config 命中 config/x.json）
			// 故意不做 basename 匹配，避免 config 规则误伤 config.ini
			if rel == r.pattern || strings.HasPrefix(rel, r.pattern+"/") {
				return true
			}
		}
	}
	return false
}

func (k *keepSet) String() string {
	out := make([]string, 0, len(k.rules))
	for _, r := range k.rules {
		s := r.pattern
		if r.dirOnly {
			s += "/"
		}
		out = append(out, s)
	}
	return strings.Join(out, " ")
}

// ---------------------------------------------------------------- zip 解析

type entry struct {
	rel    string
	zidx   int
	isDir  bool
	kept   bool
	reason string
}

// safeRelPath 归一化 zip 条目名并拦截路径穿越（Zip Slip）。
func safeRelPath(name string) (string, error) {
	n := strings.ReplaceAll(name, "\\", "/")
	if n == "" {
		return "", errors.New("空条目名")
	}
	if strings.HasPrefix(n, "/") {
		return "", fmt.Errorf("绝对路径不被允许: %s", name)
	}
	if len(n) >= 2 && n[1] == ':' {
		return "", fmt.Errorf("盘符路径不被允许: %s", name)
	}
	clean := path.Clean(n)
	if clean == "." {
		return "", nil
	}
	if clean == ".." || strings.HasPrefix(clean, "../") {
		return "", fmt.Errorf("条目路径逃出目标目录: %s", name)
	}
	return clean, nil
}

func stripSegments(rel string, n int) (string, bool) {
	if n <= 0 {
		return rel, true
	}
	segs := strings.Split(rel, "/")
	if len(segs) <= n {
		return "", false
	}
	return strings.Join(segs[n:], "/"), true
}

// buildPlan 解析 zip 目录，完成结构校验与保护清单过滤。
func buildPlan(zr *zip.Reader, c *config, keep *keepSet) ([]entry, error) {
	if len(zr.File) == 0 {
		return nil, errors.New("更新包为空")
	}

	plans := make([]entry, 0, len(zr.File))
	found := make(map[string]bool)

	for i, f := range zr.File {
		clean, err := safeRelPath(f.Name)
		if err != nil {
			return nil, err
		}
		if clean == "" {
			continue
		}
		rel, ok := stripSegments(clean, c.strip)
		if !ok || rel == "" {
			continue
		}
		isDir := strings.HasSuffix(f.Name, "/") || f.FileInfo().IsDir()
		e := entry{rel: rel, zidx: i, isDir: isDir}
		switch {
		case c.selfRel != "" && strings.EqualFold(rel, c.selfRel):
			// 硬性不变量：绝不覆盖正在运行的自己（Windows 下必然失败）
			e.kept = true
			e.reason = "updater 自身正在运行"
		case c.keepRel != "" && strings.EqualFold(rel, c.keepRel):
			e.kept = true
			e.reason = "保护清单文件自身"
		case keep.matches(rel):
			e.kept = true
			e.reason = "保护清单命中"
		}
		if !isDir {
			found[strings.ToLower(rel)] = true
		}
		plans = append(plans, e)
	}

	if len(plans) == 0 {
		return nil, fmt.Errorf("剥离 %d 层目录后没有可写入的内容，请检查 --strip", c.strip)
	}

	for _, r := range c.requires {
		key := strings.ToLower(strings.ReplaceAll(strings.TrimLeft(r, "./\\"), "\\", "/"))
		if !found[key] {
			return nil, fmt.Errorf("更新包中缺少必需文件: %s", r)
		}
	}

	// 防御：再确认一次所有写入路径都落在目标目录内
	for _, p := range plans {
		dst := filepath.Join(c.targetDir, filepath.FromSlash(p.rel))
		if !withinDir(c.targetDir, dst) {
			return nil, fmt.Errorf("条目逃出目标目录: %s", p.rel)
		}
	}
	return plans, nil
}

func withinDir(dir, p string) bool {
	d := filepath.Clean(dir)
	t := filepath.Clean(p)
	if t == d {
		return true
	}
	return strings.HasPrefix(strings.ToLower(t), strings.ToLower(d)+string(os.PathSeparator))
}

// ---------------------------------------------------------------- 写入

// writeFileAtomic 先写同目录临时文件再 rename，避免中途失败留下半截文件。
func writeFileAtomic(dst string, src io.Reader, mode os.FileMode, retry int, delay time.Duration) error {
	dir := filepath.Dir(dst)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}

	tmp, err := os.CreateTemp(dir, ".upd-*")
	if err != nil {
		return err
	}
	tmpName := tmp.Name()
	committed := false
	defer func() {
		if !committed {
			os.Remove(tmpName)
		}
	}()

	if _, err := io.Copy(tmp, src); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Close(); err != nil {
		return err
	}
	if mode != 0 {
		_ = os.Chmod(tmpName, mode)
	}

	var lastErr error
	for i := 0; i < retry; i++ {
		if err := os.Rename(tmpName, dst); err == nil {
			committed = true
			return nil
		} else {
			lastErr = err
		}
		if i < retry-1 {
			time.Sleep(delay)
		}
	}
	return fmt.Errorf("替换 %s 失败（已重试 %d 次）: %w", filepath.Base(dst), retry, lastErr)
}

type applyStats struct {
	written int
	skipped int
}

func applyPlan(zr *zip.Reader, plans []entry, c *config) (applyStats, error) {
	var st applyStats
	var firstErr error

	for _, p := range plans {
		if p.kept {
			st.skipped++
			continue
		}
		// 纵深防御：即使规划阶段被绕过，也绝不覆盖正在运行的自身
		if c.selfRel != "" && strings.EqualFold(p.rel, c.selfRel) {
			logger.Printf("SKIP   %s（updater 自身正在运行，硬性跳过）", p.rel)
			st.skipped++
			continue
		}
		dst := filepath.Join(c.targetDir, filepath.FromSlash(p.rel))
		if p.isDir {
			if err := os.MkdirAll(dst, 0o755); err != nil {
				logger.Printf("WARN  创建目录失败 %s: %v", p.rel, err)
				if firstErr == nil {
					firstErr = err
				}
			}
			continue
		}

		rc, err := zr.File[p.zidx].Open()
		if err != nil {
			logger.Printf("ERROR 读取包内条目失败 %s: %v", p.rel, err)
			if firstErr == nil {
				firstErr = err
			}
			continue
		}
		mode := zr.File[p.zidx].Mode()
		err = writeFileAtomic(dst, rc, mode, c.writeRetry, c.writeDelay)
		rc.Close()
		if err != nil {
			logger.Printf("ERROR 写入失败 %s: %v", p.rel, err)
			if firstErr == nil {
				firstErr = err
			}
			continue
		}
		logger.Printf("OK    写入 %s", p.rel)
		st.written++
	}
	return st, firstErr
}

// ---------------------------------------------------------------- 进程拉起

func quoteArgs(args []string) string {
	parts := make([]string, 0, len(args))
	for _, a := range args {
		if a == "" {
			parts = append(parts, `""`)
			continue
		}
		if strings.ContainsAny(a, " \t\"") {
			parts = append(parts, `"`+strings.ReplaceAll(a, `"`, `\"`)+`"`)
			continue
		}
		parts = append(parts, a)
	}
	return strings.Join(parts, " ")
}

// splitArgs 解析 --args 传入的原始参数串，支持双引号包裹。
func splitArgs(s string) []string {
	var (
		out     []string
		cur     strings.Builder
		inQuote bool
		started bool
	)
	for _, r := range s {
		switch {
		case r == '"':
			inQuote = !inQuote
			started = true
		case (r == ' ' || r == '\t') && !inQuote:
			if started {
				out = append(out, cur.String())
				cur.Reset()
				started = false
			}
		default:
			cur.WriteRune(r)
			started = true
		}
	}
	if started {
		out = append(out, cur.String())
	}
	return out
}

func launchApp(targetDir, exeName, extraArgs string) error {
	if exeName == "" {
		return errors.New("未指定 --launch，跳过拉起")
	}
	exePath := exeName
	if !filepath.IsAbs(exePath) {
		exePath = filepath.Join(targetDir, exeName)
	}
	if _, err := os.Stat(exePath); err != nil {
		return fmt.Errorf("可执行文件不存在: %s", exePath)
	}

	cmd := exec.Command(exePath, splitArgs(extraArgs)...)
	cmd.Dir = targetDir
	cmd.Stdin = nil
	cmd.Stdout = nil
	cmd.Stderr = nil
	cmd.SysProcAttr = &syscall.SysProcAttr{
		CreationFlags: detachedProcess | createNewProcGroup,
	}
	if err := cmd.Start(); err != nil {
		return err
	}
	return cmd.Process.Release()
}

// ---------------------------------------------------------------- 主流程

func main() {
	os.Exit(run())
}

func run() int {
	cfg, err := parseFlags()
	if err != nil {
		if errors.Is(err, flag.ErrHelp) {
			return exitOK
		}
		fmt.Fprintln(os.Stderr, "updater:", err)
		return exitUsage
	}
	if err := setupLogger(cfg); err != nil {
		fmt.Fprintln(os.Stderr, "updater:", err)
		return exitUsage
	}
	defer func() {
		if logFile != nil {
			logFile.Close()
		}
	}()

	logger.Printf("INFO  updater 启动 pid=%d", cfg.pid)
	logger.Printf("INFO  zip=%s", cfg.zipPath)
	logger.Printf("INFO  target=%s", cfg.targetDir)
	logger.Printf("INFO  launch=%s args=%q strip=%d dry-run=%v", cfg.launchExe, cfg.launchArgs, cfg.strip, cfg.dryRun)

	// 0) 前置校验
	if fi, err := os.Stat(cfg.zipPath); err != nil {
		return abort(cfg, "更新包不可读", err)
	} else if fi.IsDir() {
		return abort(cfg, "更新包路径是目录", nil)
	}
	if fi, err := os.Stat(cfg.targetDir); err != nil {
		return abort(cfg, "目标目录不可读", err)
	} else if !fi.IsDir() {
		return abort(cfg, "目标路径不是目录", nil)
	}

	// 1) 写权限预检 / 自提权
	if !cfg.dryRun {
		if err := dirWritable(cfg.targetDir); err != nil {
			if cfg.elevate {
				logger.Printf("WARN  目标目录不可写，尝试自提权: %v", err)
				if e := selfElevate(os.Args[1:]); e != nil {
					return abort(cfg, "自提权失败", e)
				}
				logger.Printf("INFO  已拉起管理员实例，当前实例退出")
				return exitOK
			}
			return abort(cfg, "目标目录不可写（可加 --elevate 提权）", err)
		}
	}

	// 2) 单实例互斥
	mutex, err := acquireMutex(`Local\updater-` + strings.ToLower(strings.ReplaceAll(cfg.targetDir, `\`, `-`)))
	if err != nil {
		return abort(cfg, "获取单实例锁失败", err)
	}
	defer procCloseHandle.Call(mutex)

	// 3) 等待主进程退出
	if cfg.pid > 0 {
		logger.Printf("INFO  等待进程 %d 退出（最多 %s）", cfg.pid, cfg.timeout)
		if err := waitProcessExit(cfg.pid, cfg.timeout); err != nil {
			return abort(cfg, "等待主进程退出失败", err)
		}
		logger.Printf("INFO  进程 %d 已退出", cfg.pid)
	}

	// 4) 加载保护清单
	keep := &keepSet{}
	keepFileParsed := 0
	if cfg.keepFile != "" {
		p := cfg.keepFile
		if !filepath.IsAbs(p) {
			p = filepath.Join(cfg.targetDir, p)
		}
		keepFileParsed, err = keep.loadFromFile(p)
		if err != nil {
			return abort(cfg, "读取保护清单失败", err)
		}
	}
	for _, r := range cfg.keeps {
		keep.add(r)
	}
	if cfg.selfRel != "" {
		logger.Printf("INFO  updater 自身位于 target 内（%s），该路径无条件跳过", cfg.selfRel)
	} else {
		logger.Printf("INFO  updater 运行于 target 之外")
	}
	logger.Printf("INFO  保护清单（%d 条来自清单文件）: %s", keepFileParsed, keep.String())

	// 5) 解析并校验更新包
	zr, err := zip.OpenReader(cfg.zipPath)
	if err != nil {
		return abort(cfg, "打开更新包失败", err)
	}

	plans, err := buildPlan(&zr.Reader, cfg, keep)
	if err != nil {
		zr.Close()
		return abort(cfg, "更新包校验失败", err)
	}
	total := 0
	for _, p := range plans {
		if !p.isDir {
			total++
		}
	}
	logger.Printf("INFO  包内条目 %d，待写入文件 %d，受保护跳过 %d", len(plans), total-len(keptPath(plans)), len(keptPath(plans)))

	if cfg.dryRun {
		for _, p := range plans {
			if p.isDir {
				continue
			}
			if p.kept {
				logger.Printf("PLAN  [跳过] %s (%s)", p.rel, p.reason)
			} else {
				logger.Printf("PLAN  [写入] %s", p.rel)
			}
		}
		zr.Close()
		logger.Printf("INFO  dry-run 结束，未做任何改动")
		return exitOK
	}

	// 6) 执行写入
	stats, applyErr := applyPlan(&zr.Reader, plans, cfg)
	// Windows 下文件句柄未释放会导致后续删除失败，必须先关闭
	zr.Close()
	logger.Printf("INFO  写入 %d 个文件，跳过 %d 个受保护条目", stats.written, stats.skipped)

	if applyErr != nil {
		logger.Printf("ERROR 更新未完整成功，保留更新包以便重试: %s", cfg.zipPath)
	}

	// 7) 清理
	if applyErr == nil && cfg.deleteZip {
		if err := os.Remove(cfg.zipPath); err != nil {
			logger.Printf("WARN  删除更新包失败: %v", err)
		}
	}

	// 8) 拉起应用（失败时也拉起，保证用户还能用）
	if err := launchApp(cfg.targetDir, cfg.launchExe, cfg.launchArgs); err != nil {
		logger.Printf("ERROR 拉起应用失败: %v", err)
		if applyErr == nil {
			applyErr = err
		}
	} else {
		logger.Printf("INFO  已拉起 %s", cfg.launchExe)
	}

	if applyErr != nil {
		logger.Printf("ERROR 更新结束（失败），日志: %s", cfg.logPath)
		return exitFail
	}
	logger.Printf("INFO  更新结束（成功），日志: %s", cfg.logPath)
	return exitOK
}

func keptPath(plans []entry) []entry {
	out := make([]entry, 0)
	for _, p := range plans {
		if p.kept && !p.isDir {
			out = append(out, p)
		}
	}
	return out
}

// abort 记录失败原因，尽量把旧版本拉起来，然后返回失败退出码。
func abort(cfg *config, msg string, err error) int {
	if err != nil {
		logger.Printf("ERROR %s: %v", msg, err)
	} else {
		logger.Printf("ERROR %s", msg)
	}
	if cfg.launchExe != "" {
		if e := launchApp(cfg.targetDir, cfg.launchExe, cfg.launchArgs); e != nil {
			logger.Printf("ERROR 兜底拉起应用失败: %v", e)
		} else {
			logger.Printf("INFO  已兜底拉起 %s", cfg.launchExe)
		}
	}
	logger.Printf("ERROR 更新结束（失败），日志: %s", cfg.logPath)
	return exitFail
}
