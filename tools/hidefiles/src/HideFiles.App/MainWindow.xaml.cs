using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HideFiles.App.Models;
using HideFiles.App.ViewModels;
using Wpf.Ui.Controls;

namespace HideFiles.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is MainViewModel vm)
        {
            vm.FlushConfig();
        }
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

    private void FileList_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        // 文件列表为只读视图，禁止进入单元格就地编辑模式（避免只读属性引发绑定异常）
        e.Cancel = true;
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is not DependencyObject dep) return;

        // 排除列头、滚动条、复选框、删除按钮等内部操作控件
        if (FindVisualParent<DataGridColumnHeader>(dep) != null) return;
        if (FindVisualParent<ScrollBar>(dep) != null) return;
        if (FindVisualParent<System.Windows.Controls.Button>(dep) != null) return;
        if (FindVisualParent<System.Windows.Controls.CheckBox>(dep) != null) return;

        // 仅在双击数据行时触发在资源管理器中定位
        var row = FindVisualParent<DataGridRow>(dep);
        if (row != null && row.Item is FileItemModel item && DataContext is MainViewModel vm)
        {
            e.Handled = true;
            vm.LocateFile(item);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T match) return match;
            if (element is Visual || element is System.Windows.Media.Media3D.Visual3D)
            {
                element = VisualTreeHelper.GetParent(element);
            }
            else
            {
                element = LogicalTreeHelper.GetParent(element);
            }
        }
        return null;
    }
}
