using System.Windows;
using HideFiles.Core;

namespace HideFiles.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        mainWindow.Show();

        // 如果启动时携带参数（例如右键菜单传入的路径）
        if (e.Args.Length == 0) return;
        if (mainWindow.DataContext is not ViewModels.MainViewModel vm) return;

        var request = ShellActionParser.Parse(e.Args);
        if (request.Paths.Count == 0) return;

        if (!request.IsAutoProcess)
        {
            vm.AddPaths(request.Paths);
            return;
        }

        // 动作 → 模式/方向的映射集中在 ShellActionParser，避免此处再写死模式（2.1 回归的根因）
        if (request.IsObfuscate is bool isObfuscate)
        {
            _ = vm.AutoProcessPathsAsync(request.Paths, request.Mode!.Value, isObfuscate);
        }
        else
        {
            // 新菜单的合并动词：按每个文件的容器状态自动分派"做/还原"
            _ = vm.AutoProcessSmartAsync(request.Paths, request.Mode!.Value);
        }
    }
}
