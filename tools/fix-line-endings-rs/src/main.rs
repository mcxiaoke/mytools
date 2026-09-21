//! fix-line-endings: Fix line endings (CRLF <-> LF) for a file or all text
//! files in a directory. A zero-dependency (std-only) Rust port of
//! fix_line_endings.py.
//!
//! Usage:
//!     fix-line-endings <path> [--to lf|crlf] [--dry-run] [--ci]
//!                      [--ext .py,.dart] [--exclude-dir NAME ...]
//!
//! - Binary files are auto-detected and skipped.
//! - Default target ending is LF.
//! - --ci: check-only mode, no files are modified; exits with code 1 if any
//!   file does not match the target ending (for CI / git hooks).
//! - Exit codes: 0 = ok, 1 = CI check found files needing fixes, 2 = usage /
//!   path errors.

use std::collections::HashSet;
use std::env;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::process::ExitCode;

/// Bytes read from the head of a file for binary sniffing.
const BINARY_SNIFF_BYTES: usize = 8192;

/// Common binary extensions to skip outright (fast path, avoids sniffing).
const BINARY_EXTS: &[&str] = &[
    ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tiff", ".mp3", ".wav", ".ogg",
    ".flac", ".aac", ".m4a", ".mp4", ".avi", ".mov", ".mkv", ".webm", ".zip", ".7z", ".rar", ".gz",
    ".tar", ".bz2", ".xz", ".exe", ".dll", ".so", ".dylib", ".lib", ".a", ".obj", ".o", ".ttf",
    ".otf", ".woff", ".woff2", ".eot", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
    ".class", ".jar", ".pyc", ".pyd", ".wasm", ".db", ".sqlite", ".bin", ".dat", ".cache",
];

/// Options parsed from the command line. Mirrors the Python argparse setup.
struct Options {
    path: PathBuf,
    to: String,                    // "lf" | "crlf"
    dry_run: bool,                 // report only, do not modify
    ci: bool,                      // check-only for CI, exit 1 if fixes needed
    exts: Option<HashSet<String>>, // None => all non-binary files
    exclude_dirs: HashSet<String>,
}

fn main() -> ExitCode {
    match run() {
        Ok(code) => ExitCode::from(code),
        Err(msg) => {
            eprintln!("Error: {msg}");
            ExitCode::from(2)
        }
    }
}

fn run() -> Result<u8, String> {
    let args: Vec<String> = env::args().skip(1).collect();
    let opts = match parse_args(&args) {
        Ok(o) => o,
        Err(msg) => {
            // argparse-style: print usage + error to stderr, exit 2.
            eprintln!("usage: fix-line-endings [-h] [--to {{lf,crlf}}] [--dry-run] [--ci] [--ext EXT] [--exclude-dir [EXCLUDE_DIR ...]] path");
            eprintln!("fix-line-endings: error: {msg}");
            return Ok(2);
        }
    };

    let root = &opts.path;
    if !root.exists() {
        return Err(format!("path does not exist: {}", display_path(root)));
    }

    let ending = if opts.to == "lf" { "LF" } else { "CRLF" };
    let dry_run = if opts.ci { true } else { opts.dry_run };
    let mode = if opts.ci {
        "CI-CHECK"
    } else if opts.dry_run {
        "DRY-RUN"
    } else {
        "FIX"
    };

    let mut changed_count: u64 = 0;
    let mut checked: u64 = 0;

    for f in iter_files(root, &opts.exts, &opts.exclude_dirs) {
        if is_binary_file(&f) {
            continue;
        }
        checked += 1;
        let result = fix_file(&f, &opts.to, dry_run);
        match result {
            Ok((changed, count)) => {
                if changed {
                    changed_count += 1;
                    let tag = if dry_run { "WOULD FIX" } else { "FIXED" };
                    println!("{tag}  {}  ({count} line(s) -> {ending})", display_path(&f));
                }
            }
            Err(e) => {
                println!("SKIP  {} ({e})", display_path(&f));
            }
        }
    }

    if opts.ci {
        if changed_count > 0 {
            println!(
                "\n[CI-CHECK] target={ending}  checked={checked} text file(s), {changed_count} file(s) need fixing."
            );
            return Ok(1);
        }
        println!("\n[CI-CHECK] target={ending}  checked={checked} text file(s), all endings OK.");
        return Ok(0);
    }

    let action = if dry_run { "would be fixed" } else { "fixed" };
    println!(
        "\n[{mode}] target={ending}  checked={checked} text file(s), {changed_count} {action}."
    );
    Ok(0)
}

/// Parse command-line arguments by hand (no third-party deps).
fn parse_args(args: &[String]) -> Result<Options, String> {
    let mut path: Option<PathBuf> = None;
    let mut to = "lf".to_string();
    let mut dry_run = false;
    let mut ci = false;
    let mut ext_arg: Option<String> = None;
    let mut exclude_dirs: Vec<String> = Vec::new();

    let mut i = 0;
    while i < args.len() {
        let arg = &args[i];
        match arg.as_str() {
            "-h" | "--help" => {
                print_help();
                std::process::exit(0);
            }
            "--dry-run" => dry_run = true,
            "--ci" => ci = true,
            "--to" => {
                i += 1;
                let v = args
                    .get(i)
                    .ok_or_else(|| "argument --to: expected one argument".to_string())?;
                if v != "lf" && v != "crlf" {
                    return Err(format!(
                        "argument --to: invalid choice: '{v}' (choose from lf, crlf)"
                    ));
                }
                to = v.clone();
            }
            "--ext" => {
                i += 1;
                let v = args
                    .get(i)
                    .ok_or_else(|| "argument --ext: expected one argument".to_string())?;
                ext_arg = Some(v.clone());
            }
            "--exclude-dir" => {
                // nargs="*": collect the following bare tokens until an option
                // (starting with '-') or the end of the arguments.
                while i + 1 < args.len() && !args[i + 1].starts_with('-') {
                    i += 1;
                    exclude_dirs.push(args[i].clone());
                }
            }
            s if s.starts_with("--") && s.contains('=') => {
                let (key, value) = s.split_once('=').unwrap();
                match key {
                    "--to" => {
                        if value != "lf" && value != "crlf" {
                            return Err(format!(
                                "argument --to: invalid choice: '{value}' (choose from lf, crlf)"
                            ));
                        }
                        to = value.to_string();
                    }
                    "--ext" => ext_arg = Some(value.to_string()),
                    _ => return Err(format!("unrecognized arguments: {s}")),
                }
            }
            s if s.starts_with('-') => {
                return Err(format!("unrecognized arguments: {s}"));
            }
            pos => {
                if path.is_some() {
                    return Err(format!("unrecognized arguments: {pos}"));
                }
                path = Some(PathBuf::from(pos));
            }
        }
        i += 1;
    }

    let path = path.ok_or_else(|| "the following arguments are required: path".to_string())?;
    let exts = parse_ext_arg(ext_arg.as_deref());

    let mut dirs: HashSet<String> = HashSet::new();
    dirs.insert(".git".to_string());
    dirs.insert("node_modules".to_string());
    dirs.insert("__pycache__".to_string());
    for d in exclude_dirs {
        dirs.insert(d);
    }

    Ok(Options {
        path,
        to,
        dry_run,
        ci,
        exts,
        exclude_dirs: dirs,
    })
}

fn parse_ext_arg(value: Option<&str>) -> Option<HashSet<String>> {
    match value {
        None => None, // all non-binary files
        Some("") => None,
        Some(v) => {
            let mut set = HashSet::new();
            for e in v.split(',') {
                let e = e.trim().to_ascii_lowercase();
                if e.is_empty() {
                    continue;
                }
                if e.starts_with('.') {
                    set.insert(e);
                } else {
                    set.insert(format!(".{e}"));
                }
            }
            Some(set)
        }
    }
}

/// Binary detection: NUL byte, or a high ratio of control bytes in the head.
fn is_binary_file(path: &Path) -> bool {
    let mut head = [0u8; BINARY_SNIFF_BYTES];
    let n = match read_head(path, &mut head) {
        Ok(n) => n,
        Err(_) => return true, // unreadable -> treat as binary and skip
    };
    if n == 0 {
        return false; // empty file -> text
    }
    let chunk = &head[..n];
    if chunk.contains(&0) {
        return true;
    }
    let control = chunk
        .iter()
        .filter(|&&b| b < 9 || (13 < b && b < 32))
        .count();
    control as f64 / chunk.len() as f64 > 0.10
}

fn read_head(path: &Path, buf: &mut [u8]) -> io::Result<usize> {
    use std::io::Read;
    let mut f = fs::File::open(path)?;
    f.read(buf)
}

/// Normalize all line endings to target. Returns (new_data, changed_line_count).
///
/// Mirrors the Python semantics exactly:
/// - Unify CRLF / lone CR to LF first.
/// - Target LF  : changed count = number of CR bytes in the original data.
/// - Target CRLF: changed count = number of LF bytes after unification.
fn convert_bytes(data: &[u8], target: &str) -> (Vec<u8>, u64) {
    // Unify CRLF and lone CR to LF.
    let mut unified = Vec::with_capacity(data.len());
    let mut i = 0;
    let mut cr_count: u64 = 0;
    while i < data.len() {
        if data[i] == b'\r' {
            cr_count += 1;
            if i + 1 < data.len() && data[i + 1] == b'\n' {
                unified.push(b'\n');
                i += 2;
            } else {
                unified.push(b'\n');
                i += 1;
            }
        } else {
            unified.push(data[i]);
            i += 1;
        }
    }

    if target == "lf" {
        (unified, cr_count)
    } else {
        let lf_count = unified.iter().filter(|&&b| b == b'\n').count() as u64;
        let mut crlf = Vec::with_capacity(unified.len() + lf_count as usize);
        for &b in &unified {
            if b == b'\n' {
                crlf.push(b'\r');
            }
            crlf.push(b);
        }
        (crlf, lf_count)
    }
}

/// Returns Ok((changed, line_count)). On changed && !dry_run, writes the file.
fn fix_file(path: &Path, target: &str, dry_run: bool) -> io::Result<(bool, u64)> {
    let data = fs::read(path)?;
    let (new_data, count) = convert_bytes(&data, target);
    let changed = new_data != data;
    if changed && !dry_run {
        fs::write(path, &new_data)?;
    }
    Ok((changed, count))
}

/// Yield candidate files: a single file path as-is, or a sorted recursive walk
/// of a directory with the same filters as the Python version:
/// exclude dirs -> binary extensions -> user extension filter.
fn iter_files(
    root: &Path,
    exts: &Option<HashSet<String>>,
    exclude_dirs: &HashSet<String>,
) -> Vec<PathBuf> {
    if root.is_file() {
        return vec![root.to_path_buf()];
    }

    let mut files: Vec<PathBuf> = Vec::new();
    let mut dirs: Vec<PathBuf> = vec![root.to_path_buf()];

    while let Some(dir) = dirs.pop() {
        let entries = match fs::read_dir(&dir) {
            Ok(e) => e,
            Err(_) => continue,
        };
        for entry in entries.flatten() {
            let path = entry.path();
            let ft = match entry.file_type() {
                Ok(t) => t,
                Err(_) => continue,
            };
            if ft.is_dir() {
                dirs.push(path);
            } else if ft.is_file() {
                // Symbolic links to files: metadata says file, keep going below.
                files.push(path);
            } else if ft.is_symlink() {
                // Follow symlinks to files; never descend into symlinked dirs
                // (avoids cycles), matching pathlib.rglob defaults (no follow).
                if path.is_file() {
                    files.push(path);
                }
            }
        }
    }

    files.retain(|p| {
        if let Some(rel) = p.strip_prefix(root).ok() {
            // Skip if any parent dir component (excluding the file name) is
            // in the exclude set -- matches relative_to(root).parts[:-1].
            let parent_parts = rel.parent().map(|pp| pp.components().count()).unwrap_or(0);
            if parent_parts > 0 {
                for comp in rel.parent().unwrap().components() {
                    if let std::path::Component::Normal(os) = comp {
                        if exclude_dirs.contains(&os.to_string_lossy().to_string()) {
                            return false;
                        }
                    }
                }
            }
        }
        let ext = extension_lower(p);
        if BINARY_EXTS.contains(&ext.as_str()) {
            return false;
        }
        if let Some(set) = exts {
            if !set.contains(&ext) {
                return false;
            }
        }
        true
    });

    sort_paths(&mut files, root);
    files
}

/// Display a path the way Python's pathlib does on the current platform.
/// On Windows, pathlib normalizes separators to backslash.
fn display_path(p: &Path) -> String {
    let s = p.to_string_lossy();
    if cfg!(windows) {
        s.replace('/', "\\")
    } else {
        s.into_owned()
    }
}

fn extension_lower(p: &Path) -> String {
    match p.extension() {
        Some(os) => {
            let mut s = os.to_string_lossy().to_ascii_lowercase();
            if !s.starts_with('.') {
                s.insert(0, '.');
            }
            s
        }
        None => String::new(),
    }
}

/// Sort by the path relative to `root`, matching Python's
/// `sorted(root.rglob("*"))` ordering. Path components are compared in a
/// case-insensitive, component-wise fashion (approximation of pathlib on
/// Windows). Stable sort keeps OS enumeration order for equal keys.
fn sort_paths(paths: &mut Vec<PathBuf>, root: &Path) {
    paths.sort_by(|a, b| {
        let ka = sort_key(a, root);
        let kb = sort_key(b, root);
        ka.cmp(&kb)
    });
}

fn sort_key(p: &Path, root: &Path) -> Vec<String> {
    match p.strip_prefix(root) {
        Ok(rel) => rel
            .components()
            .map(|c| c.as_os_str().to_string_lossy().to_ascii_lowercase())
            .collect(),
        Err(_) => vec![p.to_string_lossy().to_ascii_lowercase()],
    }
}

fn print_help() {
    println!(
        "Fix line endings (CRLF <-> LF) for a file or all text files in a directory.\n\
         \n\
         Usage:\n\
         \x20   fix-line-endings <path> [--to lf|crlf] [--dry-run] [--ci] [--ext .py,.dart] [--exclude-dir NAME ...]\n\
         \n\
         - Binary files are auto-detected and skipped.\n\
         - Default target ending is LF.\n\
         - --ci: check-only mode, no files are modified; exits with code 1 if any\n\
         \x20  file does not match the target ending (for CI / git hooks).\n\
         \n\
         Options:\n\
         \x20 path             Input file or directory\n\
         \x20 --to lf|crlf     Target line ending (default: lf)\n\
         \x20 --dry-run        Only report what would change, do not modify files\n\
         \x20 --ci             Check-only mode for CI / git hooks: never modify\n\
         \x20                  files, exit 1 if any file needs fixing\n\
         \x20 --ext LIST       Comma-separated extensions, e.g. .py,.dart\n\
         \x20                  (default: all non-binary)\n\
         \x20 --exclude-dir N  Directory names to skip, e.g. build .git\n\
         \n\
         Exit codes:\n\
         \x20 0 = OK, 1 = CI check found files needing fixes, 2 = usage/path error"
    );
}
