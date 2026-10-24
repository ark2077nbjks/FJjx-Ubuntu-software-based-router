using System.Windows;
using System.Windows.Threading;
using NetPanel.Services;

namespace NetPanel;

static class Ver
{
    public static string Text()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "?" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }
}

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.Ensure();
        AppPaths.Log("=== NetPanel 启动 ===");
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppPaths.Log("未处理异常(域): " + args.ExceptionObject);
    }

    private void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppPaths.Log("未处理异常(UI): " + e.Exception);
        MessageBox.Show("出错了，详情已写入日志：\n" + AppPaths.LogFile + "\n\n" + e.Exception.Message,
            "网络面板", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
