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
