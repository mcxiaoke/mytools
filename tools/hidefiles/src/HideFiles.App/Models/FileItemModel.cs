using CommunityToolkit.Mvvm.ComponentModel;
using HideFiles.Core;

namespace HideFiles.App.Models;

public partial class FileItemModel : ObservableObject
{
    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayOriginalFileName))]
    [NotifyPropertyChangedFor(nameof(DisplayEncryptedFileName))]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private long _fileSize;

    [ObservableProperty]
    private FileStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayOriginalFileName))]
    private string? _originalFileName;

    /// <summary>
    /// 加密/混淆后的文件名。
    /// 未加密时为预测混淆名（如 hf_xxxx.dat）；已加密时为实际密文文件名。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayEncryptedFileName))]
    private string? _encryptedFileName;

    /// <summary>用于界面展示的原始文件名：未加密文件直接为原名；已加密文件为解密后的原始名</summary>
    public string DisplayOriginalFileName =>
        !string.IsNullOrEmpty(OriginalFileName)
            ? OriginalFileName
            : (Status == FileStatus.Normal ? FileName : "(未解锁)");

    /// <summary>用于界面展示的加密文件名：未加密文件为预测混淆名；已加密文件为磁盘密文名</summary>
    public string DisplayEncryptedFileName =>
        !string.IsNullOrEmpty(EncryptedFileName)
            ? EncryptedFileName
            : (Status == FileStatus.Obfuscated ? FileName : "--");

    [ObservableProperty]
    private string? _statusText;

    /// <summary>本条文件最近一次处理失败的原因；成功、或仅"格式不支持"时清空/不设置</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    /// <summary>是否有需要排查的错误提示（含"不支持"的说明文案，用于 tooltip）</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// 是否为真正的处理失败。"不支持"属于模式不匹配而非失败，
    /// 列表里应当显示灰色状态而不是红色告警。
    /// </summary>
    public bool IsFailed => Status != FileStatus.Unsupported && HasError;

    partial void OnStatusChanged(FileStatus value)
    {
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(DisplayOriginalFileName));
        OnPropertyChanged(nameof(DisplayEncryptedFileName));
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(IsFailed));
    }

    [ObservableProperty]
    private bool _isSelected = true;

    public string DisplaySize
    {
        get
        {
            if (FileSize >= 1024 * 1024) return $"{(double)FileSize / 1024 / 1024:F2} MB";
            if (FileSize >= 1024) return $"{(double)FileSize / 1024:F1} KB";
            return $"{FileSize} B";
        }
    }
}
