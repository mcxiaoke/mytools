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

    public static void Install(string appExePath, string? cliExePath = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        // 统一优先调用 GUI 应用程序（appExePath），杜绝控制台黑框
        string targetExe = appExePath;
        string iconPath = GetOrCreateIconPath(appExePath);

        // 1. 文件右键 (*)
        RegisterCascadingMenu(@"Software\Classes\*\shell", targetExe, iconPath, isBackground: false);

        // 2. 文件夹右键 (Directory)
        RegisterCascadingMenu(@"Software\Classes\Directory\shell", targetExe, iconPath, isBackground: false);

        // 3. 文件夹背景空白处 (Directory\Background)
        RegisterCascadingMenu(@"Software\Classes\Directory\Background\shell", targetExe, iconPath, isBackground: true);

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

        string targetParam = isBackground ? "\"%V\"" : "\"%1\"";

        // 子菜单 1: 混淆
        using (var hideKey = subShell.CreateSubKey("1_Hide"))
        {
            hideKey.SetValue("MUIVerb", "混淆保护 (Hide)");
            hideKey.SetValue("Icon", iconPath);
            using var cmdKey = hideKey.CreateSubKey("command");
            cmdKey.SetValue("", $"\"{appExe}\" --hide {targetParam}");
        }

        // 子菜单 2: 还原
        using (var unhideKey = subShell.CreateSubKey("2_Unhide"))
        {
            unhideKey.SetValue("MUIVerb", "一键还原 (Unhide)");
            unhideKey.SetValue("Icon", iconPath);
            using var cmdKey = unhideKey.CreateSubKey("command");
            cmdKey.SetValue("", $"\"{appExe}\" --unhide {targetParam}");
        }

        // 子菜单 3: 在 HideFiles 中打开
        using (var openKey = subShell.CreateSubKey("3_OpenApp"))
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
