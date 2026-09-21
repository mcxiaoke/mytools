using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace KeepOn.Services
{
    /// <summary>
    /// 全局热键注册（RegisterHotKey / UnregisterHotKey）。
    /// 需要窗口句柄作为消息接收目标，因此必须在主窗口创建后调用 Initialize。
    /// </summary>
    public class HotkeyService : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyId = 0x4B4F; // "KO"

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [Flags]
        private enum Modifiers : uint
        {
            None = 0x0000,
            Alt = 0x0001,
            Control = 0x0002,
            Shift = 0x0004,
            Win = 0x0008,
            NoRepeat = 0x4000
        }

        private HwndSource _source;
        private IntPtr _handle = IntPtr.Zero;
        private bool _registered;
        private string _currentGesture;

        /// <summary>热键触发回调</summary>
        public event Action Pressed;

        public bool IsRegistered => _registered;

        /// <summary>当前已注册的手势文本；未注册时为 null</summary>
        public string CurrentGesture => _registered ? _currentGesture : null;

        /// <summary>
        /// 绑定消息接收窗口。传入已创建句柄的 Window 即可。
        /// </summary>
        public void Initialize(System.Windows.Window window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));

            _handle = new WindowInteropHelper(window).Handle;
            _source = HwndSource.FromHwnd(_handle);
            _source?.AddHook(WndProc);
        }

        /// <summary>
        /// 注册（或重新注册）热键。gesture 形如 "Win+Shift+W"、"Ctrl+Alt+K"。
        /// 返回是否注册成功；失败通常是格式非法或已被其他程序占用。
        /// </summary>
        public bool Register(string gesture)
        {
            Unregister();

            if (string.IsNullOrWhiteSpace(gesture)) return false;
            if (_handle == IntPtr.Zero)
            {
                Log.Warn("Hotkey", "窗口句柄尚未就绪，无法注册全局热键");
                return false;
            }

            if (!TryParseGesture(gesture, out uint modifiers, out uint vk))
            {
                Log.Warn("Hotkey", "无法解析热键格式: " + gesture);
                return false;
            }

            try
            {
                bool ok = RegisterHotKey(_handle, HotkeyId, modifiers, vk);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.Warn("Hotkey", "注册全局热键失败 (" + gesture + ")，错误码 " + err +
                                       "，该组合可能已被其他程序占用");
                    return false;
                }

                _registered = true;
                _currentGesture = gesture.Trim();
                Log.Info("Hotkey", "已注册全局热键: " + _currentGesture);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Hotkey", "注册全局热键异常", ex);
                return false;
            }
        }

        public void Unregister()
        {
            if (!_registered || _handle == IntPtr.Zero)
            {
                _registered = false;
                return;
            }

            try
            {
                UnregisterHotKey(_handle, HotkeyId);
            }
            catch
            {
                // 释放失败无需中断流程
            }
            finally
            {
                _registered = false;
                _currentGesture = null;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                try
                {
                    Pressed?.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Error("Hotkey", "热键回调执行异常", ex);
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>手势文本的分隔符，如 "Win+Shift+W" 中的 '+'</summary>
        private static readonly char[] GestureSeparators = { '+' };

        /// <summary>
        /// 解析 "Win+Shift+W" 形式的手势文本为修饰键与虚拟键码。
        /// </summary>
        public static bool TryParseGesture(string gesture, out uint modifiers, out uint virtualKey)
        {
            modifiers = 0;
            virtualKey = 0;

            if (string.IsNullOrWhiteSpace(gesture)) return false;

            string[] parts = gesture.Split(GestureSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            for (int i = 0; i < parts.Length; i++)
            {
                string token = parts[i].Trim();
                if (token.Length == 0) return false;

                bool isLast = i == parts.Length - 1;

                if (!isLast)
                {
                    switch (token.ToUpperInvariant())
                    {
                        case "CTRL":
                        case "CONTROL":
                            modifiers |= (uint)Modifiers.Control;
                            break;
                        case "ALT":
                            modifiers |= (uint)Modifiers.Alt;
                            break;
                        case "SHIFT":
                            modifiers |= (uint)Modifiers.Shift;
                            break;
                        case "WIN":
                        case "WINDOWS":
                            modifiers |= (uint)Modifiers.Win;
                            break;
                        default:
                            // 修饰键位置出现非修饰键，视为非法
                            return false;
                    }
                }
                else
                {
                    if (!_KeyMapLookup.TryGetValue(token, out uint vk))
                    {
                        return false;
                    }
                    virtualKey = vk;
                }
            }

            // 必须至少有一个修饰键，否则会拦截普通按键
            if (modifiers == 0) return false;
            if (virtualKey == 0) return false;

            modifiers |= (uint)Modifiers.NoRepeat;
            return true;
        }

        private static readonly Dictionary<string, uint> _KeyMapLookup = BuildStaticKeyMap();

        private static Dictionary<string, uint> BuildStaticKeyMap()
        {
            var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

            // 虚拟键码基数：A-Z 从 VK_A(0x41) 起连续 26 个
            const int VirtualKeyA = 0x41;
            const int LetterCount = 26;

            // 虚拟键码基数：0-9 从 VK_0(0x30) 起连续 10 个
            const int VirtualKey0 = 0x30;
            const int DigitCount = 10;

            // 虚拟键码基数：F1-F24 从 VK_F1(0x70) 起连续 24 个
            const int VirtualKeyF1 = 0x70;
            const int FunctionKeyCount = 24;

            // A-Z
            for (int i = 0; i < LetterCount; i++)
            {
                char c = (char)('A' + i);
                map[c.ToString()] = (uint)(VirtualKeyA + i);
            }

            // 0-9
            for (int i = 0; i < DigitCount; i++)
            {
                map[i.ToString()] = (uint)(VirtualKey0 + i);
            }

            // F1-F24
            for (int i = 1; i <= FunctionKeyCount; i++)
            {
                map["F" + i] = (uint)(VirtualKeyF1 + i - 1);
            }

            // 常用符号键（OEM）
            map["`"] = 0xC0;
            map["~"] = 0xC0;
            map["-"] = 0xBD;
            map["="] = 0xBB;
            map["["] = 0xDB;
            map["]"] = 0xDD;
            map["\\"] = 0xDC;
            map[";"] = 0xBA;
            map["'"] = 0xDE;
            map[","] = 0xBC;
            map["."] = 0xBE;
            map["/"] = 0xBF;

            // 特殊键
            map["SPACE"] = 0x20;
            map["TAB"] = 0x09;
            map["ENTER"] = 0x0D;
            map["RETURN"] = 0x0D;
            map["ESC"] = 0x1B;
            map["ESCAPE"] = 0x1B;
            map["BACKSPACE"] = 0x08;
            map["DELETE"] = 0x2E;
            map["DEL"] = 0x2E;
            map["INSERT"] = 0x2D;
            map["HOME"] = 0x24;
            map["END"] = 0x23;
            map["PAGEUP"] = 0x21;
            map["PAGEDOWN"] = 0x22;
            map["LEFT"] = 0x25;
            map["UP"] = 0x26;
            map["RIGHT"] = 0x27;
            map["DOWN"] = 0x28;

            return map;
        }

        public void Dispose()
        {
            Unregister();

            try
            {
                _source?.RemoveHook(WndProc);
            }
            catch
            {
                // 忽略
            }

            _source = null;
            _handle = IntPtr.Zero;
        }
    }
}
