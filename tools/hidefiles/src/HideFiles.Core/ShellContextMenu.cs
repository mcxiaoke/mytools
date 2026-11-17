using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HideFiles.Core;

[SupportedOSPlatform("windows")]
public static class ShellContextMenu
{
    private const string MenuKeyName = "HideFiles";

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_FLUSH = 0x1000;

    public static bool IsInstalled()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\" + MenuKeyName);
        return key != null;
    }

    /// <summary>
    /// 安装右键菜单。appExePath 必须是 GUI 程序（HideFilesApp.exe）——菜单项传的是
    /// --hide/--unhide/--encrypt/--decrypt 这些 GUI 参数，指向 CLI 会导致点击无效并弹控制台。
    /// </summary>
    public static void Install(string appExePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        string iconPath = GetOrCreateIconPath(appExePath);

        // 1. 文件右键 (*)
        RegisterCascadingMenu(@"Software\Classes\*\shell", appExePath, iconPath, isBackground: false);

        // 2. 文件夹右键 (Directory)
        RegisterCascadingMenu(@"Software\Classes\Directory\shell", appExePath, iconPath, isBackground: false);

        // 3. 文件夹背景空白处 (Directory\Background)
        RegisterCascadingMenu(@"Software\Classes\Directory\Background\shell", appExePath, iconPath, isBackground: true);

        // 刷新 Shell 关联（仅在安装/卸载菜单时执行一次）
        RefreshShell();
    }

    public static void Uninstall()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        UnregisterMenu(@"Software\Classes\*\shell");
        UnregisterMenu(@"Software\Classes\Directory\shell");
        UnregisterMenu(@"Software\Classes\Directory\Background\shell");

        // 刷新 Shell 关联（仅在安装/卸载菜单时执行一次）
        RefreshShell();
    }

    private static string GetOrCreateIconPath(string appExe)
    {
        try
        {
            string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HideFiles");
            Directory.CreateDirectory(appDataDir);
            string appDataIco = Path.Combine(appDataDir, "AppIcon.ico");

            // 1. 如果已有物理文件且大小正常，直接使用
            if (File.Exists(appDataIco) && new FileInfo(appDataIco).Length > 0)
            {
                return appDataIco;
            }

            // 2. 尝试从 exe 所在目录提取同步
            string exeDir = Path.GetDirectoryName(appExe) ?? string.Empty;
            string localIco = Path.Combine(exeDir, "Assets", "Icon", "AppIcon.ico");
            if (!File.Exists(localIco))
            {
                localIco = Path.Combine(exeDir, "AppIcon.ico");
            }

            if (File.Exists(localIco))
            {
                File.Copy(localIco, appDataIco, overwrite: true);
                return appDataIco;
            }

            // 3. 从 Core 程序集内嵌资源释放 AppIcon.ico
            var asm = typeof(ShellContextMenu).Assembly;
            using var stream = asm.GetManifestResourceStream("HideFiles.Core.AppIcon.ico");
            if (stream != null && stream.Length > 0)
            {
                using var fs = new FileStream(appDataIco, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.CopyTo(fs);
                return appDataIco;
            }
        }
        catch { }

        // 回退方案：使用 exe PE 资源图标
        return $"{appExe},0";
    }

    private static void RegisterCascadingMenu(string parentKeyPath, string appExe, string iconPath, bool isBackground)
    {
        using var parentKey = Registry.CurrentUser.CreateSubKey(parentKeyPath);
        if (parentKey == null) return;

        using var mainMenuKey = parentKey.CreateSubKey(MenuKeyName);
        if (mainMenuKey == null) return;

        mainMenuKey.SetValue("MUIVerb", "HideFiles");
        mainMenuKey.SetValue("Icon", iconPath);
        mainMenuKey.SetValue("SubCommands", string.Empty); // 开启静态级联子菜单

        using var subShell = mainMenuKey.CreateSubKey("shell");
        if (subShell == null) return;

        // 清理旧版本遗留的子项（例如旧版的 3_OpenApp），避免菜单出现重复项
        foreach (var legacy in new[] { "3_OpenApp" })
        {
            try { subShell.DeleteSubKeyTree(legacy, throwOnMissingSubKey: false); }
            catch { }
        }

        string targetParam = isBackground ? "\"%V\"" : "\"%1\"";

        // 子菜单 1: 混淆文件（结构锁，原地加密关键结构，极速）
        using (var hideKey = subShell.CreateSubKey("1_Hide"))
        {
            hideKey.SetValue("MUIVerb", "混淆文件 (结构锁·极速)");
            hideKey.SetValue("Icon", iconPath);
            using var cmdKey = hideKey.CreateSubKey("command");
            cmdKey.SetValue("", $"\"{appExe}\" --hide {targetParam}");
        }

        // 子菜单 2: 还原文件（结构锁还原）
        using (var unhideKey = subShell.CreateSubKey("2_Unhide"))
        {
            unhideKey.SetValue("MUIVerb", "还原文件 (结构锁)");
            unhideKey.SetValue("Icon", iconPath);
            using var cmdKey = unhideKey.CreateSubKey("command");
            cmdKey.SetValue("", $"\"{appExe}\" --unhide {targetParam}");
        }

        // 子菜单 3: 加密文件（整体加密，最强保护）
        using (var encKey = subShell.CreateSubKey("3_Encrypt"))
        {
            encKey.SetValue("MUIVerb", "加密文件 (整体·最强)");
            encKey.SetValue("Icon", iconPath);
            using var cmdKey = encKey.CreateSubKey("command");
            cmdKey.SetValue("", $"\"{appExe}\" --encrypt {targetParam}");
        }

        // 子菜单 4: 解密文件（整体加密还原）
        using (var decKey = subShell.CreateSubKey("4_Decrypt"))
        {
            decKey.SetValue("MUIVerb", "解密文件 (整体)");
            decKey.SetValue("Icon", iconPath);
            using var cmdKey = decKey.CreateSubKey("command");
            cmdKey.SetValue("", $"\"{appExe}\" --decrypt {targetParam}");
        }

        // 子菜单 5: 在 HideFiles 中打开
        using (var openKey = subShell.CreateSubKey("5_OpenApp"))
        {
            openKey.SetValue("MUIVerb", "在 HideFiles 中管理...");
            openKey.SetValue("Icon", iconPath);
            using var cmdKey = openKey.CreateSubKey("command");
            cmdKey.SetValue("", $"\"{appExe}\" --open {targetParam}");
        }
    }

    private static void UnregisterMenu(string parentKeyPath)
    {
        try
        {
            using var parentKey = Registry.CurrentUser.OpenSubKey(parentKeyPath, writable: true);
            parentKey?.DeleteSubKeyTree(MenuKeyName, throwOnMissingSubKey: false);
        }
        catch { }
    }

    /// <summary>
    /// 仅在注册表菜单发生增删时刷新 Explorer 关联，禁止在常规文件混淆还原中调用（避免桌面闪烁）
    /// </summary>
    public static void RefreshShell()
    {
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }
}
