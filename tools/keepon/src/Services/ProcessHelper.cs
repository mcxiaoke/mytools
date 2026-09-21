using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace KeepOn.Services
{
    /// <summary>
    /// 进程名归一化、容错匹配与运行进程发现。
    /// </summary>
    public static class ProcessHelper
    {
        /// <summary>进程名显示时的最大长度，超出则截断并加省略号</summary>
        private const int MaxDisplayNameLength = 28;

        /// <summary>截断后保留的字符数，留出 3 个字符给省略号</summary>
        private const int TruncatedNameLength = MaxDisplayNameLength - 3;

        public class RunningProcessInfo
        {
            /// <summary>标准形式：小写且带 .exe，如 "chrome.exe"</summary>
            public string ProcessName { get; set; }

            /// <summary>不带扩展名，如 "chrome"</summary>
            public string PureName { get; set; }

            public string WindowTitle { get; set; }

            public string DisplayText
            {
                get
                {
                    if (string.IsNullOrWhiteSpace(WindowTitle)) return ProcessName;
                    string title = WindowTitle.Length > MaxDisplayNameLength
                        ? WindowTitle.Substring(0, TruncatedNameLength) + "..."
                        : WindowTitle;
                    return ProcessName + " (" + title + ")";
                }
            }

            public override string ToString() => DisplayText;
        }

        /// <summary>
        /// 归一化为小写且带 .exe 的标准进程名。
        /// "chrome" -> "chrome.exe"；"D:\a\game.exe" -> "game.exe"；无效输入返回 null。
        /// </summary>
        public static string Normalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            string s = raw.Trim().Trim('"', '\'', '`');
            s = s.Replace("/", "\\");

            try
            {
                s = Path.GetFileName(s);
            }
            catch
            {
                // 路径非法字符等情况，退化为原始字符串继续处理
            }

            if (string.IsNullOrWhiteSpace(s)) return null;

            s = s.Trim();
            if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                s += ".exe";
            }

            return s.ToLowerInvariant();
        }

        /// <summary>取不带 .exe 的纯进程名（供 Process.GetProcessesByName 使用）</summary>
        public static string NormalizeNameOnly(string raw)
        {
            string norm = Normalize(raw);
            if (string.IsNullOrEmpty(norm)) return null;

            return norm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? norm.Substring(0, norm.Length - 4)
                : norm;
        }

        /// <summary>批量归一化：过滤无效项并按忽略大小写去重</summary>
        public static List<string> NormalizeList(IEnumerable<string> rawList)
        {
            var result = new List<string>();
            if (rawList == null) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in rawList)
            {
                string norm = Normalize(item);
                if (!string.IsNullOrEmpty(norm) && seen.Add(norm))
                {
                    result.Add(norm);
                }
            }

            return result;
        }

        /// <summary>忽略大小写、忽略 .exe 与路径差异的宽松匹配</summary>
        public static bool IsMatch(string procA, string procB)
        {
            if (string.IsNullOrWhiteSpace(procA) || string.IsNullOrWhiteSpace(procB))
                return false;

            string nameA = NormalizeNameOnly(procA);
            string nameB = NormalizeNameOnly(procB);

            return string.Equals(nameA, nameB, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>判断某个进程当前是否存在运行实例</summary>
        public static bool IsProcessRunning(string rawProcessName)
        {
            string nameOnly = NormalizeNameOnly(rawProcessName);
            if (string.IsNullOrEmpty(nameOnly)) return false;

            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName(nameOnly);
                return processes != null && processes.Length > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                DisposeAll(processes);
            }
        }

        /// <summary>
        /// 在候选名单中找出第一个正在运行的进程，返回其标准名称；未命中返回 null。
        /// </summary>
        public static string FindFirstRunning(IEnumerable<string> candidates)
        {
            if (candidates == null) return null;

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                string nameOnly = NormalizeNameOnly(candidate);
                if (string.IsNullOrEmpty(nameOnly)) continue;

                Process[] processes = null;
                try
                {
                    processes = Process.GetProcessesByName(nameOnly);
                    if (processes != null && processes.Length > 0)
                    {
                        return Normalize(candidate);
                    }
                }
                catch
                {
                    // 单个进程查询失败不影响后续候选
                }
                finally
                {
                    DisposeAll(processes);
                }
            }

            return null;
        }

        /// <summary>
        /// 获取当前有可见主窗口的进程列表（排除自身与 explorer），按名称排序去重。
        /// </summary>
        public static List<RunningProcessInfo> GetRunningWindowProcesses(string selfProcessName = "keepon")
        {
            var result = new List<RunningProcessInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string self = Normalize(selfProcessName);

            Process[] all = null;
            try
            {
                all = Process.GetProcesses();
                foreach (var p in all)
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;

                        string pure = p.ProcessName;
                        string norm = Normalize(pure);
                        if (string.IsNullOrEmpty(norm)) continue;

                        if (!string.IsNullOrEmpty(self) && IsMatch(norm, self)) continue;
                        if (string.Equals(norm, "explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;

                        if (seen.Add(norm))
                        {
                            result.Add(new RunningProcessInfo
                            {
                                ProcessName = norm,
                                PureName = pure,
                                WindowTitle = p.MainWindowTitle
                            });
                        }
                    }
                    catch
                    {
                        // 部分系统进程拒绝访问，忽略
                    }
                }
            }
            catch
            {
                // 枚举失败时返回已收集到的部分结果
            }
            finally
            {
                DisposeAll(all);
            }

            return result.OrderBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void DisposeAll(Process[] processes)
        {
            if (processes == null) return;
            foreach (var p in processes)
            {
                try { p?.Dispose(); } catch { }
            }
        }
    }
}
