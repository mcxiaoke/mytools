use std::path::Path;

#[derive(Debug, Clone)]
pub enum Rule {
    Directory(String), // normalized to lower case, e.g. "config/"
    Glob(String),      // e.g. "*.log"
    Exact(String),     // e.g. "settings.json"
}

pub struct KeepManager {
    rules: Vec<Rule>,
    self_rel_path: Option<String>,
    keep_file_name: String,
}

impl KeepManager {
    pub fn new(target: &Path, keep_file_name: &str, cli_keeps: &[String]) -> Self {
        let mut rules = Vec::new();

        // 1. Read keep file if it exists in target
        let keep_file_path = target.join(keep_file_name);
        if let Ok(content) = std::fs::read_to_string(&keep_file_path) {
            for line in content.lines() {
                let trimmed = line.trim();
                if trimmed.is_empty() || trimmed.starts_with('#') {
                    continue;
                }
                rules.push(parse_rule(trimmed));
            }
        }

        // 2. Add CLI keep rules
        for k in cli_keeps {
            rules.push(parse_rule(k.trim()));
        }

        // 3. Determine if current running updater is inside target
        let self_rel_path = detect_self_rel_path(target);

        KeepManager {
            rules,
            self_rel_path,
            keep_file_name: keep_file_name.to_ascii_lowercase(),
        }
    }

    pub fn is_kept(&self, rel_path: &str) -> (bool, &'static str) {
        let normalized = rel_path.replace('\\', "/").trim_start_matches('/').to_string();
        let lower = normalized.to_ascii_lowercase();

        // 1. Internal working files / directories: never touch
        if lower.starts_with(".updater_tmp")
            || lower.starts_with(".updater_bak")
            || lower == ".updater.lock"
            || lower.starts_with(".updater/")
            || lower == ".updater"
        {
            return (true, "internal updater state");
        }

        // 2. Keep file itself: never overwrite
        if lower == self.keep_file_name || lower.ends_with(&format!("/{}", self.keep_file_name)) {
            return (true, "keep file itself");
        }

        // 3. Self protection: running updater binary inside target
        if let Some(ref self_rel) = self.self_rel_path {
            if &lower == self_rel {
                return (true, "running updater self-protection");
            }
        }

        // 4. Check user rules (.updatekeep + CLI)
        for rule in &self.rules {
            match rule {
                Rule::Directory(dir) => {
                    if lower.starts_with(dir) || format!("{}/", lower).starts_with(dir) {
                        return (true, "rule match (directory)");
                    }
                }
                Rule::Exact(exact) => {
                    if &lower == exact || lower.starts_with(&format!("{}/", exact)) {
                        return (true, "rule match (exact)");
                    }
                }
                Rule::Glob(pattern) => {
                    // Match full relative path, filename, or any path segment
                    if glob_match(pattern, &lower) {
                        return (true, "rule match (glob)");
                    }
                    if let Some(filename) = lower.rsplit('/').next() {
                        if glob_match(pattern, filename) {
                            return (true, "rule match (glob filename)");
                        }
                    }
                    for segment in lower.split('/') {
                        if glob_match(pattern, segment) {
                            return (true, "rule match (glob segment)");
                        }
                    }
                }
            }
        }

        (false, "")
    }

    pub fn self_rel_path(&self) -> Option<&str> {
        self.self_rel_path.as_deref()
    }
}

fn parse_rule(s: &str) -> Rule {
    let normalized = s.replace('\\', "/").trim_start_matches('/').to_string();
    let lower = normalized.to_ascii_lowercase();

    if lower.ends_with('/') {
        Rule::Directory(lower)
    } else if lower.contains('*') || lower.contains('?') || lower.contains('[') {
        Rule::Glob(lower)
    } else {
        Rule::Exact(lower)
    }
}

fn detect_self_rel_path(target: &Path) -> Option<String> {
    let self_exe = std::env::current_exe().ok()?;
    let self_canonical = std::fs::canonicalize(&self_exe).unwrap_or(self_exe);
    let target_canonical = std::fs::canonicalize(target).unwrap_or_else(|_| target.to_path_buf());

    let self_lower = self_canonical.to_string_lossy().replace('\\', "/").to_ascii_lowercase();
    let target_lower = target_canonical.to_string_lossy().replace('\\', "/").to_ascii_lowercase();

    let target_prefix = if target_lower.ends_with('/') {
        target_lower
    } else {
        format!("{}/", target_lower)
    };

    if self_lower.starts_with(&target_prefix) {
        let rel = &self_lower[target_prefix.len()..];
        Some(rel.trim_start_matches('/').to_string())
    } else {
        None
    }
}

/// Simple wildcard matcher supporting * and ?
pub fn glob_match(pattern: &str, text: &str) -> bool {
    let p_bytes = pattern.as_bytes();
    let t_bytes = text.as_bytes();

    let mut px = 0;
    let mut tx = 0;
    let mut next_px = 0;
    let mut next_tx = 0;

    while px < p_bytes.len() || tx < t_bytes.len() {
        if px < p_bytes.len() {
            let c = p_bytes[px];
            match c {
                b'*' => {
                    next_px = px + 1;
                    next_tx = tx + 1;
                    px += 1;
                    continue;
                }
                b'?' => {
                    if tx < t_bytes.len() {
                        px += 1;
                        tx += 1;
                        continue;
                    }
                }
                _ => {
                    if tx < t_bytes.len() && t_bytes[tx] == c {
                        px += 1;
                        tx += 1;
                        continue;
                    }
                }
            }
        }

        if 0 < next_tx && next_tx <= t_bytes.len() {
            px = next_px;
            tx = next_tx;
            next_tx += 1;
            continue;
        }

        return false;
    }

    true
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_glob_match() {
        assert!(glob_match("*.log", "app.log"));
        assert!(glob_match("*.log", "logs/app.log"));
        assert!(glob_match("test?.txt", "test1.txt"));
        assert!(!glob_match("test?.txt", "test12.txt"));
        assert!(glob_match("config/*", "config/settings.json"));
    }
}
