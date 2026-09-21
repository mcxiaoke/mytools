using System;
using System.Windows.Media;

namespace KeepOn.Views
{
    /// <summary>
    /// 状态徽标的配色。
    ///
    /// 颜色字面量集中在此处，并预先把 Brush 冻结（Freeze）后缓存为静态实例：
    /// - 避免每次状态刷新都重新 new SolidColorBrush 并解析颜色字符串
    /// - 冻结后的 Brush 不可变，可安全跨线程共享，WPF 渲染时也无需再复制
    /// </summary>
    public sealed class BadgeStyle
    {
        /// <summary>挂起 / 需要注意：琥珀色</summary>
        public static readonly BadgeStyle Warning = new BadgeStyle("#FFFEF3C7", "#FF92400E");

        /// <summary>正常工作：绿色</summary>
        public static readonly BadgeStyle Success = new BadgeStyle("#FFDCFCE7", "#FF166534");

        /// <summary>保持唤醒生效中：蓝色</summary>
        public static readonly BadgeStyle Active = new BadgeStyle("#FFDBEAFE", "#FF1E40AF");

        /// <summary>未启用：灰色</summary>
        public static readonly BadgeStyle Neutral = new BadgeStyle("#FFF1F5F9", "#FF475569");

        private BadgeStyle(string background, string foreground)
        {
            Background = CreateFrozenBrush(background);
            Foreground = CreateFrozenBrush(foreground);
        }

        public Brush Background { get; }

        public Brush Foreground { get; }

        private static Brush CreateFrozenBrush(string colorText)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorText));
            brush.Freeze();
            return brush;
        }
    }
}
