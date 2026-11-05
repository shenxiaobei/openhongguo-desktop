using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Hongguo.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 只记录错误类型，防止媒体地址或播放参数意外进入日志。
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openhongguo");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "native-errors.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e.Exception.GetType().Name}{Environment.NewLine}");
        }
        catch
        {
        }
        System.Windows.MessageBox.Show("本次操作没有完成，请重试。若窗口尚未打开，请重新启动应用。", "红果原生桌面版", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
