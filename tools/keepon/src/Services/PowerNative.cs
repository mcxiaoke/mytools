using System;
using System.Runtime.InteropServices;

namespace KeepOn.Services
{
    /// <summary>
    /// 对 Windows 电源相关 Win32 API 的最小封装：
    /// - SetThreadExecutionState：向系统声明"有线程需要保持唤醒"，是 PowerToys Awake 的同一实现路径。
    /// - GetSystemPowerStatus：查询交流/电池供电与电量，用于电池保护策略。
    /// </summary>
    public static class PowerNative
    {
        [Flags]
        public enum ExecutionState : uint
        {
            /// <summary>不改变当前状态（用于清除之前的连续请求）</summary>
            None = 0x00000000,

            /// <summary>系统需要保持可用（阻止系统睡眠）</summary>
            SystemRequired = 0x00000001,

            /// <summary>显示器需要保持开启（阻止息屏）</summary>
            DisplayRequired = 0x00000002,

            /// <summary>用户在场</summary>
            UserPresent = 0x00000004,

            /// <summary>离开模式（可后台继续播放/下载，仅部分硬件支持）</summary>
            AwayModeRequired = 0x00000040,

            /// <summary>持续生效，直到下次调用显式清除</summary>
            Continuous = 0x80000000
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct SystemPowerStatus
        {
            /// <summary>0=电池供电(离线) 1=交流供电(在线) 255=未知</summary>
            public byte ACLineStatus;

            /// <summary>128=无系统电池；1=电量不足；2=低电量；4=充电中；8=充电中且电量低；255=未知</summary>
            public byte BatteryFlag;

            /// <summary>电量百分比 0-100，255=未知</summary>
            public byte BatteryLifePercent;

            public byte Reserved1;

            /// <summary>剩余电量秒数，-1 表示未知</summary>
            public int BatteryLifeTime;

            /// <summary>满电所需/可持续秒数，-1 表示未知</summary>
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

        /// <summary>BatteryFlag 取值：无系统电池（台式机），调用方据此跳过电池保护逻辑</summary>
        private const byte NoSystemBatteryFlag = 128;

        /// <summary>BatteryFlag / BatteryLifePercent 的"未知"哨兵值</summary>
        private const byte UnknownFlag = 255;

        /// <summary>ACLineStatus 取值：1 = 交流供电在线</summary>
        private const byte AcLineOnline = 1;

        /// <summary>BatteryFlag 位：正在充电</summary>
        private const byte BatteryFlagCharging = 0x04;

        /// <summary>BatteryFlag 位：充电中且电量低</summary>
        private const byte BatteryFlagLowCharging = 0x08;

        /// <summary>电量百分比未知时对外返回的哨兵值</summary>
        public const int UnknownBatteryPercent = -1;

        /// <summary>
        /// 读取当前电源状态快照。读取失败或无电池时返回 null。
        /// </summary>
        public static PowerSnapshot? TryGetPowerSnapshot()
        {
            try
            {
                if (!GetSystemPowerStatus(out SystemPowerStatus status))
                {
                    return null;
                }

                bool hasBattery = status.BatteryFlag != NoSystemBatteryFlag
                                  && status.BatteryFlag != UnknownFlag;

                int percent = status.BatteryLifePercent == UnknownFlag
                    ? UnknownBatteryPercent
                    : status.BatteryLifePercent;

                return new PowerSnapshot
                {
                    HasBattery = hasBattery,
                    IsAcOnline = status.ACLineStatus == AcLineOnline,
                    BatteryPercent = percent,
                    IsCharging = (status.BatteryFlag & BatteryFlagLowCharging) == BatteryFlagLowCharging
                                 || (status.BatteryFlag & BatteryFlagCharging) == BatteryFlagCharging
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 应用保持唤醒请求。target 为 null 时表示清除请求、恢复系统默认电源策略。
        /// </summary>
        public static bool Apply(AwakeTargetKind? target)
        {
            ExecutionState flags;
            if (target == null)
            {
                // 仅提交 ES_CONTINUOUS 即清除本线程此前声明的保持唤醒请求
                flags = ExecutionState.Continuous;
            }
            else
            {
                flags = ExecutionState.Continuous | ExecutionState.SystemRequired;
                if (target == AwakeTargetKind.SystemAndDisplay)
                {
                    flags |= ExecutionState.DisplayRequired;
                }
            }

            ExecutionState result = SetThreadExecutionState(flags);
            // 该 API 在失败时返回 0，且 SetLastError 不一定可靠，故以返回值判定
            return result != ExecutionState.None;
        }
    }

    /// <summary>电源请求目标的内部枚举，避免 Services 层反向依赖 Models 层</summary>
    public enum AwakeTargetKind
    {
        SystemOnly = 0,
        SystemAndDisplay = 1
    }

    /// <summary>电源状态只读快照</summary>
    public struct PowerSnapshot
    {
        public bool HasBattery;
        public bool IsAcOnline;
        public int BatteryPercent;
        public bool IsCharging;

        /// <summary>应视为"不应继续保持唤醒"的场景：电池供电或电量低于阈值</summary>
        public bool ShouldSuspend(int thresholdPercent)
        {
            if (!HasBattery) return false;
            if (!IsAcOnline) return true;
            if (BatteryPercent >= 0 && BatteryPercent <= thresholdPercent) return true;
            return false;
        }
    }
}
