using System;
using KeepOn.Models;

namespace KeepOn.Services
{
    /// <summary>
    /// 把 AwakeController 的状态翻译为托盘标题、气泡提示等用户可见文本。
    /// 集中在一处，便于界面与托盘复用同一套措辞。
    /// </summary>
    public static class StatusFormatter
    {
        public const string AppName = "KeepOn";

        /// <summary>一小时的分钟数，用于时长的时/分拆分</summary>
        private const int MinutesPerHour = 60;

        /// <summary>托盘菜单根节点文本，如 "保持唤醒 (永久)"</summary>
        public static string BuildHeader(AwakeController controller)
        {
            const string baseTitle = "保持唤醒";
            if (controller == null) return baseTitle;

            return baseTitle + " (" + BuildShortStatus(controller) + ")";
        }

        /// <summary>简短状态词，用于托盘标题与气泡</summary>
        public static string BuildShortStatus(AwakeController controller)
        {
            if (controller == null) return "已关闭";

            if (controller.IsBatteryPaused) return "电池暂停";
            if (controller.IsProcessTriggered) return "进程联动";

            switch (controller.Mode)
            {
                case AwakeMode.Indefinite:
                    return "永久";
                case AwakeMode.Timed:
                case AwakeMode.UntilTime:
                    return FormatRemaining(controller.RemainingTime);
                default:
                    return "已关闭";
            }
        }

        /// <summary>托盘悬停提示全文</summary>
        public static string BuildTooltip(AwakeController controller)
        {
            if (controller == null) return AppName + " - 保持唤醒";

            string modeDesc;
            if (controller.IsBatteryPaused)
            {
                modeDesc = "已因电池供电挂起保持唤醒";
            }
            else if (controller.IsProcessTriggered)
            {
                modeDesc = "进程 '" + controller.ActiveProcessTrigger + "' 联动保持唤醒中";
            }
            else
            {
                switch (controller.Mode)
                {
                    case AwakeMode.Indefinite:
                        modeDesc = "无限期保持唤醒";
                        break;
                    case AwakeMode.Timed:
                        modeDesc = "定时保持唤醒（剩余 " + FormatRemaining(controller.RemainingTime) +
                                   "，到期 " + controller.ExpireTime.ToString("HH:mm") + "）";
                        break;
                    case AwakeMode.UntilTime:
                        modeDesc = "保持唤醒至 " + controller.ExpireTime.ToString("HH:mm") +
                                   "（剩余 " + FormatRemaining(controller.RemainingTime) + "）";
                        break;
                    default:
                        modeDesc = "遵循系统默认电源策略（已关闭）";
                        break;
                }
            }

            string displayDesc = controller.KeepDisplayOn ? "保持屏幕常亮" : "允许屏幕熄灭";
            return AppName + " - " + modeDesc + " [" + displayDesc + "]";
        }

        /// <summary>设置窗口顶部的状态徽标文本</summary>
        public static string BuildBadgeText(AwakeController controller)
        {
            if (controller == null) return "已关闭";

            if (controller.IsBatteryPaused) return "已暂停（电池供电）";
            if (controller.IsProcessTriggered) return "进程唤醒中（" + controller.ActiveProcessTrigger + "）";

            switch (controller.Mode)
            {
                case AwakeMode.Indefinite:
                    return "无限期保持唤醒";
                case AwakeMode.Timed:
                    return "倒计时中（剩余 " + FormatRemaining(controller.RemainingTime) + "）";
                case AwakeMode.UntilTime:
                    return "保持至 " + controller.ExpireTime.ToString("HH:mm") +
                           "（剩余 " + FormatRemaining(controller.RemainingTime) + "）";
                default:
                    return "已关闭（跟随系统）";
            }
        }

        /// <summary>把时长格式化为 "2h05m" 或 "18m"</summary>
        public static string FormatRemaining(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero) return "0m";

            if (remaining.TotalHours >= 1)
            {
                return string.Format("{0}h{1:D2}m", (int)remaining.TotalHours, remaining.Minutes);
            }

            return Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)) + "m";
        }

        /// <summary>把分钟数格式化为菜单标签，如 "30 分钟" / "2 小时" / "1 小时 30 分"</summary>
        public static string FormatMinutes(int minutes)
        {
            if (minutes <= 0) return "0 分钟";
            if (minutes < MinutesPerHour) return minutes + " 分钟";
            if (minutes % MinutesPerHour == 0) return (minutes / MinutesPerHour) + " 小时";

            int hours = minutes / MinutesPerHour;
            int mins = minutes % MinutesPerHour;
            return hours + " 小时 " + mins + " 分";
        }
    }
}
