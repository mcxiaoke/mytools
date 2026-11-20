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

        if (mainWindow.DataContext is not ViewModels.MainViewModel vm) return;

        // 如果启动时携带参数（例如右键菜单传入工作台和路径）
        if (e.Args.Length > 0)
        {
            var request = ShellActionParser.Parse(e.Args);
            vm.SelectedTabIndex = request.TargetTab == "secure" ? 1 : 0;

            if (request.Paths.Count > 0)
            {
                if (!request.IsAutoProcess)
                {
                    vm.AddPaths(request.Paths, request.TargetTab);
                }
                else
                {
                    // 仅保留供外部脚本显式指定自动动作时使用
                    if (request.IsObfuscate is bool isObfuscate)
                    {
                        _ = vm.AutoProcessPathsAsync(request.Paths, request.Mode!.Value, isObfuscate);
                    }
                    else
                    {
                        _ = vm.AutoProcessSmartAsync(request.Paths, request.Mode!.Value);
                    }
                    return;
                }
            }
        }

        // 统一密码验证（双击打开、或右键打开工作台，流程完全相同）：
        // 若已配置密码且未解锁，弹出一次密码对话框要求输入密码；
        // 用户输入成功后自动解锁并嗅探列表中已保护文件的原始名；
        // 用户取消输入后留在界面保持未解锁状态，绝无二次弹窗。
        if (HideFilesConfig.DataDirOverride == null && vm.Config.HasPin && !vm.IsSessionUnlocked)
        {
            Dispatcher.BeginInvoke(() => vm.PromptUnlockSession(),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}
