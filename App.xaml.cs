using System;
using System.Windows;

namespace WpfVideoPet
{
    public partial class App : Application
    {
        // Removed explicit Main() because App.xaml generates an entry point in App.g.cs

        // 开机自启：首次启动时判断是否需要开机自启
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (!AutoStartService.IsEnabled()) // 如果未开启自启
            {
                AutoStartService.Enable(); // 设置开机自启
            }
        }
    }
}
