using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Threading;
using KeepOn.Models;
using KeepOn.Services;

namespace KeepOn.Cli
{
    /// <summary>
    /// 「保持唤醒至指定时刻」的边界行为验证，重点覆盖 00:00 与跨日场景。
    ///
    /// 由于 SetUntilTime 内部使用 DateTime.Now，本工具通过反射把控制器的
    /// 到期时间读回来核对计算结果，从而验证"已过则顺延次日"的判定是否正确。
    /// </summary>
    internal static class UntilTimeDiagnostics
    {
        public static int Run()
        {
            Console.WriteLine("=== 「保持唤醒至指定时刻」边界行为验证 ===");
            Console.WriteLine("当前时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Console.WriteLine();

            int failures = 0;

            // ---- 第一部分：时刻解析与跨日判定 ----
            var dispatcher = Dispatcher.CurrentDispatcher;
            var controller = new AwakeController(dispatcher);
            controller.Initialize(AppConfig.CreateDefault());

            string[] cases = { "00:00", "00:01", "18:00", "20:00", "22:00", "23:59" };

            foreach (string hhmm in cases)
            {
                bool ok = controller.SetUntilTime(hhmm);
                DateTime expire = controller.ExpireTime;
                TimeSpan remaining = expire - DateTime.Now;

                string verdict = Judge(hhmm, expire, remaining);
                if (verdict != "OK") failures++;

                Console.WriteLine(string.Format(
                    "  {0,-6} -> 到期 {1:yyyy-MM-dd HH:mm}  剩余 {2,8:0.0} 分钟  [{3}]",
                    hhmm, expire, remaining.TotalMinutes, verdict));
            }

            Console.WriteLine();
            Console.WriteLine("说明: 期望语义是「取今天的该时刻；若已过则顺延到次日」，");
            Console.WriteLine("      且 00:00 应解释为次日零点（而非当天已过去的零点）。");

            // ---- 第二部分：上次时刻的持久化与恢复 ----
            Console.WriteLine();
            Console.WriteLine("--- 上次时刻持久化（ResolveUntilTime）---");

            failures += VerifyPersistedUntilTime();

            Console.WriteLine();
            if (failures == 0)
            {
                Console.WriteLine("结论: 全部检查通过，无跨日判断与恢复逻辑缺陷。");
                return 0;
            }

            Console.WriteLine("结论: 有 " + failures + " 项不符合预期。");
            return 1;
        }

        /// <summary>
        /// 验证「上次使用的时刻」的保存与恢复：
        /// 保存 00:00 后应原样恢复 00:00，而不是回退到预设首项 18:00。
        /// </summary>
        private static int VerifyPersistedUntilTime()
        {
            int failures = 0;

            // 场景 1：保存过 00:00，应恢复 00:00
            var saved = AppConfig.CreateDefault();
            saved.LastUntilTime = "00:00";
            saved.Sanitize();
            string restored = saved.ResolveUntilTime();
            bool ok1 = restored == "00:00";
            if (!ok1) failures++;
            Console.WriteLine(string.Format(
                "  LastUntilTime=00:00      -> 恢复 {0,-6} [{1}]",
                restored, ok1 ? "OK" : "错误: 未保留上次时刻"));

            // 场景 2：从未保存过，应回退到预设首项
            var empty = AppConfig.CreateDefault();
            empty.LastUntilTime = null;
            empty.Sanitize();
            string fallback = empty.ResolveUntilTime();
            bool ok2 = fallback == empty.UntilTimePresets[0];
            if (!ok2) failures++;
            Console.WriteLine(string.Format(
                "  LastUntilTime=null       -> 恢复 {0,-6} [{1}]",
                fallback, ok2 ? "OK" : "错误: 未回退到预设首项"));

            // 场景 3：脏数据应被清空并回退到预设首项
            var dirty = AppConfig.CreateDefault();
            dirty.LastUntilTime = "25:99";
            dirty.Sanitize();
            string sanitized = dirty.ResolveUntilTime();
            bool ok3 = sanitized == dirty.UntilTimePresets[0];
            if (!ok3) failures++;
            Console.WriteLine(string.Format(
                "  LastUntilTime=25:99(脏)  -> 恢复 {0,-6} [{1}]",
                sanitized, ok3 ? "OK" : "错误: 脏数据未被清理"));

            // 场景 4：非整点时刻应被规范化为 HH:mm
            var odd = AppConfig.CreateDefault();
            odd.LastUntilTime = "8:5";
            odd.Sanitize();
            string normalized = odd.ResolveUntilTime();
            bool ok4 = normalized == "08:05";
            if (!ok4) failures++;
            Console.WriteLine(string.Format(
                "  LastUntilTime=8:5        -> 恢复 {0,-6} [{1}]",
                normalized, ok4 ? "OK" : "错误: 未规范化为 HH:mm"));

            return failures;
        }

        /// <summary>
        /// 判定某个时刻的结果是否合理：
        /// - 到期时间必须晚于当前时间（否则会立刻触发 Expired）
        /// - 顺延幅度不应超过 24 小时
        /// - 若目标时刻在今天尚未到达，则必须是今天
        /// </summary>
        private static string Judge(string hhmm, DateTime expire, TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero)
            {
                return "错误: 到期时间不晚于当前时间，会立即失效";
            }

            if (remaining > TimeSpan.FromHours(24))
            {
                return "错误: 顺延超过 24 小时";
            }

            TimeSpan ts = TimeSpan.Parse(hhmm);
            DateTime todayTarget = DateTime.Today.Add(ts);
            bool stillAheadToday = todayTarget > DateTime.Now;

            if (stillAheadToday && expire.Date != DateTime.Today)
            {
                return "错误: 今日该时刻尚未到达，却顺延到了次日";
            }

            if (!stillAheadToday && expire.Date != DateTime.Today.AddDays(1))
            {
                return "错误: 今日该时刻已过，却未顺延到次日";
            }

            return "OK";
        }
    }
}
