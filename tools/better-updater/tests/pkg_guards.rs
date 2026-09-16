//! 包解析与路径安全的加固用例（`TESTING.md` §1 C 组中"夹具可做"的部分）。
//!
//! 全部走**真实 updater 进程 + 真实 zip 字节**：不 mock 解析器，不做内部调用。
//! 断言口径统一为两条：**拒绝必须退出 2（预检层）且目录零改动**。
//!
//! 说明：`zipbomb_streamed`（声明值正常但实际解压超限）**未纳入**——理由见文件末尾注释。

#[path = "common/mod.rs"]
mod common;
use common::*;

use std::fs;
use std::time::Duration;

// ---------------------------------------------------------------------------
// zip 中央目录定点改写（构造"谎报头"包的唯一手段）
// ---------------------------------------------------------------------------

/// 定位中央目录中指定条目的记录起始偏移。
fn cd_record_offset(bytes: &[u8], want: &str) -> usize {
    let eocd = bytes
        .windows(4)
        .rposition(|w| w == b"PK\x05\x06")
        .expect("EOCD record not found");
    let cd_off = u32::from_le_bytes([bytes[eocd + 16], bytes[eocd + 17], bytes[eocd + 18], bytes[eocd + 19]]) as usize;
    let mut p = cd_off;
    loop {
        assert_eq!(&bytes[p..p + 4], b"PK\x01\x02", "expected a central directory record");
        let nlen = u16::from_le_bytes([bytes[p + 28], bytes[p + 29]]) as usize;
        let elen = u16::from_le_bytes([bytes[p + 30], bytes[p + 31]]) as usize;
        let clen = u16::from_le_bytes([bytes[p + 32], bytes[p + 33]]) as usize;
        let name = String::from_utf8_lossy(&bytes[p + 46..p + 46 + nlen]).into_owned();
        if name == want {
            return p;
        }
        p += 46 + nlen + elen + clen;
    }
}

/// 改写中央目录条目的 u32 字段。字段偏移：10=压缩方法(u16)，20=压缩大小，24=解压大小。
fn patch_cd_u32(zip: &std::path::Path, name: &str, field_off: usize, val: u32) {
    let mut b = fs::read(zip).unwrap();
    let p = cd_record_offset(&b, name);
    b[p + field_off..p + field_off + 4].copy_from_slice(&val.to_le_bytes());
    fs::write(zip, b).unwrap();
}

fn patch_cd_u16(zip: &std::path::Path, name: &str, field_off: usize, val: u16) {
    let mut b = fs::read(zip).unwrap();
    let p = cd_record_offset(&b, name);
    b[p + field_off..p + field_off + 2].copy_from_slice(&val.to_le_bytes());
    fs::write(zip, b).unwrap();
}

/// 拒绝类用例的公共断言：退出 2 + 目录零改动 + 无 Journal。
fn assert_rejected_zero_touch(target: &std::path::Path, zip: &std::path::Path, log: &std::path::Path, why: &str) {
    let code = run_updater_log(target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"], log);
    assert_eq!(code, 2, "{} must be rejected at precheck (exit 2)", why);
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    assert!(!target.join(".updater\\journal").exists(), "{}: no journal may be created", why);
}

// ---------------------------------------------------------------------------
// 用例
// ---------------------------------------------------------------------------

/// `duplicate_entry_reject`：大小写不敏感重复条目必须在**预检层**拒绝。
/// （Windows 上两个条目会争抢同一路径，先写后覆盖 = 静默的"最后一个赢"，不可接受）
#[test]
fn duplicate_entry_reject() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("dup");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = tmp.path().join("dup.zip");
    make_zip_ordered(&[("a.txt", b"first"), ("A.TXT", b"second")], &zip);
    let log = tmp.path().join("u.log");
    assert_rejected_zero_touch(&target, &zip, &log, "case-insensitive duplicate entry");
    assert!(read(&log).contains("duplicate"), "log must name the duplicate reason:\n{}", read(&log));
}

/// `zip_method_reject`：非 Store/Deflate 的压缩方法必须在预检层拒绝（不尝试解压）。
#[test]
fn zip_method_reject() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("method");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = tmp.path().join("bzip2.zip");
    make_zip_ordered(&[("a.txt", b"payload")], &zip);
    patch_cd_u16(&zip, "a.txt", 10, 12); // 12 = bzip2
    let log = tmp.path().join("u.log");
    assert_rejected_zero_touch(&target, &zip, &log, "bzip2 entry");
    assert!(
        read(&log).contains("unsupported compression method"),
        "log must name the method reason:\n{}",
        read(&log)
    );
}

/// `zipbomb_ratio`：声明解压/压缩比 > 2000:1 必须在预检层拒绝。
/// 阈值刻意抬到 Deflate 物理上限（约 1032:1）之上——所以这个现场**必须靠改写中央目录**构造，
/// 真 Deflate 产出不了 >2000 的比值。
#[test]
fn zipbomb_ratio() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("ratio");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = tmp.path().join("ratio.zip");
    make_zip_ordered(&[("blob.bin", b"0123456789")], &zip);
    patch_cd_u32(&zip, "blob.bin", 24, 512 * 1024 * 1024); // 声明 512 MiB，实际 10 B
    let log = tmp.path().join("u.log");
    assert_rejected_zero_touch(&target, &zip, &log, "declared ratio > 2000:1");
    assert!(
        read(&log).contains("zip bomb suspicion"),
        "log must record the ratio rejection:\n{}",
        read(&log)
    );
}

/// `zipbomb_declared`：声明总量超 `--max-uncompressed` 必须在预检层拒绝。
#[test]
fn zipbomb_declared() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("declared");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = tmp.path().join("big.zip");
    let big = vec![0u8; 2 * 1024 * 1024]; // 2 MiB：比值约 1000:1，不触发比值规则
    make_zip_ordered(&[("big.bin", &big)], &zip);
    let log = tmp.path().join("u.log");

    let code = run_updater_log(
        &target,
        &["--zip", zip.to_str().unwrap(), "--launch", "app.exe", "--max-uncompressed", "1"],
        &log,
    );
    assert_eq!(code, 2, "declared total above --max-uncompressed must be rejected (exit 2)");
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    assert!(!target.join(".updater\\journal").exists());
    assert!(
        read(&log).contains("exceeds --max-uncompressed"),
        "log must record the cap rejection:\n{}",
        read(&log)
    );
}

/// `zipbomb_legit_high_ratio`：**合法**高比值包必须安装成功。
/// 这是比值规则的反向守卫——阈值低于 Deflate 物理上限就会误杀含大段重复字节的正常包
/// （全零数据、未压缩位图、填充区都会命中）。
#[test]
fn zipbomb_legit_high_ratio() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("legitratio");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = tmp.path().join("zeros.zip");
    let zeros = vec![0u8; 4 * 1024 * 1024]; // 4 MiB 全零：真实比值接近 Deflate 上限
    make_zip_ordered(&[("app.exe", &new_app_bytes()), ("zeros.bin", &zeros)], &zip);
    let log = tmp.path().join("u.log");

    let code = run_updater_log(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"], &log);
    assert_eq!(code, 0, "a legitimate high-ratio package must install, not be rejected:\n{}", read(&log));
    let got = fs::read(target.join("zeros.bin")).unwrap();
    assert_eq!(got.len(), zeros.len(), "payload must be installed byte-for-byte");
    assert!(got.iter().all(|b| *b == 0));
}

/// `join_escape_blocked`：包内条目经 Junction 指向 target 之外必须被识破并拒绝。
/// 字符串层的 Zip Slip 检查看不出这种逃逸——只有物理路径解析（`GetFinalPathNameByHandleW`）能。
#[test]
fn join_escape_blocked() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("junction");
    let target = tmp.path().join("target");
    make_target(&target);
    let outside = tmp.path().join("outside");
    fs::create_dir_all(&outside).unwrap();
    write(&outside.join("evil.txt"), "outside payload");

    let link = target.join("link");
    let ok = std::process::Command::new("cmd")
        .args(["/c", "mklink", "/J", link.to_str().unwrap(), outside.to_str().unwrap()])
        .output()
        .map(|o| o.status.success())
        .unwrap_or(false);
    if !ok || !link.exists() {
        // 环境不支持 junction（非 NTFS / 策略禁用）：明确失败而非静默跳过
        panic!("cannot create a junction for this test; run on NTFS with mklink available");
    }

    let zip = tmp.path().join("escape.zip");
    make_zip_ordered(&[("link\\evil.txt", b"tampered")], &zip);
    let log = tmp.path().join("u.log");
    assert_rejected_zero_touch(&target, &zip, &log, "junction escape");
    assert!(
        read(&log).contains("physical path escape"),
        "log must record the escape detection:\n{}",
        read(&log)
    );
    assert_eq!(read(&outside.join("evil.txt")), "outside payload", "outside file must be untouched");

    let _ = fs::remove_dir(&link); // 先摘掉 junction 再让 TempDir 清理
}

/// `long_path`：target 与包内条目路径合计 > 260 字符时仍能完整更新。
///
/// ⚠️ 本机 `LongPathsEnabled = 1`，Win32 对超长路径本就放行，因此本用例
/// **证明的是"verbatim 前缀改造没有回归"**，而不是"没有 verbatim 前缀就会失败"。
/// 要隔离验证 verbatim 机制本身，需要在 `LongPathsEnabled = 0` 的机器上重跑。
#[test]
fn long_path() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("longpath");
    let mut target = tmp.path().join("t");
    while target.to_string_lossy().len() < 230 {
        target = target.join("dddddddddddddddddddd");
    }
    fs::create_dir_all(&target).unwrap();
    make_target(&target);

    let stage = tmp.path().join("stage");
    make_stage(&stage);
    let zip = tmp.path().join("lp.zip");
    make_zip(&stage, &zip, None);

    let deepest = target.join("deep").join("e".repeat(60));
    assert!(deepest.to_string_lossy().len() > 260, "test premise: destination path must exceed MAX_PATH");

    let log = tmp.path().join("u.log");
    let code = run_updater_log(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"], &log);
    assert_eq!(code, 0, "long-path update must succeed. log:\n{}", read(&log));
    assert_file_is(&target.join("app.exe"), &new_app_bytes());
}

/// `zip_handle_pinned`：事务窗口期内 zip 被独占读锁定，任何进程都无法改写或替换它。
/// 这是"验签对象 = 解压对象"（I8）在**外部可观察**层面的证据。
#[test]
fn zip_handle_pinned() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("pinned");
    let target = tmp.path().join("target");
    make_target(&target);
    write(&target.join("zzz_hold.dll"), "old dll bytes");
    let hold = hold_deny_delete(&target.join("zzz_hold.dll"));

    let zip = tmp.path().join("update.zip");
    make_zip_ordered(
        &[
            ("app.exe", &new_app_bytes()),
            ("zzz_hold.dll", b"new dll bytes"),
        ],
        &zip,
    );
    let log = tmp.path().join("u.log");
    let mut child = spawn_updater(
        &target,
        &[
            "--zip",
            zip.to_str().unwrap(),
            "--launch",
            "app.exe",
            "--write-retries",
            "100",
            "--write-delay-ms",
            "200",
        ],
        Some(&log),
    );
    let pid = child.id();

    let in_window = wait_for(|| !backup_files(&target).is_empty(), Duration::from_secs(30));
    assert!(in_window, "transaction must reach the apply window");

    // 事务进行中：zip 已被 FILE_SHARE_READ 独占读锁定
    assert!(
        fs::remove_file(&zip).is_err(),
        "zip must not be deletable while the transaction holds it"
    );
    assert!(
        fs::rename(&zip, tmp.path().join("moved.zip")).is_err(),
        "zip must not be renameable (replaceable) while the transaction holds it"
    );
    assert!(
        fs::OpenOptions::new().write(true).open(&zip).is_err(),
        "zip must not be writable (no in-place tamper after verification)"
    );
    println!("[evidence] zip pinned during transaction: delete/rename/write all refused");

    kill_pid(pid);
    let _ = child.wait();
    assert!(wait_journal_gone(&target, Duration::from_secs(60)), "watchdog must converge");
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    drop(hold);
}

/// 包内空目录必须被真正创建（zip 目录条目 + 清单 `DIR:` 行两条来源）。
///
/// 这是回归用例：`plan.dirs` 曾经只被打印与写入 Journal，**从未在磁盘上创建**——
/// 有文件的目录会被 `apply_file` 的 MkdirAll 顺带建出来，所以问题只对**空目录**显形
/// （真实应用里 `logs\`、`plugins\` 这类占位目录就会静默丢失）。
#[test]
fn package_empty_dir_created() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("emptydir");
    let target = tmp.path().join("target");
    make_target(&target);
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    fs::create_dir_all(stage.join("emptydir")).unwrap();
    fs::create_dir_all(stage.join("nested\\deeper")).unwrap();
    let manifest = manifest_for(&stage, "1.0.1");
    let zip = tmp.path().join("e.zip");
    make_zip(&stage, &zip, Some(&manifest));

    let log = tmp.path().join("u.log");
    let code = run_updater_log(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"], &log);
    assert_eq!(code, 0, "update must succeed:\n{}", read(&log));
    assert!(target.join("emptydir").is_dir(), "empty dir declared in package must be created");
    assert!(target.join("nested\\deeper").is_dir(), "nested empty dir must be created (parents-first)");
    println!("[evidence] empty dirs created: emptydir, nested\\deeper");
}

// ---------------------------------------------------------------------------
// 未纳入的矩阵条目及理由
// ---------------------------------------------------------------------------
//
// `zipbomb_streamed`（声明值正常但实际解压超限）：
//   当前依赖 `zip` 2.x 的读取侧**强制按中央目录声明的大小截断**——解压流不会超过声明值，
//   因此"实际 > 声明"在当前依赖下不可达（构造路径只剩"声明 > 实际"，那走的是
//   `SizeMismatch` 而非 `CapExceeded`）。流式计数（`ApplyCtx.max_total`）保留为
//   纵深防御的第二层，但在依赖语义下它是**冗余层**，无法构造出只命中它、不命中预检层的现场。
//
// `fs_unsupported_default_continue` / `fs_unsupported_strict_reject`：
//   需要一个不支持 `GetFinalPathNameByHandleW` 的文件系统（网络盘）。本机无此环境。
//
// `rm_diagnose_names_holder`：
//   需要"真实独占句柄持有者 + 重试耗尽"才能触发 RM 诊断输出，且要断言日志中出现持有者进程名。
