using System;
using System.Collections.Generic;
using System.Linq;

namespace KeepOn
{
    /// <summary>
    /// 命令行参数：
    ///   --minimized / --min     启动后不打开设置窗口，仅常驻托盘（开机自启使用）
    ///   --help / -h / /?        显示帮助
    ///   --version / -v          显示版本
    /// </summary>
    public class CommandLineOptions
    {
        public bool StartMinimized { get; private set; }
        public bool ShowHelp { get; private set; }
        public bool ShowVersion { get; private set; }

        public static CommandLineOptions Parse(string[] args)
        {
            var options = new CommandLineOptions();
            if (args == null || args.Length == 0) return options;

            foreach (var raw in args)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                switch (raw.Trim().ToLowerInvariant())
                {
                    case "--minimized":
                    case "--min":
                    case "-m":
                        options.StartMinimized = true;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                    case "-?":
                        options.ShowHelp = true;
                        break;
                    case "--version":
                    case "-v":
                        options.ShowVersion = true;
                        break;
                    default:
                        // 未知参数忽略，保持容错
                        break;
                }
            }

            return options;
        }

        public static string BuildHelpText()
        {
            return string.Join(Environment.NewLine, new[]
            {
                "KeepOn - 保持唤醒工具",
                "",
                "用法: KeepOn.exe [选项]",
                "",
                "选项:",
                "  --minimized, --min    启动后仅常驻系统托盘，不弹出设置窗口",
                "  --version, -v         显示版本信息",
                "  --help, -h            显示本帮助",
                "",
                "配置文件: " + Services.Paths.ConfigFile,
                "日志目录: " + Services.Log.LogDirectory
            });
        }
    }
}
