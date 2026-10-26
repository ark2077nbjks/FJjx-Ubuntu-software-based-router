using System.Windows;
using NetPanel.Services;

namespace NetPanel;

public partial class DeployWindow : Window
{
    private CancellationTokenSource? _cts;
    private bool _running;

    public DeployWindow(string host, string user)
    {
        InitializeComponent();
        TxtHost.Text = host;
        TxtUser.Text = string.IsNullOrWhiteSpace(user) ? "" : user;
        TxtSsid.Text = "Gray-Router";
        TxtLanIp.Text = "192.168.50.1";
        CmbTopo.SelectedIndex = 0;
    }

    private void Log(string s)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(s)); return; }
        TxtLog.AppendText($"{DateTime.Now:HH:mm:ss}  {s}{Environment.NewLine}");
        try { AppPaths.Log("[部署] " + s); } catch { }
        TxtLog.ScrollToEnd();
    }

    private void Step(int i, int n, string title)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Step(i, n, title)); return; }
        PbStep.Maximum = n;
        PbStep.Value = Math.Min(i, n);
        TxtStep.Text = $"第 {i}/{n} 步 · {title}";
        TxtPct.Text = $"{i * 100 / Math.Max(n, 1)}%";
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        var o = new DeployOptions
        {
            Host = TxtHost.Text.Trim(),
            User = TxtUser.Text.Trim(),
            SshPassword = PwSsh.Password,
            SudoPassword = PwSudo.Password,
            Topology = (CmbTopo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "A",
            Ssid = TxtSsid.Text.Trim(),
            WifiPass = PwWifi.Password,
            LanIp = TxtLanIp.Text.Trim(),
            DryRun = ChkDry.IsChecked == true,
        };

        var bad = o.Validate();
        if (bad != "") { Log("× " + bad); return; }

        _running = true;
        BtnStart.IsEnabled = false;
        BtnClose.IsEnabled = false;
        PbStep.Value = 0;
        TxtLog.Clear();
        Log($"开始部署 → {o.User}@{o.Host}" + (o.DryRun ? "（演练模式）" : ""));
        _cts = new CancellationTokenSource();

        try
        {
            var dep = new SshDeploy(Log, Step);
            var res = await Task.Run(() => dep.RunAsync(o, _cts.Token));

            Log("");
            if (res.Checks.Count > 0)
            {
                var ok = res.Checks.Count(c => c.Ok);
                Log($"自检结果：{ok}/{res.Checks.Count} 通过");
                foreach (var c in res.Checks) Log($"   {(c.Ok ? "√" : "×")} {c.Name}  {c.Detail}");
            }
            Log((res.Ok ? "√ " : "× ") + res.Message);

            if (res.Ok)
            {
                PbStep.Value = PbStep.Maximum;
                TxtStep.Text = "部署完成";
                TxtPct.Text = "100%";
                TxtStep.Foreground = System.Windows.Media.Brushes.SeaGreen;
            }
            else
            {
                TxtStep.Text = "没完全成功";
                TxtStep.Foreground = System.Windows.Media.Brushes.IndianRed;
            }
        }
        catch (Exception ex)
        {
            Log("× 出错了：" + ex.Message);
        }
        finally
        {
            _running = false;
            BtnStart.IsEnabled = true;
            BtnClose.IsEnabled = true;
            PwSsh.Clear();
            PwSudo.Clear();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            var r = MessageBox.Show("还在部署，现在关掉会中断（远端可能停在半装状态）。\n确定要关吗？",
                "正在部署", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
            _cts?.Cancel();
        }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
