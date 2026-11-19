namespace HideFiles.Core;

/// <summary>
/// 产品对外显示名。集中一处，避免窗口标题、右键菜单、关于页、导出文件名各写一遍而漂移。
/// 只影响"给人看的文字"：程序集名（CarroCasket / CarroCasket.exe）、命名空间、
/// 配置目录（%APPDATA%\HideFiles）与注册表键名都保持原样，
/// 以免已装机用户的配置与右键菜单注册失效。
/// </summary>
public static class AppBrand
{
    /// <summary>产品显示名（界面标题、右键菜单、关于页、导出文件名）</summary>
    public const string Name = "Carro Casket";

    /// <summary>中文名（保留但不用）</summary>
    public const string NameZh = "萝卜密匣";

    /// <summary>一句话定位，用来说明"混淆"与"加密"的分工</summary>
    public const string Tagline = "文件混淆与加密";
}
