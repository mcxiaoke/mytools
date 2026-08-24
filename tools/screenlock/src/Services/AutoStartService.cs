using System;
using Microsoft.Win32;

namespace ScreenLock.Services
{
    public static class AutoStartService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ScreenLock";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    return key != null && key.GetValue(ValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        key.SetValue(ValueName, "\"" + exe + "\"");
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
            }
            catch
            {
            }
        }

        public static void Sync(bool enable)
        {
            if (IsEnabled() != enable) SetEnabled(enable);
        }
    }
}
