using System;
using System.Diagnostics;
using System.Windows;

namespace WpfVideoPet
{
    public partial class App : Application
    {
        // Removed explicit Main() because App.xaml generates an entry point in App.g.cs

        /// <summary>
        /// 在应用启动时执行单实例检测，根据用户选择决定是否接管旧进程，并在首启时自动设置开机自启。
        /// </summary>
        /// <param name="e">启动事件参数。</param>
        protected override void OnStartup(StartupEventArgs e)
        {
            var currentProcess = Process.GetCurrentProcess(); // 当前进程信息
            var similarProcesses = Process.GetProcessesByName(currentProcess.ProcessName); // 同名进程列表
            Process? existingInstance = null; // 已存在的旧实例

            foreach (var process in similarProcesses)
            {
                if (process.Id == currentProcess.Id)
                {
                    process.Dispose();
                    continue;
                }

                if (existingInstance == null)
                {
                    existingInstance = process;
                }
                else
                {
                    process.Dispose();
                }
            }

            if (existingInstance != null)
            {
                AppLogger.Warn("检测到应用已在运行，将阻止重复启动。");
                var result = MessageBox.Show(
                    "检测到程序已在运行。\n\n点击“是”关闭旧进程并启动当前实例，点击“否”保留旧进程并退出。",
                    "提示",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No); // 用户选择

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        AppLogger.Info($"用户选择关闭旧进程 (PID={existingInstance.Id}) 并继续启动新实例。");

                        if (!existingInstance.CloseMainWindow())
                        {
                            AppLogger.Warn("旧进程未响应关闭窗口请求，尝试强制结束。");
                            existingInstance.Kill(true);
                        }
                        else if (!existingInstance.WaitForExit(5000))
                        {
                            AppLogger.Warn("旧进程在 5 秒内未退出，尝试强制结束。");
                            existingInstance.Kill(true);
                        }

                        existingInstance.WaitForExit();
                        AppLogger.Info("旧进程已成功退出，继续启动当前实例。");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error(ex, "终止旧进程失败");
                        MessageBox.Show("关闭旧进程失败，当前实例将退出。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        existingInstance.Dispose();
                        Shutdown();
                        return;
                    }

                    existingInstance.Dispose();
                }
                else
                {
                    AppLogger.Info("用户选择保留旧实例，当前进程将退出。");
                    existingInstance.Dispose();
                    Shutdown();
                    return;
                }
            }

            base.OnStartup(e);

            if (!AutoStartService.IsEnabled()) // 如果未开启自启
            {
                AutoStartService.Enable(); // 设置开机自启
            }
        }
    }
}