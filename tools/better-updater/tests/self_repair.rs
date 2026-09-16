//! 自身修复入口专项（`updater.exe` 无参数 / `--recover` 缺 `--target`）。
//!
//! 存在的理由：`SCOPE.md` §1 只承诺 L1 + L2。若应用**已无法启动**（半新半旧导致
//! loader 阶段就失败），调用方的启动自检代码根本跑不到，L3 无从触发。
//! 因此必须有一个**不依赖宿主**的人工入口——而人能做的动作只有"双击"。
//! 本文件验证的正是这个动作：把 `updater.exe` 放进安装目录，什么都不传，它收敛这个目录。
//!
//! 与 `crash_windows.rs` 的区别：那里验证"看门狗/冷启动能否收敛"，
//! 这里验证"**人**能否触发收敛"，以及触发失败时的可观测性。

#[path = "common/mod.rs"]
mod common;
use common::*;

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, Instant};

/// 把本项目的 updater 复制进一个独立目录，模拟"updater.exe 随应用放进安装目录"的部署形态。
/// 返回 (目录, 该目录内的 updater 路径)。
fn install_dir(tag: &str) -> (TempDir, PathBuf) {
    let tmp = TempDir::new(tag);
    let dir = tmp.path().join("app");
    fs::create_dir_all(&dir).unwrap();
    let dst = dir.join("updater.exe");
    fs::copy(exe_path(), &dst).unwrap();
    (tmp, dst)
}

/// 在该目录内运行它，`args` 里**不含** `--target`（这正是被测语义）。
///
/// **带硬超时，且超时即失败。** 这不是洁癖：失败提示框是**模态**的，一旦 `main` 里的
/// 门条件写松了，`cargo test` 会静默挂住、直到有人手动去点掉它——本项目真的发生过一次
/// （套件从 4.6s 变成 27.9s，靠人工点掉才结束）。把"挂起"变成"断言失败"，
/// 是为了让下一个人一眼看到原因，而不是对着一动不动的终端猜。
fn run_in(dir: &Path, args: &[&str]) -> (i32, String) {
    let _g = GlobalLock::acquire();
    let mut child = Command::new(dir.join("updater.exe"))
        .args(args)
        .current_dir(dir)
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped())
        .spawn()
        .expect("failed to spawn updater");

    let deadline = Instant::now() + Duration::from_secs(30);
    while Instant::now() < deadline {
        if child.try_wait().expect("try_wait").is_some() {
            // 进程已退出 ⇒ 管道里只剩缓冲数据，读它不会阻塞
            let out = child.wait_with_output().expect("wait_with_output");
            let mut text = String::from_utf8_lossy(&out.stdout).into_owned();
            text.push_str(&String::from_utf8_lossy(&out.stderr));
            return (out.status.code().unwrap_or(-1), text);
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    let _ = child.kill();
    let _ = child.wait();
    panic!(
        "updater did not exit within 30s (args={:?}). Almost certainly a modal dialog is blocking it: \
         check the gate conditions in main() before repair_failed_alert().",
        args
    );
}

/// 构造一个 APPLYING 阶段被中断的现场：
/// `app.exe` 已被替换为新内容、`backup\<GEN>\app.exe` 是旧内容、`new.dll` 是本次新增的文件。
fn make_interrupted(dir: &Path, gen: &str) {
    let up = dir.join(".updater");
    fs::create_dir_all(up.join("backup").join(gen)).unwrap();
    fs::create_dir_all(up.join("tmp").join(gen)).unwrap();
    write_bin(&dir.join("app.exe"), b"NEW-CONTENT");
    write_bin(&up.join("backup").join(gen).join("app.exe"), b"OLD-CONTENT");
    write_bin(&dir.join("new.dll"), b"ADDED-FILE");
    let d = dir.to_string_lossy().into_owned();
    write(
        &up.join("journal"),
        &format!(
            "JOURNAL:3\nGEN:{}\nTARGET:{}\nBACKUP:{}\\.updater\\backup\\{}\n\
             TMPDIR:{}\\.updater\\tmp\\{}\nSTAGE:APPLYING\nEXIST:app.exe\nNEW:new.dll\n",
            gen, d, d, gen, d, gen
        ),
    );
}

fn bytes(p: &Path) -> Vec<u8> {
    fs::read(p).unwrap_or_default()
}

// ---------------------------------------------------------------------------

/// 双击但无事可做：必须安静地成功，绝不能报错或改动任何东西。
#[test]
fn no_args_without_journal_is_a_silent_noop() {
    let (_tmp, _exe) = install_dir("selfrepair-noop");
    let dir = _exe.parent().unwrap().to_path_buf();

    let (code, out) = run_in(&dir, &[]);
    assert_eq!(code, 0, "no-args with nothing to repair must exit 0; output:\n{}", out);
    assert!(dir.join("updater.exe").exists(), "the updater must not touch itself");
}

/// **核心用例**：应用已经起不来时，人双击 `updater.exe` 就能把目录收敛回旧版。
#[test]
fn no_args_rolls_back_an_interrupted_site() {
    let (_tmp, _exe) = install_dir("selfrepair-rollback");
    let dir = _exe.parent().unwrap().to_path_buf();
    let gen = "20260916T100000-99999";
    make_interrupted(&dir, gen);

    let (code, out) = run_in(&dir, &[]);
    assert_eq!(code, 0, "self-repair must succeed; output:\n{}", out);

    // 逐项验证收敛结果：这就是"双击救回来"的全部含义
    assert_eq!(bytes(&dir.join("app.exe")), b"OLD-CONTENT", "old content must be restored");
    assert!(!dir.join("new.dll").exists(), "the newly added file must be removed");
    assert!(
        !dir.join(".updater").join("journal").exists(),
        "the journal must be consumed; otherwise the next launch repeats the work"
    );
    assert!(
        fs::read_dir(dir.join(".updater").join("backup"))
            .map(|r| r.flatten().count())
            .unwrap_or(0)
            == 0,
        "backup must be emptied by the rollback"
    );
}

/// 显式 `--recover` 不带 `--target` 与"无参数"等价（同一入口的两种写法）。
#[test]
fn explicit_recover_without_target_uses_own_directory() {
    let (_tmp, _exe) = install_dir("selfrepair-recover");
    let dir = _exe.parent().unwrap().to_path_buf();
    make_interrupted(&dir, "20260916T100001-1");

    let (code, out) = run_in(&dir, &["--recover"]);
    assert_eq!(code, 0, "output:\n{}", out);
    assert_eq!(bytes(&dir.join("app.exe")), b"OLD-CONTENT");
}

/// **安全边界**：Journal 指向别的目录时必须拒绝（退出 3），且**保留现场**。
/// 否则一个放错位置的 updater.exe 会去回滚别人的目录。
///
/// 这条同时是**不阻塞**的回归守卫：它必然走到"修复失败"分支，
/// 若 `main` 的门条件写松了，模态框会在这里把测试挂住（30s 后断言失败而非卡死）。
#[test]
fn foreign_journal_is_rejected_and_the_site_is_preserved() {
    let (_tmp, _exe) = install_dir("selfrepair-foreign");
    let dir = _exe.parent().unwrap().to_path_buf();
    make_interrupted(&dir, "20260916T100002-2");

    // 把 Journal 的 TARGET 改成别处 ⇒ validate 必须拒绝
    let jp = dir.join(".updater").join("journal");
    let t = read(&jp).replace(&dir.to_string_lossy().into_owned(), r"C:\Somewhere\Else");
    write(&jp, &t);

    let (code, out) = run_in(&dir, &[]);
    assert_eq!(code, 3, "a foreign journal must be refused with catastrophic exit; output:\n{}", out);
    assert_eq!(bytes(&dir.join("app.exe")), b"NEW-CONTENT", "the site must be left untouched");
    assert!(dir.join("new.dll").exists(), "nothing may be removed when the journal is refused");
}

/// `--silent` 只抑制**提示框**，绝不抑制修复动作本身。
#[test]
fn silent_suppresses_only_the_dialog_not_the_repair() {
    let (_tmp, _exe) = install_dir("selfrepair-silent");
    let dir = _exe.parent().unwrap().to_path_buf();
    make_interrupted(&dir, "20260916T100005-5");

    let (code, out) = run_in(&dir, &["--silent"]);
    assert_eq!(code, 0, "--silent must not disable the repair; output:\n{}", out);
    assert_eq!(bytes(&dir.join("app.exe")), b"OLD-CONTENT", "the repair must still happen");
    assert!(!dir.join(".updater").join("journal").exists());
}

/// 显式给出的 `--target` 必须压过缺省推断——否则脚本会莫名其妙地动到 updater 自己所在目录。
#[test]
fn explicit_target_takes_precedence_over_own_directory() {
    let (tmp, exe) = install_dir("selfrepair-prec");
    let own = exe.parent().unwrap().to_path_buf();
    let other = tmp.path().join("other");
    fs::create_dir_all(&other).unwrap();
    make_interrupted(&other, "20260916T100003-3");

    let (code, out) = run_in(&own, &["--recover", "--target", &other.to_string_lossy()]);
    assert_eq!(code, 0, "output:\n{}", out);
    assert_eq!(bytes(&other.join("app.exe")), b"OLD-CONTENT", "the explicit target must be repaired");
    assert!(
        !own.join(".updater").exists(),
        "the updater must not touch its own directory when --target is given"
    );
}

/// **反误判**：`--zip` 缺 `--target` 是"写错的更新调用"，绝不能被当成自身修复。
///
/// 误判的后果很严重：它会静默地去修 updater **自己所在的目录**然后返回 0，
/// 而调用方以为"更新成功了"——比直接报错危险得多。
#[test]
fn a_malformed_update_call_is_not_treated_as_self_repair() {
    let (_tmp, _exe) = install_dir("selfrepair-malformed");
    let dir = _exe.parent().unwrap().to_path_buf();
    // 放一个"可被收敛"的现场：一旦误判，它就会被改动，从而暴露问题
    make_interrupted(&dir, "20260916T100006-6");

    for args in [
        vec!["--zip", "x.zip"],
        vec!["--zip", "x.zip", "--launch", "a.exe"],
        vec!["--launch", "a.exe"],
        vec!["--dry-run"],
        vec!["--pid", "12345"],
    ] {
        let (code, out) = run_in(&dir, &args);
        assert_eq!(code, 2, "must stay a usage error: {:?}\n{}", args, out);
        assert!(out.contains("missing required --target"), "{:?}\n{}", args, out);
    }

    // 现场原封不动：没有任何一次误判动过它
    assert_eq!(bytes(&dir.join("app.exe")), b"NEW-CONTENT", "nothing may be repaired");
    assert!(dir.join("new.dll").exists());
    assert!(dir.join(".updater").join("journal").exists(), "the journal must be untouched");
}

/// `--help` 优先级最高：加了自身修复入口之后，无参数语义不能把 help 挤掉。
#[test]
fn help_still_wins_and_documents_the_repair_entry() {
    let (_tmp, _exe) = install_dir("selfrepair-help");
    let dir = _exe.parent().unwrap().to_path_buf();

    let (code, out) = run_in(&dir, &["--help"]);
    assert_eq!(code, 0);
    assert!(out.contains("REPAIR"), "help must document the repair entry:\n{}", out);
    assert!(
        out.contains("no arguments"),
        "help must state that no arguments means self-repair:\n{}",
        out
    );
}
