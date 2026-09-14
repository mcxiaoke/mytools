// Tier: 0
//! 单一路径构造 to_verbatim + 物理路径逃逸校验（DESIGN §7.3 / §7.4）。
//! 规则：所有传裸 Win32 API 的路径必须经 to_verbatim()；禁止出现第二个路径构造函数。
#![allow(dead_code)]
use super::handle::H;
use super::wide;
use windows_sys::Win32::Foundation::{GetLastError, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, GetFinalPathNameByHandleW, FILE_FLAG_BACKUP_SEMANTICS, FILE_SHARE_DELETE,
    FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};

/// 判断是否为绝对路径（盘符 / UNC / 根路径）。
pub fn is_abs(p: &str) -> bool {
    let b = p.as_bytes();
    p.starts_with("\\\\") || (b.len() >= 2 && b[1] == b':') || p.starts_with('\\')
}

/// 规范化（不加前缀）：绝对化、统一 `\`、消解 `.`/`..`、去每段尾随点与空格。
/// 这是加 `\\?\` 前缀前的四条强制前置规范（DESIGN §7.3）。
pub fn normalize(input: &str) -> String {
    let mut s = input.trim().replace('/', "\\");
    // 先剥已有 verbatim 前缀，统一按无前缀形式处理
    if let Some(r) = s.strip_prefix("\\\\?\\UNC\\") {
        s = r.to_string();
    } else if let Some(r) = s.strip_prefix("\\\\?\\") {
        s = r.to_string();
    }
    let unc = s.starts_with("\\\\");
    let has_drive = s.len() >= 2 && s.as_bytes()[1] == b':';
    let rooted = s.starts_with('\\');
    if !unc && !has_drive && !rooted {
        if let Ok(cwd) = std::env::current_dir() {
            s = format!("{}\\{}", cwd.to_string_lossy(), s);
        }
    }
    let segs: Vec<&str> = s.split('\\').filter(|x| !x.is_empty()).collect();
    let mut stack: Vec<String> = Vec::new();
    let mut i = 0usize;
    if unc && segs.len() >= 2 {
        // UNC 的 server\share 两段按字面保留
        stack.push(segs[0].to_string());
        stack.push(segs[1].to_string());
        i = 2;
    }
    for seg in &segs[i..] {
        if *seg == "." {
            continue;
        }
        if *seg == ".." {
            stack.pop();
            continue;
        }
        let mut t = seg.to_string();
        while t.ends_with(' ') || t.ends_with('.') {
            t.pop();
        }
        if !t.is_empty() {
            stack.push(t);
        }
    }
    let joined = stack.join("\\");
    if unc {
        format!("\\\\{}", joined)
    } else if rooted {
        format!("\\{}", joined)
    } else {
        joined
    }
}

/// 裸 Win32 API 专用：统一加 verbatim 前缀（盘符路径加 `\\?\`，UNC 加 `\\?\UNC\`）。
pub fn to_verbatim(p: &str) -> String {
    let n = normalize(p);
    if n.starts_with("\\\\?\\") {
        return n;
    }
    if let Some(r) = n.strip_prefix("\\\\") {
        return format!("\\\\?\\UNC\\{}", r);
    }
    format!("\\\\?\\{}", n)
}

/// 大小写不敏感比较用的规范形式：无前缀、去尾随分隔符、小写。
pub fn norm_ci(p: &str) -> String {
    normalize(p).trim_end_matches('\\').to_lowercase()
}

/// child 是否严格位于 target 之下（含相等）。
pub fn is_under(target_ci: &str, child_ci: &str) -> bool {
    child_ci == target_ci || child_ci.starts_with(&format!("{}\\", target_ci))
}

/// 物理路径校验结论（分级判定由调用方执行，DESIGN §7.4）。
pub enum PathVerdict {
    /// 解析成功且位于 target 内。
    Ok,
    /// 解析成功但逃出 target：真正的攻击特征（Junction/Symlink 越界），必须拒绝。
    Escape,
    /// 解析失败（文件系统/网络盘不支持）：默认仅 WARNING，--strict-path-check 时拒绝。
    ResolveFailed(u32),
}

fn strip_verbatim(p: &str) -> String {
    if let Some(r) = p.strip_prefix("\\\\?\\UNC\\") {
        return format!("\\\\{}", r);
    }
    if let Some(r) = p.strip_prefix("\\\\?\\") {
        return r.to_string();
    }
    p.to_string()
}

/// 校验 dir_abs（可不存在，向上回溯最近存在祖先）解析后的物理路径位于 target 内。
pub fn physical_check(dir_abs: &str, target_ci: &str) -> PathVerdict {
    let mut cur = normalize(dir_abs);
    for _ in 0..64 {
        if std::path::Path::new(&cur).exists() {
            break;
        }
        let pp = std::path::Path::new(&cur).parent().map(|x| x.to_path_buf());
        match pp {
            Some(p) if !p.as_os_str().is_empty() && p != std::path::Path::new(&cur) => {
                cur = p.to_string_lossy().into_owned();
            }
            _ => break,
        }
    }
    let w = wide(&to_verbatim(&cur));
    let h = unsafe {
        CreateFileW(
            w.as_ptr(),
            0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            core::ptr::null(),
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            core::ptr::null_mut(),
        )
    };
    if h == INVALID_HANDLE_VALUE || h.is_null() {
        return PathVerdict::ResolveFailed(unsafe { GetLastError() });
    }
    let _g = H(h);
    let mut buf = vec![0u16; 1024];
    let mut n = unsafe { GetFinalPathNameByHandleW(h, buf.as_mut_ptr(), buf.len() as u32, 0) };
    if n == 0 {
        buf.resize(32768, 0);
        n = unsafe { GetFinalPathNameByHandleW(h, buf.as_mut_ptr(), buf.len() as u32, 0) };
        if n == 0 {
            return PathVerdict::ResolveFailed(unsafe { GetLastError() });
        }
    }
    buf.truncate(n as usize);
    let phys = norm_ci(&strip_verbatim(&super::from_wide(&buf)));
    if is_under(target_ci, &phys) {
        PathVerdict::Ok
    } else {
        PathVerdict::Escape
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalize_resolves_dots_and_case() {
        assert_eq!(normalize("C:\\a\\b\\..\\c"), "C:\\a\\c");
        assert_eq!(normalize("C:/a/./b/"), "C:\\a\\b");
        assert_eq!(norm_ci("c:\\app\\"), "c:\\app");
        assert_eq!(norm_ci("C:\\App"), "c:\\app");
    }

    #[test]
    fn normalize_trims_trailing_dots_and_spaces() {
        assert_eq!(normalize("C:\\a\\b. \\"), "C:\\a\\b");
        assert_eq!(normalize("C:\\a\\b..."), "C:\\a\\b");
    }

    #[test]
    fn verbatim_prefixes() {
        assert_eq!(to_verbatim("C:\\a\\b"), "\\\\?\\C:\\a\\b");
        assert_eq!(to_verbatim("\\\\srv\\share\\a"), "\\\\?\\UNC\\srv\\share\\a");
        assert_eq!(to_verbatim("\\\\?\\C:\\a"), "\\\\?\\C:\\a");
    }

    #[test]
    fn under_checks() {
        let t = norm_ci("C:\\App");
        assert!(is_under(&t, &norm_ci("C:\\App\\x\\y.dll")));
        assert!(is_under(&t, &t));
        assert!(!is_under(&t, &norm_ci("C:\\Appx\\y.dll")));
        assert!(!is_under(&t, &norm_ci("D:\\App\\y.dll")));
    }
}
