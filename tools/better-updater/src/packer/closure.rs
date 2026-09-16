// Tier: 1
//! 启动闭包判定与 zip 条目排序 —— 这是整个打包流程里**唯一不能出错**的一段。
//!
//! 为什么需要它（`USAGE.md` §3.1 / §13.3）：Windows 的 loader 在**任何用户代码之前**加载入口 exe 的
//! import 表。若一次更新在"启动闭包"内部被中断（掉电、或施工进程与看门狗都被杀），应用会**起不来**，
//! 于是调用方的启动自检也跑不到，只剩"双击 updater.exe"这一条人工入口。
//! 把闭包整体**连续排在 zip 末尾**，能把"中断落在致命组合上"的概率从"覆盖整个事务"压到"闭包自身那一小段"：
//! 闭包之前被中断 ⇒ 闭包整体仍是旧版 ⇒ 应用照常启动 ⇒ 能自愈。
//!
//! 判定规则（与 `scripts/release_pack.ps1` 逐条对齐，可由 stage 根的 `.bootclosure` 覆盖）：
//! - 基线：**根目录直系文件**（相对路径不含分隔符）—— 入口 exe 与各 DLL 都在此；
//! - 附加：默认 `data/*.so` 与 `data/*.dat`（Flutter 的 Dart AOT 快照与 ICU 数据）；
//! - 覆盖：`.bootclosure` 每行一条 glob（`#` 注释），**替换**默认附加项；
//! - 包元数据 `updater.manifest` 永远不算闭包（更新器把它当元数据拦掉，它不会被写到磁盘）。
use crate::packer::manifest::MANIFEST_NAME;
use crate::packer::walk::Item;
use std::path::Path;

/// 覆盖文件（stage 根，不入包）。
pub const CFG_NAME: &str = ".bootclosure";

/// 默认附加模式（与脚本一致）。
pub const DEFAULT_PATTERNS: [&str; 2] = ["data/*.so", "data/*.dat"];

/// 读取模式：有 `.bootclosure` 则用它（可读到则视为生效），否则用默认。
pub fn patterns(stage: &Path) -> Result<(Vec<String>, bool), String> {
    let p = stage.join(CFG_NAME);
    if !p.exists() {
        return Ok((DEFAULT_PATTERNS.iter().map(|s| s.to_string()).collect(), false));
    }
    let text = std::fs::read_to_string(&p).map_err(|e| format!("read {}: {}", p.display(), e))?;
    let mut out = Vec::new();
    for line in text.lines() {
        let t = line.trim();
        if t.is_empty() || t.starts_with('#') {
            continue;
        }
        out.push(t.replace('\\', "/"));
    }
    Ok((out, true))
}

/// 单条目是否属于启动闭包。`rel` 为 `'\'` 分隔的相对路径。
pub fn is_closure(rel: &str, patterns: &[String]) -> bool {
    let n = rel.replace('\\', "/");
    if n == MANIFEST_NAME {
        return false;
    }
    if !n.contains('/') {
        return true; // 根目录直系文件
    }
    let name: Vec<char> = n.chars().collect();
    patterns.iter().any(|p| {
        let pat: Vec<char> = p.chars().collect();
        crate::keep::glob_match(&pat, &name)
    })
}

/// 排序结果：非闭包文件 → 空目录条目 → **闭包文件连续收尾**。
///
/// `items` 可含全部目录；这里只为**空**目录产条目（非空目录由文件动作顺带创建，
/// 写进 zip 只是冗余 —— 与旧脚本的空目录判定一致）。
pub struct Order<'a> {
    pub non_closure: Vec<&'a Item>,
    pub empty_dirs: Vec<&'a Item>,
    pub closure: Vec<&'a Item>,
}

/// 生成顺序。三组的组内排序都用**字节序**（跨平台确定；脚本用区域敏感的 `Sort-Object`，
/// 顺序可能不同，但闭包"连续收尾"这一性质两者一致——这才是契约）。
pub fn order<'a>(items: &'a [Item], patterns: &[String]) -> Order<'a> {
    let mut non = Vec::new();
    let mut clo = Vec::new();
    for it in items.iter().filter(|i| !i.is_dir) {
        if is_closure(&it.rel, patterns) {
            clo.push(it);
        } else {
            non.push(it);
        }
    }
    non.sort_by(|a, b| a.rel.as_bytes().cmp(b.rel.as_bytes()));
    clo.sort_by(|a, b| a.rel.as_bytes().cmp(b.rel.as_bytes()));
    let mut dirs: Vec<&Item> = items.iter().filter(|i| i.is_dir && i.empty_dir).collect();
    dirs.sort_by(|a, b| a.rel.as_bytes().cmp(b.rel.as_bytes()));
    Order { non_closure: non, empty_dirs: dirs, closure: clo }
}

/// 回读自检用：给定**实际**的文件条目序列（不含目录条目）与闭包集合，判断闭包是否构成连续尾部。
/// 返回 `Err(说明)`：不满足时把"闭包成员 vs 实际尾部"列出来（与脚本的失败信息同型）。
pub fn closure_is_a_contiguous_tail(actual_files: &[String], closure: &[String]) -> Result<(), String> {
    if closure.is_empty() || actual_files.is_empty() {
        return Ok(());
    }
    if closure.len() > actual_files.len() {
        return Err(format!("closure count {} > file entries {}", closure.len(), actual_files.len()));
    }
    let tail = &actual_files[actual_files.len() - closure.len()..];
    let mut a: Vec<String> = tail.to_vec();
    let mut b: Vec<String> = closure.to_vec();
    a.sort();
    b.sort();
    if a != b {
        return Err(format!(
            "boot closure is not a contiguous tail of the zip -- an interrupted update could leave \
             the app unable to start.\n  closure ({}): {}\n  actual tail ({}): {}",
            b.len(),
            b.join(", "),
            a.len(),
            a.join(", ")
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn it(rel: &str) -> Item {
        Item { rel: rel.to_string(), full: PathBuf::from(rel), size: 1, is_dir: false, empty_dir: false }
    }
    fn dir(rel: &str) -> Item {
        Item { rel: rel.to_string(), full: PathBuf::from(rel), size: 0, is_dir: true, empty_dir: true }
    }
    fn pats() -> Vec<String> {
        DEFAULT_PATTERNS.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn closure_classification() {
        let p = pats();
        assert!(is_closure("app.exe", &p), "根目录直系文件是闭包");
        assert!(is_closure("flutter_windows.dll", &p));
        assert!(is_closure("data\\app.so", &p), "data/*.so 是闭包");
        assert!(is_closure("data\\icudtl.dat", &p));
        assert!(!is_closure("data\\flutter_assets\\a.png", &p), "惰性资源不是闭包");
        // 注意：PowerShell 的 `-like` 里 `*` **跨分隔符**，因此 data/*.so 也命中更深一层。
        // 这不是缺陷：与旧脚本逐条一致才是对的口径（更宽一点的闭包只会更安全）。
        assert!(is_closure("data\\sub\\x.so", &p), "`*` 跨分隔符，与 -like 语义一致");
        assert!(!is_closure("plugins\\p.dll", &p));
        assert!(!is_closure(MANIFEST_NAME, &p), "包元数据永远不是闭包");
    }

    #[test]
    fn order_puts_the_closure_at_the_tail() {
        let items = vec![
            it("JigsawFox.exe"),
            it("data\\flutter_assets\\a.png"),
            dir("plugins"),
            it("flutter_windows.dll"),
            it("data\\app.so"),
            it("updater.manifest"),
        ];
        let o = order(&items, &pats());
        let non: Vec<&str> = o.non_closure.iter().map(|i| i.rel.as_str()).collect();
        assert_eq!(non, vec!["data\\flutter_assets\\a.png", "updater.manifest"]);
        assert_eq!(o.empty_dirs.iter().map(|i| i.rel.as_str()).collect::<Vec<_>>(), vec!["plugins"]);
        let clo: Vec<&str> = o.closure.iter().map(|i| i.rel.as_str()).collect();
        assert_eq!(clo, vec!["JigsawFox.exe", "data\\app.so", "flutter_windows.dll"]);
        // 实际条目序列（去掉目录条目）必须让闭包构成连续尾部
        let seq: Vec<String> = non
            .iter()
            .chain(clo.iter())
            .map(|s| s.to_string())
            .collect();
        let closure: Vec<String> = clo.iter().map(|s| s.to_string()).collect();
        assert!(closure_is_a_contiguous_tail(&seq, &closure).is_ok());
    }

    #[test]
    fn tail_check_catches_mutation() {
        // 变异：把闭包成员混进非闭包区（等价于"改回纯字母序"时的形态）
        let seq = vec!["app.exe".to_string(), "data\\a.png".to_string(), "z.dll".to_string()];
        let closure = vec!["app.exe".to_string(), "z.dll".to_string()];
        let e = closure_is_a_contiguous_tail(&seq, &closure).unwrap_err();
        assert!(e.contains("not a contiguous tail"), "{}", e);
        assert!(e.contains("app.exe"), "失败信息必须列出成员：{}", e);
    }

    #[test]
    fn custom_patterns_replace_the_defaults() {
        let p = vec!["assets/*".to_string()];
        assert!(is_closure("assets/a.png", &p));
        assert!(is_closure("assets/deep/a.png", &p), "`*` 跨分隔符（-like 语义）");
        assert!(!is_closure("data/app.so", &p), "覆盖后默认模式失效");
        assert!(!is_closure("other/a.png", &p));
    }
}
