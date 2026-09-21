using System;
using System.IO;
using System.Reflection;

namespace KeepOn.Services
{
    /// <summary>
    /// 统一的路径解析：
    /// - 便携模式：exe 同级目录存在 portable.marker 时，配置与日志写在 exe 同级 data 目录。
    /// - 默认模式：写入 %AppData%\KeepOn。
    /// </summary>
    public static class Paths
    {
        private const string PortableMarker = "portable.marker";

        private static readonly Lazy<string> LazyDataDirectory = new Lazy<string>(ResolveDataDirectory);

        /// <summary>配置与日志的根目录</summary>
        public static string DataDirectory => LazyDataDirectory.Value;

        /// <summary>配置文件完整路径</summary>
        public static string ConfigFile => Path.Combine(DataDirectory, "config.json");

        /// <summary>程序所在目录</summary>
        public static string AppDirectory
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
                catch
                {
                    // 退化为当前目录
                }
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        private static string ResolveDataDirectory()
        {
            try
            {
                string appDir = AppDirectory;
                if (File.Exists(Path.Combine(appDir, PortableMarker)))
                {
                    return Path.Combine(appDir, "data");
                }
            }
            catch
            {
                // 探测便携标记失败时回退到漫游目录
            }

            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(roaming))
            {
                roaming = AppDomain.CurrentDomain.BaseDirectory;
            }

            return Path.Combine(roaming, "KeepOn");
        }
    }
}
