using System;

namespace KeepOn.Models
{
    /// <summary>
    /// 保持唤醒工作模式，语义对齐 PowerToys Awake 的三档模式。
    /// </summary>
    public enum AwakeMode
    {
        /// <summary>被动/关闭：遵循系统默认电源策略</summary>
        Passive = 0,

        /// <summary>无限期保持唤醒（直到手动关闭）</summary>
        Indefinite = 1,

        /// <summary>定时保持唤醒（倒计时模式）</summary>
        Timed = 2,

        /// <summary>保持唤醒至指定时刻</summary>
        UntilTime = 3
    }

    /// <summary>
    /// 保持唤醒的电源请求目标，对齐 PowerToys Awake 的"保持什么"选项。
    /// </summary>
    public enum AwakeTarget
    {
        /// <summary>仅保持系统唤醒（允许显示器熄灭）</summary>
        SystemOnly = 0,

        /// <summary>同时保持系统和显示器常亮</summary>
        SystemAndDisplay = 1
    }
}
