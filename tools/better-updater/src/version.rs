// Tier: 1
//! 版本号解析与逐段比较（DESIGN §5.3）：拒绝式解析，不做“尽力猜测”。
//! 版本比较是降级防护的判定依据，宽容解析等于给“重放旧包”留后门。

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Version {
    nums: [u64; 3],
    pre: Option<Vec<PreSeg>>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum PreSeg {
    Num(u64),
    Alpha(String),
}

impl Version {
    /// 仅接受 `主.次.补` 或 `主.次.补-预发布`；不支持 +build 与缺补段形式。
    pub fn parse(s: &str) -> Result<Version, String> {
        if s.contains('+') {
            return Err(format!("build metadata not supported: {}", s));
        }
        let (core_s, pre_raw) = match s.split_once('-') {
            Some((c, p)) => (c, Some(p)),
            None => (s, None),
        };
        let parts: Vec<&str> = core_s.split('.').collect();
        if parts.len() != 3 {
            return Err(format!("expect exactly 3 numeric segments: {}", s));
        }
        let mut nums = [0u64; 3];
        for (i, p) in parts.iter().enumerate() {
            if p.is_empty() || !p.bytes().all(|b| b.is_ascii_digit()) {
                return Err(format!("invalid numeric segment: {}", s));
            }
            nums[i] = p.parse::<u64>().map_err(|_| format!("numeric overflow: {}", s))?;
        }
        let pre = match pre_raw {
            None => None,
            Some(p) => {
                if p.is_empty() {
                    return Err(format!("empty prerelease suffix: {}", s));
                }
                let mut segs = Vec::new();
                for seg in p.split('.') {
                    if seg.is_empty() {
                        return Err(format!("empty prerelease segment: {}", s));
                    }
                    if seg.bytes().all(|b| b.is_ascii_digit()) {
                        segs.push(PreSeg::Num(seg.parse::<u64>().map_err(|_| format!("numeric overflow: {}", s))?));
                    } else {
                        segs.push(PreSeg::Alpha(seg.to_string()));
                    }
                }
                Some(segs)
            }
        };
        Ok(Version { nums, pre })
    }
}

fn cmp_pre(a: &[PreSeg], b: &[PreSeg]) -> core::cmp::Ordering {
    use core::cmp::Ordering::*;
    for (x, y) in a.iter().zip(b.iter()) {
        let c = match (x, y) {
            (PreSeg::Num(p), PreSeg::Num(q)) => p.cmp(q),
            (PreSeg::Num(_), PreSeg::Alpha(_)) => Less,
            (PreSeg::Alpha(_), PreSeg::Num(_)) => Greater,
            (PreSeg::Alpha(p), PreSeg::Alpha(q)) => p.as_bytes().cmp(q.as_bytes()),
        };
        if c != Equal {
            return c;
        }
    }
    a.len().cmp(&b.len())
}

impl Ord for Version {
    fn cmp(&self, other: &Self) -> core::cmp::Ordering {
        use core::cmp::Ordering::*;
        let c = self.nums.cmp(&other.nums);
        if c != Equal {
            return c;
        }
        match (&self.pre, &other.pre) {
            (None, None) => Equal,
            (Some(_), None) => Less,
            (None, Some(_)) => Greater,
            (Some(a), Some(b)) => cmp_pre(a, b),
        }
    }
}

impl PartialOrd for Version {
    fn partial_cmp(&self, other: &Self) -> Option<core::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn v(s: &str) -> Version {
        Version::parse(s).unwrap()
    }

    #[test]
    fn numeric_compare() {
        assert!(v("1.10.0") > v("1.9.0"));
        assert!(v("2.0.0") > v("1.999.999"));
        assert_eq!(v("1.2.3"), v("1.2.3"));
    }

    #[test]
    fn prerelease_ordering() {
        assert!(v("1.5.0-beta.1") < v("1.5.0"));
        assert!(v("1.5.0-alpha") < v("1.5.0-beta"));
        assert!(v("1.5.0-beta.1") < v("1.5.0-beta.2"));
        assert!(v("1.5.0-beta") < v("1.5.0-beta.1")); // 前缀相同，段数少者小
        assert!(v("1.5.0-1") < v("1.5.0-alpha")); // 数字段 < 字母段
    }

    #[test]
    fn rejected_forms() {
        assert!(Version::parse("1.2").is_err()); // 缺补段
        assert!(Version::parse("1.2.3.4").is_err());
        assert!(Version::parse("1.2.3+build").is_err());
        assert!(Version::parse("1.2.x").is_err());
        assert!(Version::parse("1..3").is_err());
        assert!(Version::parse("1.2.3-").is_err());
        assert!(Version::parse("").is_err());
    }
}
