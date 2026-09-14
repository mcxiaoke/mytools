// Tier: 0
//! Journal：权威集 / 阶段记录 / 建议集；PLANNED 一次性落盘 + fsync；三层恢复统一入口
//! （TRANSACTION §1.2）。行式文本、UTF-8、LF 结尾；版本未知一律拒绝恢复。
#![allow(dead_code)]
use std::io::Write;
use crate::win32::path_guard::{is_under, norm_ci};

pub const JOURNAL_VERSION: &str = "3";

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Stage {
    Planned,
    Applying,
    Committed,
}

impl Stage {
    pub fn as_str(&self) -> &'static str {
        match self {
            Stage::Planned => "PLANNED",
            Stage::Applying => "APPLYING",
            Stage::Committed => "COMMITTED",
        }
    }
    fn parse(s: &str) -> Option<Stage> {
        match s {
            "PLANNED" => Some(Stage::Planned),
            "APPLYING" => Some(Stage::Applying),
            "COMMITTED" => Some(Stage::Committed),
            _ => None,
        }
    }
}

#[derive(Debug, Clone)]
#[allow(clippy::large_enum_variant)]
pub struct Journal {
    pub gen: String,
    pub target: String,
    pub backup: String,
    pub tmpdir: String,
    pub tover: Option<String>,
    pub stage: Stage,
    pub exist: Vec<String>,
    pub new: Vec<String>,
    pub dir: Vec<String>,
    pub moved: Vec<String>,
    pub added: Vec<String>,
}

pub fn journal_path(target: &str) -> String {
    format!("{}\\.updater\\journal", target.trim_end_matches('\\'))
}

/// 全局唯一事务代号 `<yyyymmddThhmmss>-<pid>`。
pub fn gen_new() -> String {
    format!("{}-{}", crate::logger::stamp_compact(), std::process::id())
}

/// 创建 Journal 并写入权威集 + STAGE:PLANNED，fsync 后返回句柄。
/// 铁律 3：先落盘计划，再改动任何文件。
pub struct JournalWriter {
    file: std::fs::File,
    pending_advisory: String,
}

impl JournalWriter {
    pub fn create(j: &Journal) -> Result<JournalWriter, String> {
        let path = journal_path(&j.target);
        let mut f = std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&path)
            .map_err(|e| format!("create journal: {}", e))?;
        let mut head = String::new();
        head.push_str(&format!("JOURNAL:{}\n", JOURNAL_VERSION));
        head.push_str(&format!("GEN:{}\n", j.gen));
        head.push_str(&format!("TARGET:{}\n", j.target));
        head.push_str(&format!("BACKUP:{}\n", j.backup));
        head.push_str(&format!("TMPDIR:{}\n", j.tmpdir));
        if let Some(v) = &j.tover {
            head.push_str(&format!("TOVER:{}\n", v));
        }
        head.push_str(&format!("STAGE:{}\n", Stage::Planned.as_str()));
        for r in &j.exist {
            head.push_str(&format!("EXIST:{}\n", r));
        }
        for r in &j.new {
            head.push_str(&format!("NEW:{}\n", r));
        }
        for r in &j.dir {
            head.push_str(&format!("DIR:{}\n", r));
        }
        f.write_all(head.as_bytes()).map_err(|e| format!("write journal: {}", e))?;
        f.sync_data().map_err(|e| format!("fsync journal: {}", e))?;
        Ok(JournalWriter { file: f, pending_advisory: String::new() })
    }

    /// 建议性记录（MOVED/ADDED/DIRDONE）：追加缓冲，不逐行 fsync，不承载正确性。
    pub fn advisory(&mut self, line: &str) {
        self.pending_advisory.push_str(line);
        self.pending_advisory.push('\n');
    }

    /// 阶段迁移：flush 建议集 + 追加 STAGE 行 + fsync。成功后才执行对应的文件动作。
    pub fn stage(&mut self, s: Stage) -> Result<(), String> {
        let mut buf = core::mem::take(&mut self.pending_advisory);
        buf.push_str(&format!("STAGE:{}\n", s.as_str()));
        self.file.write_all(buf.as_bytes()).map_err(|e| format!("write journal: {}", e))?;
        self.file.sync_data().map_err(|e| format!("fsync journal: {}", e))
    }
}

#[allow(clippy::large_enum_variant)]
pub enum ReadOutcome {
    None,
    Ok(Journal),
    Reject(String),
}

/// 解析 Journal：允许尾行截断；忽略无法识别行；STAGE 取最后一条成功解析的记录。
pub fn read(target: &str) -> ReadOutcome {
    let text = match std::fs::read_to_string(journal_path(target)) {
        Ok(t) => t,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return ReadOutcome::None,
        Err(e) => return ReadOutcome::Reject(format!("journal unreadable: {}", e)),
    };
    let mut gen = None;
    let mut tgt = None;
    let mut backup = None;
    let mut tmpdir = None;
    let mut tover = None;
    let mut stage = None;
    let mut exist = Vec::new();
    let mut new = Vec::new();
    let mut dir = Vec::new();
    let mut moved = Vec::new();
    let mut added = Vec::new();
    for line in text.lines() {
        let (k, v) = match line.split_once(':') {
            Some(x) => x,
            None => continue, // 无法识别行忽略
        };
        match k {
            "JOURNAL" => {
                if v != JOURNAL_VERSION {
                    return ReadOutcome::Reject(format!("unknown journal version: {}", v));
                }
            }
            "GEN" => gen = Some(v.to_string()),
            "TARGET" => tgt = Some(v.to_string()),
            "BACKUP" => backup = Some(v.to_string()),
            "TMPDIR" => tmpdir = Some(v.to_string()),
            "TOVER" => tover = Some(v.to_string()),
            "STAGE" => {
                if let Some(s) = Stage::parse(v) {
                    stage = Some(s);
                }
            }
            "EXIST" => exist.push(v.to_string()),
            "NEW" => new.push(v.to_string()),
            "DIR" => dir.push(v.to_string()),
            "MOVED" => moved.push(v.to_string()),
            "ADDED" => added.push(v.to_string()),
            _ => {}
        }
    }
    let j = Journal {
        gen: match gen {
            Some(g) => g,
            None => return ReadOutcome::Reject("missing GEN".to_string()),
        },
        target: match tgt {
            Some(t) => t,
            None => return ReadOutcome::Reject("missing TARGET".to_string()),
        },
        backup: match backup {
            Some(b) => b,
            None => return ReadOutcome::Reject("missing BACKUP".to_string()),
        },
        tmpdir: match tmpdir {
            Some(t) => t,
            None => return ReadOutcome::Reject("missing TMPDIR".to_string()),
        },
        tover,
        stage: match stage {
            Some(s) => s,
            None => return ReadOutcome::Reject("missing STAGE".to_string()),
        },
        exist,
        new,
        dir,
        moved,
        added,
    };
    ReadOutcome::Ok(j)
}

/// 恢复前的强校验：TARGET 必须与 --target 规范化后一致；BACKUP/TMPDIR 必须在 target 内。
/// 比对前去 `\\?\` 前缀、统一分隔符、大小写不敏感、去尾随分隔符——等价路径不得判为“跨应用”。
pub fn validate(j: &Journal, target: &str) -> Result<(), String> {
    let tt = norm_ci(target);
    if norm_ci(&j.target) != tt {
        return Err(format!("journal target mismatch: journal={} given={}", j.target, target));
    }
    if !is_under(&tt, &norm_ci(&j.backup)) {
        return Err(format!("journal BACKUP outside target: {}", j.backup));
    }
    if !is_under(&tt, &norm_ci(&j.tmpdir)) {
        return Err(format!("journal TMPDIR outside target: {}", j.tmpdir));
    }
    Ok(())
}

pub enum RecoverResult {
    NoJournal,
    /// PLANNED：目录零改动，仅清理。
    Cleaned,
    RolledBack,
    RollbackIncomplete,
    /// COMMITTED：收尾完成（含容忍性 WARNING）。
    Finalized,
}

/// 三层恢复共用入口（L1/L2/L3 天然幂等）：以 Journal + 磁盘实际状态为唯一事实来源。
pub fn recover(target: &str, ttl_days: u32, retr: crate::transaction::RetryParams) -> Result<RecoverResult, String> {
    let j = match read(target) {
        ReadOutcome::None => return Ok(RecoverResult::NoJournal),
        ReadOutcome::Reject(e) => return Err(e),
        ReadOutcome::Ok(j) => j,
    };
    validate(&j, target)?;
    match j.stage {
        Stage::Planned => {
            // 目录零改动：仅清理备份/临时目录与 Journal
            let _ = std::fs::remove_dir_all(&j.backup);
            let _ = std::fs::remove_dir_all(&j.tmpdir);
            let _ = std::fs::remove_file(journal_path(target));
            log::info!("recovery(L3): PLANNED journal cleaned up (gen {})", j.gen);
            Ok(RecoverResult::Cleaned)
        }
        Stage::Applying => {
            log::warn!("recovery(L3): APPLYING journal found (gen {}), rolling back", j.gen);
            match crate::transaction::rollback(target, &j, retr) {
                Ok(()) => Ok(RecoverResult::RolledBack),
                Err(()) => Ok(RecoverResult::RollbackIncomplete),
            }
        }
        Stage::Committed => {
            log::info!("recovery(L3): COMMITTED journal found (gen {}), finalizing", j.gen);
            crate::transaction::finalize_committed(target, &j, ttl_days, retr, true);
            Ok(RecoverResult::Finalized)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = "JOURNAL:3\nGEN:20260914T161230-18422\nTARGET:C:\\App\n\
BACKUP:C:\\App\\.updater\\backup\\20260914T161230-18422\n\
TMPDIR:C:\\App\\.updater\\tmp\\20260914T161230-18422\n\
TOVER:1.5.0\nSTAGE:PLANNED\nEXIST:app.exe\nNEW:new.dll\nDIR:plugins\\newdir\n";

    fn write_tmp(name: &str, text: &str) -> String {
        // read() 在 <dir>\.updater\journal 找 Journal，与真实布局一致
        let dir = std::env::temp_dir().join(format!("updater-journal-test-{}-{}", name, std::process::id()));
        std::fs::create_dir_all(dir.join(".updater")).unwrap();
        std::fs::write(dir.join(".updater").join("journal"), text).unwrap();
        dir.to_string_lossy().into_owned()
    }

    #[test]
    fn parse_ok_and_stages() {
        let dir = write_tmp("j1", SAMPLE);
        assert_eq!(read(&dir).journal_target(), "C:\\App");
        let dir2 = write_tmp("j2", &SAMPLE.replace("STAGE:PLANNED", "STAGE:APPLYING\nMOVED:app.exe\nSTAGE:COMMITTED\n"));
        match read(&dir2) {
            ReadOutcome::Ok(j) => {
                assert_eq!(j.stage, Stage::Committed); // STAGE 取最后一条
                assert_eq!(j.moved, vec!["app.exe".to_string()]);
            }
            _ => panic!("expect ok"),
        }
    }

    #[test]
    fn parse_unknown_version_rejected() {
        let dir = write_tmp("j3", &SAMPLE.replace("JOURNAL:3", "JOURNAL:99"));
        assert!(matches!(read(&dir), ReadOutcome::Reject(_)));
    }

    #[test]
    fn parse_truncated_tail_tolerated() {
        let dir = write_tmp("j4", "JOURNAL:3\nGEN:g1\nTARGET:C:\\App\nBACKUP:C:\\App\\.updater\\backup\\g1\nTMPDIR:C:\\App\\.updater\\tmp\\g1\nSTAGE:PLANNED\nEXIST:app.ex");
        match read(&dir) {
            ReadOutcome::Ok(j) => assert_eq!(j.stage, Stage::Planned),
            _ => panic!("truncated tail must be tolerated"),
        }
    }

    #[test]
    fn target_mismatch_rejected_but_case_equal_ok() {
        let dir = write_tmp("j5", SAMPLE);
        let j = match read(&dir) {
            ReadOutcome::Ok(j) => j,
            _ => panic!(),
        };
        assert!(validate(&j, "c:\\app\\").is_ok()); // 大小写/尾随分隔符等价
        assert!(validate(&j, "C:\\App2").is_err());
        assert!(validate(&j, "D:\\App").is_err());
    }

    impl ReadOutcome {
        fn journal_target(self) -> String {
            match self {
                ReadOutcome::Ok(j) => j.target,
                _ => panic!("expect journal"),
            }
        }
    }
}
