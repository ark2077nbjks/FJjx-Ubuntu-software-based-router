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
    // 单实例锁：两个面板同时跑会把流量统计算两遍、日志互相打架
    private static System.Threading.Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 调试用：设 NETPANEL_MULTI=1 可以无视单实例锁（方便一边跑正式版一边测调试版）
        var bypassLock = Environment.GetEnvironmentVariable("NETPANEL_MULTI") == "1";
        _single = new System.Threading.Mutex(true, "Global\\NetPanel_SingleInstance", out var isFirst);
        if (!isFirst && !bypassLock)
        {
            MessageBox.Show("网络面板已经开着了。\n\n如果窗口看不见，它可能缩在右下角托盘里 —— 双击那个蓝色路由器图标就能打开。\n（同一时间只能开一个：不然流量会重复统计，日志也会乱。）",
                "网络面板", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // .NET 默认只有 UTF-8/UTF-16；写中文 bat、读 GBK 输出都要这张表
        try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
        catch (Exception ex) { AppPaths.Log("注册编码表失败: " + ex.Message); }

        AppPaths.Ensure();
        AppPaths.Log("=== NetPanel 启动 ===");

        // 没有 StartupUri，窗口在这里手动开，第二个实例就不会闪一下窗口
        MainWindow = new MainWindow();
        MainWindow.Show();
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
