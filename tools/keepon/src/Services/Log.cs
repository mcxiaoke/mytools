using System;
using System.IO;

namespace KeepOn.Services
{
    /// <summary>
    /// 极简日志：写入 %AppData%\KeepOn\logs\keepon-yyyyMMdd.log，按天切分并限制单文件体积。
    /// 日志失败绝不影响主流程。
    /// </summary>
    public static class Log
    {
        private static readonly object SyncRoot = new object();
        private const long MaxLogBytes = 2 * 1024 * 1024;

        public static string LogDirectory { get; } =
            Path.Combine(Paths.DataDirectory, "logs");

        public static void Info(string category, string message)
            => Write("INFO", category, message, null);

        public static void Warn(string category, string message)
            => Write("WARN", category, message, null);

        public static void Error(string category, string message, Exception ex = null)
            => Write("ERROR", category, message, ex);

        private static void Write(string level, string category, string message, Exception ex)
        {
            try
            {
                lock (SyncRoot)
                {
                    Directory.CreateDirectory(LogDirectory);

                    string file = Path.Combine(
                        LogDirectory,
                        "keepon-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

                    TrimIfTooLarge(file);

                    string line = string.Format(
                        "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] [{2}] {3}{4}{5}",
                        DateTime.Now,
                        level,
                        category,
                        message,
                        ex == null ? string.Empty : " | " + ex.GetType().Name + ": " + ex.Message,
                        Environment.NewLine);

                    File.AppendAllText(file, line);
                }
            }
            catch
            {
                // 日志系统本身不允许抛出异常
            }
        }

        private static void TrimIfTooLarge(string file)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length < MaxLogBytes) return;

                // 超过上限直接截断重建，避免日志无限增长
                File.WriteAllText(file, string.Empty);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
