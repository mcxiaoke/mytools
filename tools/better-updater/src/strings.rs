// Tier: 2
//! 原生双语界面文案管理（零依赖，zh-CN 简体中文 / 其余所有区域平滑 Fallback 英文）。
use windows_sys::Win32::Globalization::GetUserDefaultUILanguage;

#[derive(Debug, Clone, Copy)]
pub struct UiStrings {
    pub default_title: &'static str,
    pub preparing: &'static str,
    pub waiting_process: &'static str,
    pub verifying_package: &'static str,
    pub updating_files: &'static str,
    pub completing: &'static str,
}

pub const ZH_CN: UiStrings = UiStrings {
    default_title: "正在更新应用...",
    preparing: "正在准备更新...",
    waiting_process: "正在等待旧版本退出...",
    verifying_package: "正在校验更新包...",
    updating_files: "正在更新文件，请稍候...",
    completing: "更新完成，正在启动新版本...",
};

pub const EN: UiStrings = UiStrings {
    default_title: "Updating application...",
    preparing: "Preparing update...",
    waiting_process: "Waiting for previous version to exit...",
    verifying_package: "Verifying update package...",
    updating_files: "Updating files, please wait...",
    completing: "Update complete, launching application...",
};

/// Returns true if the user's primary UI language is Simplified Chinese (zh-CN, 0x0804).
#[allow(dead_code)]
pub fn is_zh_cn() -> bool {
    unsafe { GetUserDefaultUILanguage() == 0x0804 }
}

/// Returns the centralized UI strings for a specific LANGID.
pub fn get_for_langid(langid: u16) -> &'static UiStrings {
    if langid == 0x0804 {
        &ZH_CN
    } else {
        &EN
    }
}

/// Returns the centralized UI strings for the current system locale.
pub fn get() -> &'static UiStrings {
    get_for_langid(unsafe { GetUserDefaultUILanguage() })
}

// ---------------------------------------------------------------------------
// 自身修复失败的提示框文案
// ---------------------------------------------------------------------------
//
// 单独成函数而不是塞进 `UiStrings`：它需要插入退出码与目录，而 `format!` 的格式串
// **必须是字面量**，不能来自结构体字段。此处每个分支各持一份字面量，两不互串。

/// 提示框标题。
pub fn repair_failed_title_for(langid: u16) -> &'static str {
    if langid == 0x0804 {
        "自动修复未完成"
    } else {
        "Automatic repair did not complete"
    }
}

/// 提示框正文（含退出码与目录，末尾给行动指引）。
pub fn repair_failed_message_for(langid: u16, code: i32, target: &str) -> String {
    if langid == 0x0804 {
        format!(
            "更新器无法自动恢复该目录（退出码 {}）。\n\n目录：{}\n\n\
             请重新下载完整的更新包，解压覆盖到上面的目录，然后重新启动程序。",
            code, target
        )
    } else {
        format!(
            "The updater could not restore this directory automatically (exit code {}).\n\n\
             Directory: {}\n\n\
             Download the full update package again, extract it over that directory, \
             then start the app once more.",
            code, target
        )
    }
}

pub fn repair_failed_title() -> &'static str {
    repair_failed_title_for(unsafe { GetUserDefaultUILanguage() })
}

pub fn repair_failed_message(code: i32, target: &str) -> String {
    repair_failed_message_for(unsafe { GetUserDefaultUILanguage() }, code, target)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_locale_fallback() {
        // zh-CN (0x0804) gets Chinese
        assert_eq!(get_for_langid(0x0804).default_title, "正在更新应用...");
        assert_eq!(get_for_langid(0x0804).preparing, "正在准备更新...");

        // All other locales/regions get English per specification
        assert_eq!(get_for_langid(0x0409).default_title, "Updating application..."); // en-US
        assert_eq!(get_for_langid(0x0809).default_title, "Updating application..."); // en-GB
        assert_eq!(get_for_langid(0x0404).default_title, "Updating application..."); // zh-TW
        assert_eq!(get_for_langid(0x0C04).default_title, "Updating application..."); // zh-HK
        assert_eq!(get_for_langid(0x0411).default_title, "Updating application..."); // ja-JP
        assert_eq!(get_for_langid(0x0407).default_title, "Updating application..."); // de-DE
    }

    #[test]
    fn test_strings_validity() {
        assert!(!ZH_CN.default_title.is_empty());
        assert!(!ZH_CN.preparing.is_empty());
        assert!(!ZH_CN.waiting_process.is_empty());
        assert!(!ZH_CN.verifying_package.is_empty());
        assert!(!ZH_CN.updating_files.is_empty());
        assert!(!ZH_CN.completing.is_empty());

        assert!(!EN.default_title.is_empty());
        assert!(!EN.preparing.is_empty());
        assert!(!EN.waiting_process.is_empty());
        assert!(!EN.verifying_package.is_empty());
        assert!(!EN.updating_files.is_empty());
        assert!(!EN.completing.is_empty());

        let s = get();
        assert!(!s.default_title.is_empty());
    }

    /// 修复提示必须把**目录与退出码**都带出来，且两种语言都不能残留占位符。
    /// 这条文案出现在"已经出问题"的时刻，绝不能再出错。
    #[test]
    fn test_repair_failed_message_carries_context() {
        for langid in [0x0804u16, 0x0409] {
            let title = repair_failed_title_for(langid);
            assert!(!title.is_empty());

            let msg = repair_failed_message_for(langid, 3, "C:\\App\\JigsawFox");
            assert!(msg.contains("3"), "exit code must appear: {}", msg);
            assert!(msg.contains("C:\\App\\JigsawFox"), "target must appear: {}", msg);
            assert!(!msg.contains("{}"), "no placeholder may survive: {}", msg);
            // 必须有行动指引（最后一段）
            assert!(msg.lines().last().map(|l| l.trim().len() > 10).unwrap_or(false), "{}", msg);
        }
        // zh-CN 与英文必须是两份不同文案（不是漏配后回落到同一份）
        assert_ne!(repair_failed_title_for(0x0804), repair_failed_title_for(0x0409));
        assert_ne!(
            repair_failed_message_for(0x0804, 3, "d"),
            repair_failed_message_for(0x0409, 3, "d")
        );
    }
}
