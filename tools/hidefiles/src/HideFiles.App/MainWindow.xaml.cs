using System.Windows;
using HideFiles.App.ViewModels;
using Wpf.Ui.Controls;

namespace HideFiles.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (paths != null && paths.Length > 0 && DataContext is MainViewModel vm)
            {
                // 根据拖放目标 DataGrid 决定加入哪个工作台（命名元素 > 当前 Tab）
                string mode = ReferenceEquals(sender, SecureFileGrid) ? "secure" : "struct";
                await vm.AddPathsAsync(paths, mode);
            }
        }
    }
}
