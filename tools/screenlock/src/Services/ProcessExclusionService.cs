using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ScreenLock.Services
{
    public static class ProcessExclusionService
    {
        private static DateTime _lastCheck = DateTime.MinValue;
        private static string _lastHash = null;
        private static bool _lastResult = false;
        private static readonly object _lock = new object();
        private const int CacheMs = 2000;

        public static bool IsExcludedRunning(List<string> excludeList)
        {
            if (excludeList == null || excludeList.Count == 0) return false;

            // normalize list to hash for cache
            string hash = null;
            try
            {
                var normalized = excludeList.Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => Normalize(s)).Where(s => !string.IsNullOrEmpty(s))
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (normalized.Length == 0) return false;
                hash = string.Join("|", normalized);

                lock (_lock)
                {
                    if (_lastHash == hash && (DateTime.Now - _lastCheck).TotalMilliseconds < CacheMs)
                        return _lastResult;
                }

                bool running = CheckRunning(normalized);

                lock (_lock)
                {
                    _lastHash = hash;
                    _lastCheck = DateTime.Now;
                    _lastResult = running;
                }
                return running;
            }
            catch
            {
                return false;
            }
        }

        private static string Normalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim().Trim('"', '\'');
            raw = raw.Replace("/", "\\");
            // if contains path, take file name
            try { raw = Path.GetFileName(raw); } catch { }
            if (string.IsNullOrEmpty(raw)) return null;
            // strip .exe
            if (raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(0, raw.Length - 4);
            raw = raw.Trim();
            if (raw.Length == 0) return null;
            return raw.ToLowerInvariant();
        }

        private static bool CheckRunning(string[] normalizedExcludes)
        {
            try
            {
                var processes = Process.GetProcesses();
                var runningNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in processes)
                {
                    try
                    {
                        string name = p.ProcessName;
                        if (!string.IsNullOrEmpty(name))
                            runningNames.Add(name.ToLowerInvariant());
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                foreach (var excl in normalizedExcludes)
                {
                    if (runningNames.Contains(excl))
                        return true;
                    // also support wildcard prefix/suffix? simple contains check not needed; keep exact
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public static void InvalidateCache()
        {
            lock (_lock)
            {
                _lastCheck = DateTime.MinValue;
                _lastHash = null;
            }
        }
    }
}
