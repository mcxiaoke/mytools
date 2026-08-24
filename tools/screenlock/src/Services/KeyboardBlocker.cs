using System;
using System.Runtime.InteropServices;

namespace ScreenLock.Services
{
    public class KeyboardBlocker : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int VK_TAB = 0x09;
        private const int VK_RETURN = 0x0D;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_BACK = 0x08;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_APPS = 0x5D;
        private const int VK_F4 = 0x70;
        private const int VK_NUMPAD0 = 0x60;
        private const int VK_NUMPAD9 = 0x69;
        private const int VK_LBUTTON = 0x01;
        private const int VK_RBUTTON = 0x02;
        private const int VK_CANCEL = 0x03;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private IntPtr _hook = IntPtr.Zero;
        private readonly LowLevelKeyboardProc _proc;
        private readonly IntPtr _module;

        public KeyboardBlocker()
        {
            _proc = HookProc;
            _module = GetModuleHandle(null);
        }

        public bool Installed { get { return _hook != IntPtr.Zero; } }

        public void Install()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, _module, 0);
        }

        public void Remove()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private static bool IsAllowedKey(uint vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return true;
            if (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9) return true;
            switch ((int)vk)
            {
                case VK_BACK:
                case VK_RETURN:
                case VK_SHIFT:
                    return true;
            }
            return false;
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP)
                {
                    var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    bool altDown = (info.flags & 0x20) != 0;
                    uint vk = info.vkCode & 0xFF;

                    bool winDown = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

                    if (IsAllowedKey(vk) && !winDown && !altDown)
                        return CallNextHookEx(_hook, nCode, wParam, lParam);

                    if (vk == VK_LWIN || vk == VK_RWIN || vk == VK_APPS)
                        return (IntPtr)1;

                    if (winDown)
                        return (IntPtr)1;

                    if (altDown && (vk == VK_TAB || vk == VK_ESCAPE || vk == VK_F4 || vk == VK_SPACE))
                        return (IntPtr)1;

                    if (!altDown && vk == VK_ESCAPE && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
                        return (IntPtr)1;
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Remove();
        }
    }
}
