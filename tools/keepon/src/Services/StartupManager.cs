using System;
using Microsoft.Win32;

namespace KeepOn.Services
{
    /// <summary>
    /// 开机自启管理：写当前用户 HKCU\...\Run，无需管理员权限。
    /// </summary>
    public static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "KeepOn";

        /// <summary>当前是否已注册开机自启（以注册表实际值为准，而非配置项）</summary>
        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;

                    object value = key.GetValue(ValueName);
                    return value != null && !string.IsNullOrWhiteSpace(value.ToString());
                }
            }
            catch (Exception ex)
            {
                Log.Error("Startup", "读取开机自启注册表项失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 设置开机自启。成功返回 true；失败记录日志并返回 false（调用方应回滚 UI 状态）。
        /// </summary>
        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null)
                    {
                        Log.Warn("Startup", "无法打开 Run 注册表项，开机自启设置未生效");
                        return false;
                    }

                    if (enabled)
                    {
                        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        key.SetValue(ValueName, "\"" + exePath + "\" --minimized", RegistryValueKind.String);
                        Log.Info("Startup", "已启用开机自启: " + exePath);
                    }
                    else
                    {
                        if (key.GetValue(ValueName) != null)
                        {
                            key.DeleteValue(ValueName, false);
                        }
                        Log.Info("Startup", "已关闭开机自启");
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Startup", "设置开机自启失败", ex);
                return false;
            }
        }
    }
}
