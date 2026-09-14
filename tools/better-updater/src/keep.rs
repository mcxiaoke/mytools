// Tier: 0
//! .updatekeep 三类规则 + 自研 glob 引擎 + 内部保留名拦截（DESIGN §7.1/§7.2，I9）。
//! 拦截规则两条（均大小写不敏感）：① 精确等于 `.updater`；② 以 `.updater\` 为前缀。
#![allow(dead_code)]

pub struct KeepRules {
    rules: Vec<Rule>,
}

enum Rule {
    /// 以 / 或 \ 结尾：保护整棵子树。
    Dir(String),
    /// 含 *?[：匹配完整相对路径、basename 或任一上级目录。
    Glob(String),
    /// 精确匹配该文件，或作为目录前缀（故意不做 basename 匹配）。
    Literal(String),
}

impl KeepRules {
    pub fn empty() -> KeepRules {
        KeepRules { rules: Vec::new() }
    }

    /// 解析 .updatekeep 文本 + --keep 追加规则。`#` 注释与空行忽略，大小写不敏感。
    pub fn parse(text: &str, extra: &[String]) -> KeepRules {
        let mut rules = Vec::new();
        let mut push = |raw: &str| {
            let l = raw.trim();
            if l.is_empty() || l.starts_with('#') {
                return;
            }
            let norm = l.replace('/', "\\").to_lowercase();
            let norm = norm.trim_end_matches('\\').to_string();
            if norm.is_empty() {
                return;
            }
            if raw.trim_end().ends_with('/') || raw.trim_end().ends_with('\\') {
                rules.push(Rule::Dir(norm));
            } else if norm.contains('*') || norm.contains('?') || norm.contains('[') {
                rules.push(Rule::Glob(norm));
            } else {
                rules.push(Rule::Literal(norm));
            }
        };
        for line in text.lines() {
            push(line);
        }
        for e in extra {
            push(e);
        }
        KeepRules { rules }
    }

    /// 命中即既不覆盖也不删除。
    pub fn is_protected(&self, rel_norm: &str) -> bool {
        let rel = rel_norm.to_lowercase();
        for r in &self.rules {
            let matched = match r {
                Rule::Dir(p) | Rule::Literal(p) => rel == *p || rel.starts_with(&format!("{}\\", p)),
                Rule::Glob(g) => {
                    let pat: Vec<char> = g.chars().collect();
                    let base = rel.rsplit('\\').next().unwrap_or("");
                    let mut hit = glob_match(&pat, &rel.chars().collect::<Vec<char>>())
                        || glob_match(&pat, &base.chars().collect::<Vec<char>>());
                    if !hit {
                        // 任一上级目录
                        let mut cur = rel.clone();
                        while let Some(pos) = cur.rfind('\\') {
                            cur = cur[..pos].to_string();
                            if glob_match(&pat, &cur.chars().collect::<Vec<char>>()) {
                                hit = true;
                                break;
                            }
                        }
                    }
                    hit
                }
            };
            if matched {
                return true;
            }
        }
        false
    }
}

/// 内部保留名（唯一落盘保留名，DESIGN §7.1 优先级 1）。planner 与 apply 两处调用（I9）。
pub fn is_reserved(rel: &str) -> bool {
    let r = rel.to_lowercase();
    r == ".updater" || r.starts_with(".updater\\")
}

fn lower_chars(s: &[char]) -> Vec<char> {
    s.iter().map(|c| c.to_ascii_lowercase()).collect()
}

/// 支持通配符的匹配引擎：`*` 任意序列、`?` 单字符、`[...]` 字符类（含 `!`/`^` 否定与 a-z 区间）。
pub fn glob_match(pat: &[char], name: &[char]) -> bool {
    let pat = lower_chars(pat);
    let name = lower_chars(name);
    gm(&pat, &name)
}

fn gm(pat: &[char], name: &[char]) -> bool {
    if pat.is_empty() {
        return name.is_empty();
    }
    match pat[0] {
        '*' => {
            for i in 0..=name.len() {
                if gm(&pat[1..], &name[i..]) {
                    return true;
                }
            }
            false
        }
        '?' => !name.is_empty() && gm(&pat[1..], &name[1..]),
        '[' => {
            let close = match pat.iter().skip(1).position(|&c| c == ']') {
                Some(p) => p + 1,
                None => return name[0] == pat[0] && gm(&pat[1..], &name[1..]), // 无闭括号按字面
            };
            if name.is_empty() {
                return false;
            }
            let mut cls = &pat[1..close];
            let neg = !cls.is_empty() && (cls[0] == '!' || cls[0] == '^');
            if neg {
                cls = &cls[1..];
            }
            let c = name[0];
            let mut m = false;
            let mut i = 0usize;
            while i < cls.len() {
                if i + 2 < cls.len() && cls[i + 1] == '-' {
                    if c >= cls[i] && c <= cls[i + 2] {
                        m = true;
                    }
                    i += 3;
                } else {
                    if c == cls[i] {
                        m = true;
                    }
                    i += 1;
                }
            }
            if neg {
                m = !m;
            }
            m && gm(&pat[close + 1..], &name[1..])
        }
        c => !name.is_empty() && name[0] == c && gm(&pat[1..], &name[1..]),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rules(text: &str) -> KeepRules {
        KeepRules::parse(text, &[])
    }

    #[test]
    fn dir_trailing_slash() {
        let r = rules("userdata\\\nconfig/\n");
        assert!(r.is_protected("userdata\\a.txt"));
        assert!(r.is_protected("userdata\\sub\\b.txt"));
        assert!(r.is_protected("config\\x.json"));
        assert!(!r.is_protected("config.ini")); // 目录规则不误伤同名前缀文件
        assert!(!r.is_protected("userdata2\\x"));
    }

    #[test]
    fn literal_file_or_prefix() {
        let r = rules("config\nsettings.json\n");
        assert!(r.is_protected("config\\x.json")); // 目录前缀
        assert!(r.is_protected("settings.json"));
        assert!(!r.is_protected("config.ini")); // 故意不做 basename 匹配
    }

    #[test]
    fn glob_rules() {
        let r = rules("*.log\nlogs/*.tmp\ndata/[ab].bin\n");
        assert!(r.is_protected("app.log"));
        assert!(r.is_protected("sub\\dir\\crash.log")); // 匹配 basename
        assert!(r.is_protected("logs\\a.tmp"));
        assert!(r.is_protected("data\\a.bin"));
        assert!(r.is_protected("data\\b.bin"));
        assert!(!r.is_protected("data\\c.bin"));
        assert!(!r.is_protected("logs\\a.txt"));
    }

    #[test]
    fn comments_and_case() {
        let r = rules("# comment\n\nUserdata\\\n");
        assert!(r.is_protected("USERDATA\\x"));
    }

    #[test]
    fn reserved_names() {
        assert!(is_reserved(".updater"));
        assert!(is_reserved(".updater\\journal"));
        assert!(is_reserved(".UPDATER\\state"));
        assert!(is_reserved(".updater\\backup\\g\\x.dll"));
        assert!(!is_reserved("updater.manifest"));
        assert!(!is_reserved(".updaterx\\a"));
        assert!(!is_reserved("app\\updater.exe"));
    }
}
