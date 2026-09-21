using System;
using System.Collections.Generic;

namespace KeepOn.Models
{
    /// <summary>
    /// 配置项的默认值与取值边界。
    ///
    /// 这里集中定义"唯一权威来源"，避免同一个数字散落在模型、服务与界面三处
    /// 各自维护——那样改一处默认时长就要同步改四五个地方，极易漏改。
    /// </summary>
    public static class ConfigDefaults
    {
        #region 定时时长（分钟）

        /// <summary>定时保持唤醒的默认时长（分钟）</summary>
        public const int DurationMinutes = 30;

        /// <summary>定时时长下限（分钟）</summary>
        public const int MinDurationMinutes = 1;

        /// <summary>定时时长上限（分钟），即 24 小时</summary>
        public const int MaxDurationMinutes = 24 * 60;

        /// <summary>托盘"定时保持唤醒"菜单的默认预设时长（分钟）</summary>
        public static readonly IReadOnlyList<int> TimerPresets =
            new[] { 60, 120, 240, 480, 600, 720, 1440 };

        #endregion

        #region 保持至指定时刻

        /// <summary>托盘"保持唤醒至指定时刻"菜单的默认预设时刻（HH:mm）</summary>
        public static readonly IReadOnlyList<string> UntilTimePresets =
            new[] { "18:00", "20:00", "22:00", "00:00" };

        /// <summary>一天的分钟数，用于时刻文本的取值范围校验</summary>
        public const int MinutesPerDay = 24 * 60;

        #endregion

        #region 电池保护

        /// <summary>低电量保护阈值的默认值（百分比）</summary>
        public const int BatteryThresholdPercent = 20;

        /// <summary>低电量阈值下限（百分比）</summary>
        public const int MinBatteryThresholdPercent = 5;

        /// <summary>低电量阈值上限（百分比）</summary>
        public const int MaxBatteryThresholdPercent = 95;

        #endregion

        #region 进程联动

        /// <summary>目标进程退出后延迟恢复的默认缓冲秒数</summary>
        public const int ProcessExitDelaySeconds = 120;

        /// <summary>退出缓冲秒数下限</summary>
        public const int MinProcessExitDelaySeconds = 0;

        /// <summary>退出缓冲秒数上限（1 小时）</summary>
        public const int MaxProcessExitDelaySeconds = 3600;

        #endregion

        #region 其他

        /// <summary>全局快速切换热键的默认值</summary>
        public const string ToggleHotkey = "Win+Shift+W";

        /// <summary>当前配置结构版本</summary>
        public const int ConfigVersion = 1;

        #endregion

        /// <summary>把分钟数收敛到合法区间；小于下限时回退为默认时长而非下限</summary>
        public static int ClampDurationMinutes(int minutes)
        {
            // 注意：非法（<=0）时回退为默认时长，而非抬到下限 1 分钟——
            // 这保留了原有语义，避免用户误填 0 时被静默改成 1 分钟。
            if (minutes < MinDurationMinutes) return DurationMinutes;
            if (minutes > MaxDurationMinutes) return MaxDurationMinutes;
            return minutes;
        }

        /// <summary>把电池阈值收敛到合法区间</summary>
        public static int ClampBatteryThreshold(int percent)
        {
            if (percent < MinBatteryThresholdPercent) return MinBatteryThresholdPercent;
            if (percent > MaxBatteryThresholdPercent) return MaxBatteryThresholdPercent;
            return percent;
        }

        /// <summary>把进程退出缓冲秒数收敛到合法区间</summary>
        public static int ClampProcessExitDelaySeconds(int seconds)
        {
            if (seconds < MinProcessExitDelaySeconds) return MinProcessExitDelaySeconds;
            if (seconds > MaxProcessExitDelaySeconds) return MaxProcessExitDelaySeconds;
            return seconds;
        }

        /// <summary>判断分钟数是否落在合法的定时时长区间内</summary>
        public static bool IsValidDurationMinutes(int minutes)
        {
            return minutes >= MinDurationMinutes && minutes <= MaxDurationMinutes;
        }
    }
}
