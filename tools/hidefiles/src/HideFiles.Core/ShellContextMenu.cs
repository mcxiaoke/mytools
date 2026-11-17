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

        string targetExe = cliExePath ?? appExePath;

        // 1. 文件右键 (*)
        RegisterCascadingMenu(@"Software\Classes\*\shell", appExePath, targetExe, isBackground: false);

        // 2. 文件夹右键 (Directory)
        RegisterCascadingMenu(@"Software\Classes\Directory\shell", appExePath, targetExe, isBackground: false);

        // 3. 文件夹背景空白处 (Directory\Background)
        RegisterCascadingMenu(@"Software\Classes\Directory\Background\shell", appExePath, targetExe, isBackground: true);

        // 刷新 Shell 关联
        RefreshShell();
    }

    public static void Uninstall()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        UnregisterMenu(@"Software\Classes\*\shell");
        UnregisterMenu(@"Software\Classes\Directory\shell");
        UnregisterMenu(@"Software\Classes\Directory\Background\shell");

        RefreshShell();
    }

    private static void RegisterCascadingMenu(string parentKeyPath, string appExe, string cliExe, bool isBackground)
    {
        using var parentKey = Registry.CurrentUser.CreateSubKey(parentKeyPath);
        if (parentKey == null) return;

        using var mainMenuKey = parentKey.CreateSubKey(MenuKeyName);
        if (mainMenuKey == null) return;

        mainMenuKey.SetValue("MUIVerb", "HideFiles");
        mainMenuKey.SetValue("Icon", $"\"{appExe}\",0");
        mainMenuKey.SetValue("SubCommands", string.Empty); // 开启静态级联子菜单

        using var subShell = mainMenuKey.CreateSubKey("shell");
        if (subShell == null) return;

        string targetParam = isBackground ? "\"%V\"" : "\"%1\"";

        // 子菜单 1: 混淆
        using (var hideKey = subShell.CreateSubKey("1_Hide"))
        {
            hideKey.SetValue("MUIVerb", "混淆保护 (Hide)");
            using var cmdKey = hideKey.CreateSubKey("command");
            cmdKey.SetValue("", $"\"{cliExe}\" hide {targetParam}");
        }

        // 子菜单 2: 还原
        using (var unhideKey = subShell.CreateSubKey("2_Unhide"))
        {
            unhideKey.SetValue("MUIVerb", "一键还原 (Unhide)");
            using var cmdKey = unhideKey.CreateSubKey("command");
            cmdKey.SetValue("", $"\"{cliExe}\" unhide {targetParam}");
        }

        // 子菜单 3: 在 HideFiles 中打开
        using (var openKey = subShell.CreateSubKey("3_OpenApp"))
        {
            openKey.SetValue("MUIVerb", "在 HideFiles 中管理...");
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

    public static void RefreshShell()
    {
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }
}
