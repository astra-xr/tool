using System;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows;

[assembly: AssemblyTitle("安诊")]
[assembly: AssemblyProduct("safeTriage")]
[assembly: AssemblyDescription("Windows 故障排查助手")]
[assembly: AssemblyVersion("0.5.0.0")]
[assembly: AssemblyFileVersion("0.5.0.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace SafeTriage
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                return app.Run(new MainWindow().Window);
            }
            catch (Exception error)
            {
                MessageBox.Show("安诊启动失败。\n" + error.Message, "安诊", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
