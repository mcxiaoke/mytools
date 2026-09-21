using System;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using KeepOn.Models;
using KeepOn.Services;

namespace KeepOn.Cli
{
    /// <summary>
    /// KeepOn 的无界面验证工具。
    ///
    /// 用途：在无法交互操作托盘界面的环境下，验证核心电源控制逻辑是否真正生效。
    /// 它复用与 GUI 完全相同的 AwakeController / PowerNative / ProcessHelper 源码，
    /// 因此结论对主程序同样成立。
    ///
    /// 用法:
    ///   keepon-cli.exe status              读取当前电源状态快照
    ///   keepon-cli.exe hold [秒数]         保持唤醒指定秒数后自动释放（默认 5 秒）
    ///   keepon-cli.exe display [秒数]      保持系统+显示器常亮指定秒数
    ///   keepon-cli.exe process &lt;名称&gt;    检测进程是否运行（验证进程联动判定）
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            string command = args != null && args.Length > 0
                ? args[0].Trim().ToLowerInvariant()
                : "status";

            try
            {
                switch (command)
                {
                    case "status":
                        return RunStatus();
                    case "hold":
                        return RunHold(ParseSeconds(args, 5), AwakeTargetKind.SystemOnly);
                    case "display":
                        return RunHold(ParseSeconds(args, 5), AwakeTargetKind.SystemAndDisplay);
                    case "process":
                        return RunProcessCheck(args);
                    case "verify":
                        return RunVerify();
                    case "untiltime":
                        return UntilTimeDiagnostics.Run();
                    default:
                        PrintUsage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("执行失败: " + ex.Message);
                return 1;
            }
        }

        private static int ParseSeconds(string[] args, int fallback)
        {
            if (args != null && args.Length > 1 && int.TryParse(args[1], out int seconds))
            {
                return Math.Max(1, Math.Min(3600, seconds));
            }
            return fallback;
        }

        /// <summary>打印当前电源状态快照，便于人工确认程序是否真的阻止了睡眠</summary>
        private static int RunStatus()
        {
            Console.WriteLine("=== KeepOn 电源状态 ===");

            PowerSnapshot? snapshot = PowerNative.TryGetPowerSnapshot();
            if (snapshot == null)
            {
                Console.WriteLine("电源状态: 读取失败");
            }
            else
            {
                Console.WriteLine("交流供电: " + (snapshot.Value.IsAcOnline ? "是" : "否（电池供电）"));
                Console.WriteLine("存在电池: " + (snapshot.Value.HasBattery ? "是" : "否（台式机）"));
                Console.WriteLine("电池电量: " + (snapshot.Value.BatteryPercent >= 0
                    ? snapshot.Value.BatteryPercent + "%"
                    : "未知"));
                Console.WriteLine("正在充电: " + (snapshot.Value.IsCharging ? "是" : "否"));
            }

            Console.WriteLine();
            Console.WriteLine("当前进程是否持有保持唤醒请求，可用以下命令在另一窗口核对:");
            Console.WriteLine("  powercfg /requests");
            return 0;
        }

        /// <summary>
        /// 真正调用 SetThreadExecutionState 保持唤醒若干秒，然后释放。
        /// 这是对 PowerNative + AwakeController 端到端路径的实测。
        /// </summary>
        private static int RunHold(int seconds, AwakeTargetKind kind)
        {
            Console.WriteLine("=== KeepOn 保持唤醒实测 ===");
            Console.WriteLine("目标: " + (kind == AwakeTargetKind.SystemAndDisplay ? "系统 + 显示器" : "仅系统"));
            Console.WriteLine("时长: " + seconds + " 秒");
            Console.WriteLine();

            bool applied = PowerNative.Apply(kind);
            Console.WriteLine("SetThreadExecutionState 调用: " + (applied ? "成功" : "失败"));

            if (!applied)
            {
                Console.WriteLine("保持唤醒请求未能生效，请检查系统权限或 API 可用性。");
                return 1;
            }

            Console.WriteLine("已进入保持唤醒状态，现在可在另一窗口运行 `powercfg /requests` 核对。");
            Console.WriteLine("倒计时中...");

            for (int remaining = seconds; remaining > 0; remaining--)
            {
                Console.Write("\r剩余 " + remaining + " 秒  ");
                Thread.Sleep(1000);
            }
            Console.WriteLine();

            bool released = PowerNative.Apply(null);
            Console.WriteLine("释放保持唤醒请求: " + (released ? "成功" : "失败"));
            Console.WriteLine("已恢复系统默认电源策略。");

            return released ? 0 : 1;
        }

        /// <summary>验证进程联动判定逻辑（与主程序共用 ProcessHelper）</summary>
        private static int RunProcessCheck(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法: keepon-cli.exe process <进程名>");
                return 2;
            }

            string raw = args[1];
            string normalized = ProcessHelper.Normalize(raw);

            Console.WriteLine("输入: " + raw);
            Console.WriteLine("归一化: " + (normalized ?? "(无效)"));
            Console.WriteLine("纯名称: " + (ProcessHelper.NormalizeNameOnly(raw) ?? "(无效)"));

            bool running = ProcessHelper.IsProcessRunning(raw);
            Console.WriteLine("当前是否运行: " + (running ? "是" : "否"));

            string first = ProcessHelper.FindFirstRunning(new[] { raw });
            Console.WriteLine("FindFirstRunning 结果: " + (first ?? "(未命中)"));

            return running ? 0 : 1;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("KeepOn CLI - 保持唤醒核心逻辑验证工具");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  keepon-cli.exe status            读取当前电源状态快照");
            Console.WriteLine("  keepon-cli.exe hold [秒数]       保持系统唤醒指定秒数（默认 5）");
            Console.WriteLine("  keepon-cli.exe display [秒数]    保持系统+显示器常亮指定秒数");
            Console.WriteLine("  keepon-cli.exe process <名称>    检测进程是否运行");
            Console.WriteLine("  keepon-cli.exe verify            完整验证：应用→读取电源位→释放→再读取");
            Console.WriteLine("  keepon-cli.exe untiltime         验证「保持至指定时刻」的跨日与 00:00 边界");
        }

        /// <summary>
        /// 端到端验证：应用请求后读取系统电源位，释放后再读取，
        /// 用系统真实返回值证明 SetThreadExecutionState 生效，而非仅凭 API 返回值。
        /// </summary>
        private static int RunVerify()
        {
            Console.WriteLine("=== KeepOn 保持唤醒端到端验证 ===");
            Console.WriteLine();

            uint? before = ExecutionStateProbe.Read();
            Console.WriteLine("[1] 应用前的电源请求位: " +
                (before.HasValue ? ExecutionStateProbe.Describe(before.Value) : "读取失败"));

            bool applied = PowerNative.Apply(AwakeTargetKind.SystemAndDisplay);
            Console.WriteLine("[2] SetThreadExecutionState(系统+显示器) 调用: " + (applied ? "成功" : "失败"));

            uint? during = ExecutionStateProbe.Read();
            Console.WriteLine("[3] 应用后的电源请求位: " +
                (during.HasValue ? ExecutionStateProbe.Describe(during.Value) : "读取失败"));

            bool released = PowerNative.Apply(null);
            Console.WriteLine("[4] 释放请求调用: " + (released ? "成功" : "失败"));

            uint? after = ExecutionStateProbe.Read();
            Console.WriteLine("[5] 释放后的电源请求位: " +
                (after.HasValue ? ExecutionStateProbe.Describe(after.Value) : "读取失败"));

            Console.WriteLine();

            const uint systemRequired = 0x00000001;
            const uint displayRequired = 0x00000002;

            bool heldCorrectly = during.HasValue
                                 && (during.Value & systemRequired) != 0
                                 && (during.Value & displayRequired) != 0;

            bool releasedCorrectly = after.HasValue
                                     && (after.Value & systemRequired) == 0
                                     && (after.Value & displayRequired) == 0;

            Console.WriteLine("结论:");
            Console.WriteLine("  保持阶段生效: " + (heldCorrectly ? "通过（系统与显示器请求均已置位）" : "未通过"));
            Console.WriteLine("  释放阶段生效: " + (releasedCorrectly ? "通过（请求位已清零）" : "未通过"));

            bool ok = applied && released && heldCorrectly && releasedCorrectly;
            Console.WriteLine();
            Console.WriteLine(ok
                ? "整体验证: 通过 —— 保持唤醒功能可正常工作。"
                : "整体验证: 未通过 —— 请检查上方各项输出。");

            return ok ? 0 : 1;
        }
    }
}
