using System.Windows;

namespace HideFiles.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        mainWindow.Show();

        // 如果启动时携带参数（例如右键菜单 --open 传入的路径）
        if (e.Args.Length > 0)
        {
            var vm = mainWindow.DataContext as ViewModels.MainViewModel;
            var paths = new List<string>();
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--open" && i + 1 < e.Args.Length)
                {
                    paths.Add(e.Args[++i]);
                }
                else if (!e.Args[i].StartsWith("--"))
                {
                    paths.Add(e.Args[i]);
                }
            }
            if (paths.Count > 0 && vm != null)
            {
                vm.AddPaths(paths);
            }
        }
    }
}
