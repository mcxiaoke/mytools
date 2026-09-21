using System;
using System.Threading;
using System.Windows;
using KeepOn.Services;

namespace KeepOn
{
    /// <summary>
    /// 应用入口。KeepOn 是托盘常驻型程序，ShutdownMode 设为 OnExplicitShutdown，
    /// 由托盘"退出"菜单显式结束进程。
    /// </summary>
    public class Program
    {
        private const string SingleInstanceMutexName = @"Global\KeepOn.SingleInstance.9F2A7C41";

        private static Mutex _instanceMutex;
        private static App _app;

        [STAThread]
        public static void Main(string[] args)
        {
            // 单实例保护：重复启动时提示并退出，避免多份程序争抢电源请求与热键
            bool createdNew;
            _instanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);

            if (!createdNew)
            {
                MessageBox.Show(
                    "KeepOn 已在运行中。\n请在任务栏通知区域（系统托盘）查看已运行的实例。",
                    "KeepOn",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                var options = CommandLineOptions.Parse(args);

                _app = new App(options);
                _app.Run();
            }
            catch (Exception ex)
            {
                Log.Error("App", "应用发生未处理异常", ex);
                MessageBox.Show(
                    "KeepOn 启动失败：\n" + ex.Message,
                    "KeepOn",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                try
                {
                    if (_instanceMutex != null)
                    {
                        _instanceMutex.ReleaseMutex();
                        _instanceMutex.Dispose();
                    }
                }
                catch
                {
                    // 释放阶段忽略异常
                }
            }
        }
    }
}
