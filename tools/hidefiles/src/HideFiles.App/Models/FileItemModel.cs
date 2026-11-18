using CommunityToolkit.Mvvm.ComponentModel;
using HideFiles.Core;

namespace HideFiles.App.Models;

public partial class FileItemModel : ObservableObject
{
    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private long _fileSize;

    [ObservableProperty]
    private FileStatus _status;

    [ObservableProperty]
    private string? _originalFileName;

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
