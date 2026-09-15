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
}
