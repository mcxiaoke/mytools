using System;
using System.IO;
using System.Text;

namespace ScreenLock.Services.Tasks
{
    public static class TaskLogger
    {
        private static readonly object _lock = new object();
        private const long MaxFileBytes = 5L * 1024 * 1024;
        private const int KeepGenerations = 3;

        public static void EnsureLogDir()
        {
            try
            {
                var dir = ConfigService.LogsDirPath;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
        }

        public static string GetTaskLogPath(string taskName)
        {
            string safe = Sanitize(taskName);
            return Path.Combine(ConfigService.LogsDirPath, "task-" + safe + ".log");
        }

        public static string GetAggregateLogPath()
        {
            return Path.Combine(ConfigService.LogsDirPath, "tasks.log");
        }

        public static void Info(string taskName, string message)
        {
            Write(taskName, "INFO", message);
        }

        public static void Warn(string taskName, string message)
        {
            Write(taskName, "WARN", message);
        }

        public static void Error(string taskName, string message)
        {
            Write(taskName, "ERROR", message);
        }

        public static void Write(string taskName, string level, string message)
        {
            EnsureLogDir();
            string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] [{2}] {3}",
                DateTime.Now, level, taskName ?? "system", message);
            lock (_lock)
            {
                try
                {
                    string agg = GetAggregateLogPath();
                    RotateIfNeeded(agg);
                    File.AppendAllText(agg, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(taskName))
                {
                    try
                    {
                        string path = GetTaskLogPath(taskName);
                        RotateIfNeeded(path);
                        File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                    }
                    catch { }
                }
            }
        }

        public static void WriteOutput(string taskName, string stream, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            // split lines to preserve prefix per line
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var l in lines)
            {
                if (l.Length == 0) continue;
                Write(taskName, stream, l);
            }
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var info = new FileInfo(path);
                if (info.Length < MaxFileBytes) return;
                // rotate: .3 -> delete, .2 -> .3, .1 -> .2, current -> .1
                for (int i = KeepGenerations; i >= 1; i--)
                {
                    string src = i == 1 ? path : path + "." + (i - 1);
                    string dst = path + "." + i;
                    if (File.Exists(src))
                    {
                        try
                        {
                            if (File.Exists(dst)) File.Delete(dst);
                            File.Move(src, dst);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unknown";
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                sb.Append(ok ? c : '_');
            }
            string s = sb.ToString();
            if (s.Length > 64) s = s.Substring(0, 64);
            return s;
        }
    }
}
