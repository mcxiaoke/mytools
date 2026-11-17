using System.Windows;

namespace HideFiles.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        mainWindow.Show();

        // 如果启动时携带参数（例如右键菜单传入的路径）
        if (e.Args.Length > 0)
        {
            var vm = mainWindow.DataContext as ViewModels.MainViewModel;
            if (vm != null)
            {
                var paths = new List<string>();
                string? action = null;

                for (int i = 0; i < e.Args.Length; i++)
                {
                    string arg = e.Args[i];
                    if (arg is "--hide" or "-hide")
                    {
                        action = "hide";
                    }
                    else if (arg is "--unhide" or "-unhide")
                    {
                        action = "unhide";
                    }
                    else if (arg is "--open" or "-open")
                    {
                        action = "open";
                    }
                    else if (!arg.StartsWith("-"))
                    {
                        paths.Add(arg);
                    }
                }

                if (paths.Count > 0)
                {
                    if (action == "hide")
                    {
                        _ = vm.AutoProcessPathsAsync(paths, isObfuscate: true);
                    }
                    else if (action == "unhide")
                    {
                        _ = vm.AutoProcessPathsAsync(paths, isObfuscate: false);
                    }
                    else
                    {
                        vm.AddPaths(paths);
                    }
                }
            }
        }
    }
}
