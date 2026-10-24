using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetPanel.Services;
using Microsoft.Win32;
using System.IO.Compression;

namespace NetPanel;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(60) };
    // 网卡热插拔轮询（8 秒一次，只查名字，很轻）
    private readonly DispatcherTimer _nicPoll = new() { Interval = TimeSpan.FromSeconds(8) };
    private HashSet<string>? _knownNics;

    // ---- 托盘常驻 ----
    private AppSettings _cfg = AppSettings.Load();
    private TrayIcon? _tray;
    private bool _reallyExit;
    private bool? _lastMiniOnline;
    private DateTime? _offlineSince;   // 远端从什么时刻开始不可达（用于算离线时长）
    private bool _nicPolling;
    private bool _busy;

    // ---- 流量（每 15 秒读一次网卡计数器，只在本机内存里算，不发包）----
    private readonly DispatcherTimer _trafficTick = new() { Interval = TimeSpan.FromSeconds(15) };
    private int _trafficCount;

    // ---- 测速 ----
    private readonly List<SpeedSample> _samples = new();
    private CancellationTokenSource? _speedCts;
    private bool _speedRunning;
    private double _speedWindow = 20;

    // ---- 游戏延迟 ----
    private CancellationTokenSource? _gameCts;
    private bool _gameRunning;
    private readonly List<string> _packetLog = new();

    public MainWindow()
    {
        InitializeComponent();
        AppPaths.Ensure();
        RestoreWindowState();          // 记住上次大小/位置
        AppPaths.Log($"窗口创建  {Ver.Text()}  数据目录={AppPaths.DataDir}");

        Loaded += async (_, _) =>
        {
            TxtFoot.Text = "正在初始化…";
            TxtFoot.ToolTip = $"数据目录：{AppPaths.DataDir}";
            await RefreshAsync();
            await LoadSavedWifiAsync();
            LoadLog();
            _timer.Start();
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        _nicPoll.Tick += async (_, _) => await PollAdaptersAsync();
        _nicPoll.Start();
        InitSpeedTab();
        InitGameTab();
        InitSites();
        InitConnTab();
        InitDeviceTab();
        InitTrafficTab();
        InitTray();
        Closing += OnClosingToTray;
        Closed += (_, _) =>
        {
            _trafficTick.Stop();
            _tray?.Dispose();
            AppPaths.Log("窗口关闭");
        };
    }

    // ---------------- 状态圆点 ----------------

    private static readonly BitmapImage DotOn  = new(new Uri("pack://application:,,,/icons/dot_online.png"));
    private static readonly BitmapImage DotOff = new(new Uri("pack://application:,,,/icons/dot_offline.png"));
    private static readonly BitmapImage DotWait = new(new Uri("pack://application:,,,/icons/dot_wait.png"));
    private static readonly BitmapImage DotBad = new(new Uri("pack://application:,,,/icons/dot_bad.png"));

    private static void SetDot(System.Windows.Controls.Image? img, string kind)
    {
        if (img == null) return;
        img.Source = kind switch
        {
            "on" => DotOn,
            "off" => DotOff,
            "bad" => DotBad,
            _ => DotWait,
        };
    }

    // ---------------- 托盘常驻 ----------------

    private void InitTray()
    {
        try
        {
            _tray = new TrayIcon(_cfg);
            _tray.ShowRequested += () =>
            {
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                Topmost = true; Topmost = false;   // 确保被拉到最前
            };
            _tray.SettingsChanged += c => _cfg = c;
            _tray.ExitRequested += () =>
            {
                _reallyExit = true;
                SaveWindowState();
                _tray?.Dispose();
                _tray = null;
                Close();
                Application.Current.Shutdown();
            };
        }
        catch (Exception ex)
        {
            AppPaths.Log("[托盘] 初始化失败: " + ex.Message);
        }
    }

    private void OnClosingToTray(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowState();
        if (_reallyExit) return;
        if (!_cfg.CloseToTray)
        {
            AppPaths.Log("[托盘] 设置为直接退出，结束进程");
            return;
        }

        // 缩到托盘：窗口藏起来，采样和定时器都继续跑
        e.Cancel = true;
        Hide();
        _tray?.SetTooltip("网络面板（后台运行中）");
        _tray?.ShowMinimizedTip();
        AppPaths.Log("[托盘] 窗口已最小化到托盘，后台继续采样");
    }

    /// <summary>远端软路由连上 / 断掉：无条件写日志（带离线时长），气泡看开关</summary>
    private void NotifyLinkChange(bool online)
    {
        if (_lastMiniOnline is null || _lastMiniOnline == online)
        {
            _lastMiniOnline = online;
            return;
        }

        if (online)
        {
            var span = _offlineSince is { } t ? DateTime.Now - t : TimeSpan.Zero;
            var lasted = _offlineSince == null ? "" : $"（离线 {FormatSpan(span)}）";
            AppPaths.Log($"[提醒] 远端恢复在线{lasted}");
            _offlineSince = null;
            if (_cfg.NotifyOnOffline)
                _tray?.Balloon("软路由已连上", "远端重新可达，正常的网络状态。");
        }
        else
        {
            _offlineSince = DateTime.Now;
            AppPaths.Log("[提醒] 远端掉线");
            if (_cfg.NotifyOnOffline)
                _tray?.Balloon("软路由连不上", "远端地址不可达 —— 可能换了网络（不再连 Gray-Router）、机器关机，或校园网掉认证。点「一键诊断」看断在哪一层。", warn: true);
        }
        _lastMiniOnline = online;
    }

    /// <summary>把时长写成「2 小时 13 分」这种，短的就「3 分 12 秒」</summary>
    private static string FormatSpan(TimeSpan s)
    {
        if (s.TotalHours >= 1) return $"{(int)s.TotalHours} 小时 {s.Minutes} 分";
        if (s.TotalMinutes >= 1) return $"{s.Minutes} 分 {s.Seconds} 秒";
        return $"{Math.Max(1, s.Seconds)} 秒";
    }

    // ---------------- 流量 ----------------

    public sealed class TrafficRow
    {
        public string Adapter { get; set; } = "";
        public string Down { get; set; } = "";
        public string Up { get; set; } = "";
        public string Total { get; set; } = "";
        public string Share { get; set; } = "";
    }

    private void InitTrafficTab()
    {
        try { Traffic.Prune(30); } catch { }
        CvTraffic.SizeChanged += (_, _) => RenderTrafficChart();
        _trafficTick.Tick += async (_, _) => await TrafficTickAsync();
        _trafficTick.Start();
        Traffic.Sample();                 // 第一次只记基线
        RefreshTrafficView();
    }

    private async Task TrafficTickAsync()
    {
        try
        {
            var rows = await Task.Run(() => Traffic.Sample());
            if (rows.Count > 0)
            {
                await Task.Run(() => Traffic.Append(rows));
                var d = rows.Sum(r => r.DownKb) / 1024.0;
                var u = rows.Sum(r => r.UpKb) / 1024.0;
                TxtTrafficLive.Text = $"刚刚 ↓{Traffic.SizeText(d)}  ↑{Traffic.SizeText(u)}";
            }
            else TxtTrafficLive.Text = "空闲中";

            if (++_trafficCount % 4 == 0) RefreshTrafficView();   // 图 1 分钟重绘一次，省 CPU
        }
        catch (Exception ex) { AppPaths.Log("[流量] 采样异常: " + ex.Message); }
    }

    private void RefreshTrafficView()
    {
        try
        {
            var today = Traffic.Total(DateTime.Today);
            var yest = Traffic.Total(DateTime.Today.AddDays(-1));
            var week = Traffic.TotalDays(7);
            TxtTrafficInfo.Text =
                $"今天 ↓{Traffic.SizeText(today.downMb)} ↑{Traffic.SizeText(today.upMb)}" +
                $"   昨天 ↓{Traffic.SizeText(yest.downMb)} ↑{Traffic.SizeText(yest.upMb)}" +
                $"   近 7 天 ↓{Traffic.SizeText(week.downMb)} ↑{Traffic.SizeText(week.upMb)}" +
                $"   ·  每 15 秒采样 · 保留 30 天 · 数据在 data\\{System.IO.Path.GetFileName(Traffic.TodayFile)}";

            var per = Traffic.PerAdapter(DateTime.Today);
            var sum = per.Sum(x => x.downMb + x.upMb);
            DgTraffic.ItemsSource = per.Select(x =>
            {
                var total = x.downMb + x.upMb;
                return new TrafficRow
                {
                    Adapter = x.adapter,
                    Down = Traffic.SizeText(x.downMb),
                    Up = Traffic.SizeText(x.upMb),
                    Total = Traffic.SizeText(total),
                    Share = sum <= 0 ? "—" : $"{total / sum * 100:F1}%",
                };
            }).ToList();

            RenderTrafficChart();
        }
        catch (Exception ex) { AppPaths.Log("[流量] 刷新失败: " + ex.Message); }
    }

    /// <summary>24 根柱：每小时一组，绿色下行、蓝色上行</summary>
    private void RenderTrafficChart()
    {
        if (CvTraffic == null) return;
        CvTraffic.Children.Clear();

        double w = CvTraffic.ActualWidth, h = CvTraffic.ActualHeight;
        if (w < 120 || h < 80) return;

        var buckets = Traffic.HourlyBuckets(DateTime.Today);
        double max = 0;
        foreach (var b in buckets) max = Math.Max(max, Math.Max(b[0], b[1]));
        if (max <= 0) max = 0.1;

        const double padLeft = 56, padRight = 10, padTop = 8, padBottom = 20;
        double plotW = w - padLeft - padRight, plotH = h - padTop - padBottom;
        double slot = plotW / 24, barW = Math.Max(3, Math.Min(12, slot * 0.32));

        // 横向参考线 25% / 50% / 75%
        for (int i = 1; i <= 3; i++)
        {
            double y = padTop + plotH * (1 - i / 4.0);
            CvTraffic.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = padLeft, X2 = padLeft + plotW, Y1 = y, Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(0xED, 0xF0, 0xF5)),
                StrokeThickness = 1,
            });
        }

        // Y 轴两个刻度
        AddCanvasText(Traffic.SizeText(max), 4, padTop - 4, 11, "#9AA5B4");
        AddCanvasText("0", 4, padTop + plotH - 12, 11, "#9AA5B4");

        for (int hour = 0; hour < 24; hour++)
        {
            double x0 = padLeft + hour * slot + (slot - barW * 2 - 2) / 2;
            double baseY = padTop + plotH;

            double hDown = buckets[hour][0] / max * plotH;
            double hUp = buckets[hour][1] / max * plotH;

            if (buckets[hour][0] > 0)
                CvTraffic.Children.Add(Bar(x0, baseY - hDown, barW, hDown, "#2FB86B",
                    $"{hour:D2}:00  下行 {Traffic.SizeText(buckets[hour][0])}"));
            if (buckets[hour][1] > 0)
                CvTraffic.Children.Add(Bar(x0 + barW + 2, baseY - hUp, barW, hUp, "#2F6FED",
                    $"{hour:D2}:00  上行 {Traffic.SizeText(buckets[hour][1])}"));

            if (hour % 3 == 0)
                AddCanvasText($"{hour}", x0 - 2, baseY + 3, 11, "#9AA5B4");
        }
    }

    private static System.Windows.Shapes.Rectangle Bar(double x, double y, double w, double h, string color, string tip)
    {
        var r = new System.Windows.Shapes.Rectangle
        {
            Width = w,
            Height = Math.Max(1, h),
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            RadiusX = 2, RadiusY = 2,
            ToolTip = tip,
        };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        return r;
    }

    private void AddCanvasText(string text, double x, double y, double size, string color)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
        };
        Canvas.SetLeft(t, x);
        Canvas.SetTop(t, y);
        CvTraffic.Children.Add(t);
    }

    private void OnTrafficRefresh(object sender, RoutedEventArgs e)
    {
        var rows = Traffic.Sample();
        if (rows.Count > 0) Traffic.Append(rows);
        RefreshTrafficView();
        TxtTrafficLive.Text = "已刷新 " + DateTime.Now.ToString("HH:mm:ss");
    }

    private void OnTrafficOpenDir(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", AppPaths.DataDir); }
        catch (Exception ex) { AppPaths.Log("[流量] 打开目录失败: " + ex.Message); }
    }

    // ---------------- 窗口位置/大小记忆 ----------------

    private void RestoreWindowState()
    {
        try
        {
            var f = System.IO.Path.Combine(AppPaths.DataDir, "window.json");
            if (!File.Exists(f)) return;
            var o = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f)).RootElement;
            double w = o.GetProperty("w").GetDouble(), h = o.GetProperty("h").GetDouble();
            double x = o.GetProperty("x").GetDouble(), y = o.GetProperty("y").GetDouble();
            bool max = o.GetProperty("max").GetBoolean();

            if (w >= MinWidth && h >= MinHeight) { Width = w; Height = h; }

            // 位置必须落在可见屏幕里，否则保持居中
            double sw = SystemParameters.VirtualScreenWidth, sh = SystemParameters.VirtualScreenHeight;
            if (x > -50 && y > -20 && x < sw - 200 && y < sh - 150)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = x; Top = y;
            }
            if (max) WindowState = WindowState.Maximized;
        }
        catch { /* 配置坏了就用默认值，不打扰用户 */ }
    }

    private void SaveWindowState()
    {
        try
        {
            var b = WindowState == WindowState.Maximized
                ? RestoreBounds
                : new Rect(Left, Top, Width, Height);
            Directory.CreateDirectory(AppPaths.DataDir);
            var json = $"{{\"w\":{b.Width},\"h\":{b.Height},\"x\":{b.Left},\"y\":{b.Top},\"max\":{(WindowState == WindowState.Maximized ? "true" : "false")}}}";
            File.WriteAllText(System.IO.Path.Combine(AppPaths.DataDir, "window.json"), json);
        }
        catch { }
    }

    // ---------------- 设备扫描 ----------------

    private CancellationTokenSource? _scanCts;
    private List<LanDevice> _devices = new();

    private async void InitDeviceTab()
    {
        var cidr = await LanScan.GuessLocalCidrAsync();
        TxtScanCidr.Text = string.IsNullOrWhiteSpace(cidr) ? "192.168.1.0/24" : cidr;
    }

    private async void OnScanDevices(object sender, RoutedEventArgs e)
    {
        var cidr = TxtScanCidr.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(cidr, @"^\d+\.\d+\.\d+\.\d+/\d+$"))
        {
            TxtScanInfo.Text = "× 网段格式不对，应该是 192.168.1.0/24 这样";
            return;
        }
        if (!cidr.EndsWith("/24"))
        {
            TxtScanInfo.Text = "× 只支持 /24（一个 C 段，254 个地址）";
            return;
        }

        BtnScan.IsEnabled = false;
        BtnScanStop.IsEnabled = true;
        _scanCts = new CancellationTokenSource();
        var cfg = RemoteConfig.Load();

        try
        {
            var deep = ChkDeep.IsChecked == true;
            var list = await LanScan.ScanAsync(cidr, cfg, deep, msg =>
            {
                Dispatcher.Invoke(() => TxtScanInfo.Text = msg);
            }, _scanCts.Token);

            _devices = list;
            DgDevices.ItemsSource = list;

            var online = list.Count(x => x.State == "在线");
            TxtScanInfo.Text = $"√ 共 {list.Count} 台，{online} 台在线" +
                               (deep ? "（深度扫描）" : "（快速，轻量发包）") +
                               (cfg.IsConfigured ? " + 软路由邻居表" : "（软路由未连接，只看本机 ARP）");
            TxtFoot.Text = $"√ 扫描完成：{list.Count} 台设备";
            AppPaths.Log($"[设备] 扫描 {cidr} 完成，{list.Count} 台，在线 {online}");
        }
        catch (OperationCanceledException)
        {
            TxtScanInfo.Text = "已停止";
        }
        catch (Exception ex)
        {
            TxtScanInfo.Text = "× 扫描出错：" + ex.Message;
            AppPaths.Log("[设备] 扫描出错：" + ex);
        }
        finally
        {
            BtnScan.IsEnabled = true;
            BtnScanStop.IsEnabled = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private void OnStopScan(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        TxtScanInfo.Text = "正在停止 …";
    }

    private void OnExportDevices(object sender, RoutedEventArgs e)
    {
        if (_devices.Count == 0) { TxtScanInfo.Text = "× 先扫描一次再导出"; return; }

        try
        {
            var path = System.IO.Path.Combine(AppPaths.DataDir, $"devices-{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("IP地址,MAC,厂商,名称,来源,状态,延迟");
            foreach (var d in _devices)
                sb.AppendLine($"{d.Ip},{d.Mac},{d.Vendor},{d.Name},{d.Source},{d.State},{d.Rtt}");
            System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));

            TxtScanInfo.Text = $"√ 已导出 {_devices.Count} 条：{System.IO.Path.GetFileName(path)}";
            AppPaths.Log("[设备] 导出 " + path);
        }
        catch (Exception ex)
        {
            TxtScanInfo.Text = "× 导出失败：" + ex.Message;
        }
    }

    // ---------------- 连接 / 部署 ----------------

    private void InitConnTab()
    {
        CmbRemoteAuth.ItemsSource = new[] { "自动（先密钥后密码）", "只用密钥", "只用密码" };
        var cfg = RemoteConfig.Load();
        TxtRemoteHost.Text = string.IsNullOrWhiteSpace(cfg.Hosts) ? cfg.Host : cfg.Hosts;
        TxtRemoteUser.Text = cfg.User;
        TxtRemotePort.Text = cfg.Port.ToString();
        CmbRemoteAuth.SelectedIndex = cfg.Auth switch { "key" => 1, "password" => 2, _ => 0 };
        TxtPackageDir.Text = cfg.LastPackage;
    }

    private RemoteConfig CollectRemote()
    {
        var cfg = RemoteConfig.Load();
        var all = TxtRemoteHost.Text.Trim();
        cfg.Hosts = all;
        cfg.Host = all.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(x => x.Trim()).FirstOrDefault() ?? "";
        cfg.User = TxtRemoteUser.Text.Trim();
        cfg.Port = int.TryParse(TxtRemotePort.Text.Trim(), out var p) && p > 0 && p < 65536 ? p : 22;
        cfg.Auth = CmbRemoteAuth.SelectedIndex switch { 1 => "key", 2 => "password", _ => "auto" };
        cfg.LastPackage = TxtPackageDir.Text.Trim();
        cfg.Save();
        return cfg;
    }

    private void ConnLog(string line)
    {
        TxtConnLog.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        TxtConnLog.ScrollToEnd();
        AppPaths.Log("[连接] " + line);
    }

    /// <summary>连接页右上「刷新状态」：刷完在这页给个反馈，不用切到状态页看</summary>
    private async void OnConnRefresh(object sender, RoutedEventArgs e)
    {
        BtnConnRefresh.IsEnabled = false;
        ConnLog("正在刷新状态 …");
        try
        {
            await RefreshAsync(manual: true);
            ConnLog("√ 刷新完成：远端 " + (_lastMiniOnline == true ? "在线" : "不可达"));
        }
        catch (Exception ex) { ConnLog("× 刷新失败：" + ex.Message); }
        finally { BtnConnRefresh.IsEnabled = true; }
    }

    /// <summary>切到「连接」页时静默探一次地址（不阻塞、只探一次）</summary>
    private async void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl) return;
        if (NavTabs.SelectedIndex != 8 || _detectTried) return;   // 8 = 连接
        _detectTried = true;
        await DetectRemoteAsync(silent: true);
    }

    private CancellationTokenSource? _detectCts;
    private bool _detectTried;      // 首次切到连接页时静默探一次，只探一次

    /// <summary>自动探测远端地址（不阻塞界面，可随时再点取消）</summary>
    private async Task DetectRemoteAsync(bool silent)
    {
        // 取消上一次探测：先把引用摘掉再取消，避免碰到已 Dispose 的 CTS
        var prev = _detectCts;
        _detectCts = null;
        try { prev?.Cancel(); prev?.Dispose(); } catch { }

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        _detectCts = cts;

        BtnAutoDetect.IsEnabled = false;
        var old = TxtRemoteHost.Text;
        try
        {
            var cfg = RemoteConfig.Load();
            if (string.IsNullOrWhiteSpace(cfg.User))
            {
                if (silent) return;
                ConnLog("× 先把用户名填上（自动探测要知道用哪个用户名登录）");
                return;
            }

            if (!silent) ConnLog("开始自动探测远端地址 …");
            var hit = await RemoteDiscovery.DiscoverAsync(cfg, cfg.Port, silent ? null : ConnLog, cts.Token);
            if (hit == null)
            {
                if (!silent) ConnLog("× 没探到；可以手动填地址");
                return;
            }

            TxtRemoteHost.Text = hit.Host;
            BtnAutoDetect.ToolTip = hit.Detail;
            ConnLog($"√ 自动填入 {hit.Host} —— {hit.Detail}");
            if (!silent) TxtFoot.Text = "已自动填入：" + cfg.User + "@" + hit.Host;
        }
        catch (OperationCanceledException) { if (!silent) ConnLog("· 探测已取消"); }
        catch (Exception ex) { ConnLog("× 探测出错：" + ex.Message); }
        finally
        {
            BtnAutoDetect.IsEnabled = true;
            if (ReferenceEquals(_detectCts, cts)) _detectCts = null;
            cts.Dispose();
        }
    }

    private async void OnAutoDetect(object sender, RoutedEventArgs e) => await DetectRemoteAsync(silent: false);

    private async void OnTestRemote(object sender, RoutedEventArgs e)
    {
        var cfg = CollectRemote();
        if (!cfg.IsConfigured) { ConnLog("× 先把地址和用户名填上"); return; }

        BtnRemoteTest.IsEnabled = false;
        ConnLog($"开始试 {cfg.HostList().Count} 个地址 …");
        try
        {
            var host = await Remote.ResolveAsync(cfg, ConnLog);
            if (host == null)
            {
                ConnLog("× 所有地址都连不上（不在同一网络？软路由没开机？）");
                TxtFoot.Text = "× 连接失败：地址都不通";
                return;
            }
            ConnLog($"选中 {host}，正在验证登录 …");
            var (ok, detail) = await Remote.TestAsync(cfg);
            ConnLog((ok ? "√ " : "× ") + detail);
            TxtFoot.Text = (ok ? "√ 连接正常：" : "× 连接失败：") + cfg.Target;
        }
        finally { BtnRemoteTest.IsEnabled = true; }
    }

    private void OnPickPackage(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 GrayRouter 部署包",
            Filter = "部署包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
        {
            TxtPackageDir.Text = dlg.FileName;
            ConnLog("已选择：" + dlg.FileName);
        }
    }

    /// <summary>把 zip 解到临时目录，返回包含 install.sh 的那层目录</summary>
    private string? PreparePackageDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                if (File.Exists(System.IO.Path.Combine(path, "install.sh"))) return path;
                foreach (var sub in Directory.GetDirectories(path))
                    if (File.Exists(System.IO.Path.Combine(sub, "install.sh"))) return sub;
                ConnLog("× 这个目录里没有 install.sh");
                return null;
            }

            if (!File.Exists(path)) { ConnLog("× 找不到：" + path); return null; }

            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ConnLog("× 只支持 .zip 或已解压的目录");
                return null;
            }

            var tmp = System.IO.Path.Combine(AppPaths.TmpDir, "grayrouter-pkg");
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            ZipFile.ExtractToDirectory(path, tmp);
            if (File.Exists(System.IO.Path.Combine(tmp, "install.sh"))) return tmp;
            foreach (var sub in Directory.GetDirectories(tmp))
                if (File.Exists(System.IO.Path.Combine(sub, "install.sh"))) return sub;

            ConnLog("× 压缩包里没找到 install.sh");
            return null;
        }
        catch (Exception ex) { ConnLog("× 解压失败：" + ex.Message); return null; }
    }

    private async void OnOpenTerminal(object sender, RoutedEventArgs e)
    {
        var cfg = CollectRemote();
        if (!cfg.IsConfigured) { ConnLog("× 先把地址和用户名填上"); return; }

        BtnOpenTerm.IsEnabled = false;
        try
        {
            var host = await Remote.ResolveAsync(cfg, ConnLog);
            if (host == null) { ConnLog("× 地址都不通，没开终端"); return; }

            ConnLog($"正在打开终端：{cfg.Target}");
            Remote.OpenTerminal(cfg, "SSH - " + host);
            TxtFoot.Text = "终端窗口已打开：" + cfg.Target;
        }
        finally { BtnOpenTerm.IsEnabled = true; }
    }

    private async void OnUploadPackage(object sender, RoutedEventArgs e) => await UploadPackageAsync();

    private async Task<bool> UploadPackageAsync()
    {
        var cfg = CollectRemote();
        if (!cfg.IsConfigured) { ConnLog("× 先把地址和用户名填上"); return false; }

        var dir = PreparePackageDir(TxtPackageDir.Text.Trim());
        if (dir == null) return false;

        BtnUpload.IsEnabled = false;
        try
        {
            ConnLog("正在上传 " + dir + " …");
            var (ok, msg) = await Remote.UploadAsync(cfg, dir);
            ConnLog((ok ? "√ 已上传到 " : "× ") + msg);
            return ok;
        }
        finally { BtnUpload.IsEnabled = true; }
    }

    private bool _deployBusy;

    private async void OnDeploy(object sender, RoutedEventArgs e)
    {
        if (_deployBusy) { ConnLog("× 已经在部署了，别连点（会开两个安装窗口）"); return; }
        _deployBusy = true;
        BtnDeploy.IsEnabled = false;
        try
        {
            var cfg = CollectRemote();
            if (!cfg.IsConfigured) { ConnLog("× 先把地址和用户名填上"); return; }

            ConnLog("① 上传部署包 …");
            if (!await UploadPackageAsync()) { ConnLog("× 上传失败，部署中止"); return; }

            ConnLog("② 打开终端窗口执行安装（sudo 密码在那个窗口里输）");
            var cmd = "cd /tmp/grayrouter-upload && D=$(ls -d */ 2>/dev/null | head -1); " +
                      "cd \"${D:-.}\" && ls install.sh >/dev/null 2>&1 && sudo bash install.sh";

            Remote.RunInConsoleWindow(cfg, cmd, "GrayRouter 部署 - " + cfg.Host);
            ConnLog("③ 终端窗口已打开；装完后回「状态」页点「刷新」");
            TxtFoot.Text = "部署已启动，在终端窗口里完成";
        }
        finally
        {
            // 窗口刚起时先锁 5 秒，防止手快点第二下
            await Task.Delay(5000);
            BtnDeploy.IsEnabled = true;
            _deployBusy = false;
        }
    }

    // ---------------- 日志页 ----------------

    private void LoadLog()
    {
        try
        {
            var f = AppPaths.LogFile;
            if (!File.Exists(f)) { TxtLog.Text = "（还没有日志）"; TxtLogInfo.Text = ""; return; }

            var fi = new FileInfo(f);
            const long maxBytes = 300 * 1024;      // 只读最后 300KB，大文件也不卡
            string text;
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long skip = Math.Max(0, fi.Length - maxBytes);
                fs.Seek(skip, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                text = sr.ReadToEnd();
                if (skip > 0) text = $"…（更早的 {skip / 1024} KB 已省略）\n" + text;
            }

            var lines = text.Replace("\r\n", "\n").Split('\n');
            // 最新的排在上面，省得往下滚
            TxtLog.Text = string.Join("\n", lines.Reverse());
            TxtLogInfo.Text = $"{fi.Length / 1024.0:F1} KB · {lines.Length} 行 · 最后修改 {fi.LastWriteTime:MM-dd HH:mm:ss}";
            TxtLog.ScrollToHome();
        }
        catch (Exception ex) { TxtLog.Text = "读取日志失败：" + ex.Message; }
    }

    private void OnRefreshLog(object sender, RoutedEventArgs e)
    {
        LoadLog();
        TxtFoot.Text = "√ 日志已刷新";
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var f = AppPaths.LogFile;
            if (!File.Exists(f)) { TxtFoot.Text = "没有日志可清"; return; }
            long size = new FileInfo(f).Length;
            if (MessageBox.Show(
                    $"确定清空日志吗？当前 {size / 1024.0:F1} KB。\n\n会先把旧内容备份成 app.log.1，再清空。",
                    "清空日志", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            File.Copy(f, f + ".1", overwrite: true);
            File.WriteAllText(f, "", new UTF8Encoding(false));
            AppPaths.Log("日志已手动清空（旧内容备份在 app.log.1）");
            LoadLog();
            TxtFoot.Text = "√ 日志已清空，旧内容在 app.log.1";
        }
        catch (Exception ex)
        {
            AppPaths.Log("清空日志失败: " + ex.Message);
            MessageBox.Show("清空失败：\n" + ex.Message, "清空日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenLogDir(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", AppPaths.DataDir); }
        catch (Exception ex) { AppPaths.Log("打开目录失败: " + ex.Message); }
    }

    // ---------------- 一键网络诊断 ----------------

    private void OnDiagnose(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new DiagnoseWindow(RemoteConfig.Load()) { Owner = this };
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            AppPaths.Log("[诊断] 打开窗口失败: " + ex.Message);
            MessageBox.Show("诊断窗口打不开：" + ex.Message, "网络面板", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        // 手动刷新：立刻跑一次，并把自动刷新的计时重新归零
        _timer.Stop();
        BtnRefresh.IsEnabled = false;
        var old = BtnRefresh.Content;
        BtnRefresh.Content = "刷新中…";
        try
        {
            await RefreshAsync(manual: true);
        }
        catch (Exception ex)
        {
            TxtFoot.Text = "× 刷新失败：" + ex.Message;
        }
        finally
        {
            BtnRefresh.Content = old;
            BtnRefresh.IsEnabled = true;
            _timer.Start();
        }
    }
    private async void OnRefreshNics(object sender, RoutedEventArgs e) => await RefreshNicsAsync();

    private async Task RefreshAsync(bool manual = false)
    {
        if (_busy) return;
        _busy = true;
        BtnRefresh.IsEnabled = false;
        TxtHint.Text = "正在刷新…";
        try
        {
            var snap = await StatusGather.CollectAsync();
            TxtLocal.Text = snap.Local;
            TxtCampus.Text = snap.Campus;
            TxtMini.Text = snap.Mini;

            try { File.WriteAllText(AppPaths.SnapshotFile, snap.Raw, new UTF8Encoding(false)); } catch { }

            await RefreshNicsAsync();

            TxtHint.Text = snap.MiniOnline ? "远端已连接" : "远端未连接";
            SetDot(ImgDotLink, snap.MiniOnline ? "on" : "off");
            TxtFoot.Text = manual
                ? $"√ 已手动刷新 {DateTime.Now:HH:mm:ss}"
                : $"自动刷新 {DateTime.Now:HH:mm:ss}";
            TxtFoot.ToolTip = $"数据目录：{AppPaths.DataDir}";
            SetDot(ImgDotLocal, "on");
            SetDot(ImgDotCampus, snap.CampusOk ? "on" : "bad");
            SetDot(ImgDotMini, snap.MiniOnline ? "on" : "off");
            AppPaths.Log($"刷新完成 MiniOnline={snap.MiniOnline}");
            NotifyLinkChange(snap.MiniOnline);
        }
        catch (Exception ex)
        {
            AppPaths.Log("刷新异常: " + ex);
            TxtHint.Text = "刷新失败";
            TxtFoot.Text = "刷新失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            BtnRefresh.IsEnabled = true;
        }
    }

    // ---------------- 网卡页 ----------------


    /// <summary>网卡热插拔检测：名字集合变了就提示并自动刷新列表</summary>
    private async Task PollAdaptersAsync()
    {
        if (_nicPolling) return;
        _nicPolling = true;
        try
        {
            var list = await StatusGather.GetPhysicalAdaptersAsync();
            var names = list.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 测试钩子：设了 NETPANEL_FAKE_NIC 就当作多了一块网卡，用来验证提示链路
            var fake = Environment.GetEnvironmentVariable("NETPANEL_FAKE_NIC");
            if (!string.IsNullOrWhiteSpace(fake)) names.Add(fake.Trim());

            if (_knownNics == null) { _knownNics = names; return; }

            var added = names.Except(_knownNics).ToList();
            var removed = _knownNics.Except(names).ToList();
            if (added.Count == 0 && removed.Count == 0) return;

            _knownNics = names;

            string msg = added.Count > 0
                ? $"！检测到新网卡：{string.Join("、", added)}"
                : $"！网卡已移除：{string.Join("、", removed)}";
            if (added.Count > 0 && removed.Count > 0)
                msg = $"！网卡变化：新增 {string.Join("、", added)}；移除 {string.Join("、", removed)}";

            if (TxtNicNotice != null) TxtNicNotice.Text = msg + "  ·  列表已自动刷新";
            TxtFoot.Text = msg + $"  {DateTime.Now:HH:mm:ss}";
            AppPaths.Log(msg);
            await RefreshNicsAsync();
        }
        catch { }
        finally { _nicPolling = false; }
    }

    private async Task RefreshNicsAsync()
    {
        var list = await StatusGather.GetPhysicalAdaptersAsync();
        if (_knownNics == null)
            _knownNics = list.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n))
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);
        BuildNicRows(list);
    }

    private void BuildNicRows(List<AdapterInfo> list)
    {
        PanelNics.Children.Clear();
        if (list.Count == 0)
        {
            PanelNics.Children.Add(new TextBlock
            {
                Text = "读不到物理网卡（可能需要管理员权限）",
                Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x98)),
                Margin = new Thickness(0, 10, 0, 0)
            });
            return;
        }

        foreach (var a in list)
        {
            var row = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEF)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 12, 0, 12)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = $"{a.Name}     [{a.Status}]",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = a.IsDisabled
                    ? new SolidColorBrush(Color.FromRgb(0xB4, 0x3B, 0x3B))
                    : a.Status.Equals("Up", StringComparison.OrdinalIgnoreCase)
                        ? new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x5A))
                        : new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x30))
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{a.Description}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x74)),
                Margin = new Thickness(0, 4, 0, 0)
            });
            info.Children.Add(new TextBlock
            {
                Text = $"速率 {a.LinkSpeed}    IP {a.Ip}    网关 {a.Gateway}    MAC {a.Mac}",
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x74)),
                Margin = new Thickness(0, 3, 0, 0)
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var btnEnable = MakeButton("启用", true, a.IsDisabled);
            btnEnable.Click += async (_, _) => await ToggleAdapterAsync(a.Name, true);
            var btnDisable = MakeButton("禁用", false, !a.IsDisabled);
            btnDisable.Click += async (_, _) => await ToggleAdapterAsync(a.Name, false);
            btns.Children.Add(btnEnable);
            btns.Children.Add(btnDisable);

            Grid.SetColumn(btns, 1);
            grid.Children.Add(btns);

            row.Child = grid;
            PanelNics.Children.Add(row);
        }
    }


    /// <summary>按钮内容：小图标 + 文字</summary>
    private static UIElement IconText(string icon, string text, Color? color = null, double iconSize = 15)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri($"pack://application:,,,/icons/{icon}.png")),
            Width = iconSize,
            Height = iconSize,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        sp.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = color == null ? Brushes.Black : new SolidColorBrush(color.Value)
        });
        return sp;
    }

    private static Button MakeButton(string text, bool isEnable, bool enabled)
    {
        return new Button
        {
            Content = IconText(isEnable ? "shield_check_w" : "shield_x", text,
                isEnable ? Colors.White : Color.FromRgb(0xB4, 0x3B, 0x3B)),
            Width = 86,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = enabled,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 13,
            Background = isEnable
                ? new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED))
                : new SolidColorBrush(Color.FromRgb(0xFB, 0xEF, 0xEF)),
            Foreground = isEnable
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(0xB4, 0x3B, 0x3B)),
            BorderThickness = new Thickness(isEnable ? 0 : 1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xB4, 0xB4))
        };
    }

    /// <summary>启用/禁用：先弹确认框，用户点「是」才真正执行</summary>
    private async Task ToggleAdapterAsync(string name, bool enable)
    {
        var action = enable ? "启用" : "禁用";
        var msg = $"确认要{action}【{name}】吗？";

        if (!enable && name.Contains("WLAN", StringComparison.OrdinalIgnoreCase))
        {
            msg += "\n\n⚠️ 这是无线网卡，禁用后会立刻断网：\n" +
                   "   · 主力机上不了网\n" +
                   "   · 连远端软路由的 SSH 会断\n\n" +
                   "恢复办法：重新插网线、或开手机热点后手动启用。\n" +
                   "在校期间确认要断网再点「是」。";
        }
        else if (enable && name.Contains("以太网", StringComparison.Ordinal))
        {
            msg += "\n\n注意：如果这台机器正在当软路由，同时启用有线口会让校园网\n" +
                   "看到两个 MAC（可能被踢），先用「5 查看状态」确认模式。";
        }

        var result = MessageBox.Show(msg, $"确认{action}网卡",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            TxtFoot.Text = $"已取消{action}【{name}】";
            AppPaths.Log($"用户取消{action} {name}");
            return;
        }

        TxtFoot.Text = $"正在{action}【{name}】…";
        var (ok, detail) = await AdapterOps.SetEnabledAsync(name, enable);
        var state = string.IsNullOrWhiteSpace(detail) ? "" : $"（当前状态 {detail}）";

        TxtFoot.Text = ok
            ? $"✅ 已{action}【{name}】{state}"
            : $"❌ {action}【{name}】失败：{detail}";

        if (!ok)
            MessageBox.Show($"操作失败：\n{detail}\n\n详情见日志：\n{AppPaths.LogFile}",
                "失败", MessageBoxButton.OK, MessageBoxImage.Error);

        await Task.Delay(700);
        await RefreshNicsAsync();
    }

    // ---------------- WiFi 页 ----------------

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-location")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppPaths.Log("打开位置设置失败: " + ex.Message);
            MessageBox.Show("打不开设置页，请手动：设置 → 隐私和安全性 → 位置 → 打开「位置服务」。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void OnScanWifi(object sender, RoutedEventArgs e) => await ScanWifiAsync();

    private async Task ScanWifiAsync()
    {
        TxtWifiHint.Text = "扫描中…（需要 2~4 秒）";
        PanelWifi.Children.Clear();
        var (list, msg) = await WifiOps.ScanAsync();
        TxtWifiHint.Text = msg;

        if (list.Count == 0)
        {
            TxtWifiHint.Text = msg + "     ↓ 已降级为「已保存的网络」列表（不开位置服务也能连）";
            await LoadSavedWifiAsync();
            return;
        }

        BuildWifiRows(list);
        AppPaths.Log($"WiFi 扫描：{list.Count} 个网络");
    }

    /// <summary>没有位置服务时的降级：列出已保存的网络、可直接连接</summary>
    private async Task LoadSavedWifiAsync()
    {
        var saved = await WifiOps.SavedNetworksAsync();
        PanelWifi.Children.Clear();

        if (saved.Count == 0)
        {
            PanelWifi.Children.Add(Hint("没有已保存的 WiFi 配置。"));
            return;
        }

        PanelWifi.Children.Add(Hint($"已保存的网络（{saved.Count} 个）—— 点「连接」直接连，不需要扫描，也不需要位置服务："));

        foreach (var net in saved)
        {
            var row = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEF)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 10, 0, 10)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = net.Ssid,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold
            });
            info.Children.Add(new TextBlock
            {
                Text = "已保存的配置",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x98)),
                Margin = new Thickness(0, 3, 0, 0)
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var btn = new Button
            {
                Content = IconText("link_w", "连接", Colors.White),
                Width = 92,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            var captured = net;
            btn.Click += async (_, _) => await ConnectWifiAsync(captured);
            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);

            row.Child = grid;
            PanelWifi.Children.Add(row);
        }
    }

    private void BuildWifiRows(List<WifiNetwork> list)
    {
        PanelWifi.Children.Clear();
        foreach (var net in list)
        {
            var row = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEF)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 11, 0, 11)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(net.Ssid) ? "（隐藏网络）" : net.Ssid,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold
            });
            if (net.Saved)
            {
                title.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(10, 1, 0, 0),
                    Child = new TextBlock
                    {
                        Text = "已保存",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED))
                    }
                });
            }
            info.Children.Add(title);

            info.Children.Add(new TextBlock
            {
                Text = $"{net.Bars}    {net.Auth} / {net.Encrypt}",
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x74)),
                Margin = new Thickness(0, 4, 0, 0)
            });

            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var btn = new Button
            {
                Content = IconText("link_w", net.Saved ? "连接" : "输入密码连接", Colors.White),
                MinWidth = 118,
                Height = 32,
                Padding = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            var captured = net;
            btn.Click += async (_, _) => await ConnectWifiAsync(captured);
            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);

            row.Child = grid;
            PanelWifi.Children.Add(row);
        }
    }

    private async Task ConnectWifiAsync(WifiNetwork net)
    {
        string? pwd = null;

        if (!net.Saved)
        {
            var dlg = new PasswordDialog(net.Ssid) { Owner = this };
            if (dlg.ShowDialog() != true) { TxtWifiHint.Text = "已取消"; return; }
            pwd = dlg.Password;
        }

        var confirm = MessageBox.Show(
            $"确认连接到【{net.Ssid}】？\n\n如果当前正靠这个 WiFi 上网，切换过程会短暂断开（几秒）。",
            "确认连接", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

        if (confirm != MessageBoxResult.Yes) { TxtWifiHint.Text = "已取消连接"; return; }

        TxtWifiHint.Text = $"正在连接 {net.Ssid} …";
        var (ok, detail) = await WifiOps.ConnectAsync(net, pwd);

        if (ok)
        {
            TxtWifiHint.Text = $"√ 已发出连接请求：{net.Ssid}（约 3 秒后生效）";
            await Task.Delay(2500);
            await RefreshAsync();
            PanelWifi.Children.Clear();
            TxtWifiHint.Text = "连接完成，点「扫描」可刷新列表";
        }
        else
        {
            TxtWifiHint.Text = $"× 连接失败：{detail}";
            MessageBox.Show($"连接失败：\n{detail}\n\n详情见日志：\n{AppPaths.LogFile}",
                "失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnDisconnectWifi(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "确认断开当前 WiFi？\n\n如果这是你唯一的上网通道，断开后会立刻没网。",
            "确认断开", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) { TxtWifiHint.Text = "已取消断开"; return; }

        var (ok, detail) = await WifiOps.DisconnectAsync();
        TxtWifiHint.Text = ok ? "已断开当前 WiFi" : "断开失败：" + detail;
        await Task.Delay(1200);
        await RefreshAsync();
    }




    // ---------------- 网址连通性 ----------------

    private static readonly string[] PresetsCn =
    {
        "baidu.com", "bilibili.com", "zhihu.com", "taobao.com", "qq.com", "weibo.com",
        "163.com", "jd.com", "aliyun.com", "cnki.net"
    };

    private static readonly string[] PresetsForeign =
    {
        "youtube.com", "google.com", "x.com", "facebook.com", "instagram.com",
        "wikipedia.org", "reddit.com", "discord.com", "twitch.tv", "netflix.com",
        "chatgpt.com", "steamcommunity.com"
    };

    private static readonly string[] PresetsGitHub =
    {
        "github.com", "api.github.com", "raw.githubusercontent.com", "gist.github.com",
        "codeload.github.com", "github.io", "objects.githubusercontent.com"
    };

    private static IEnumerable<string> AllPresets => PresetsCn.Concat(PresetsForeign).Concat(PresetsGitHub);
    private bool _siteRunning;

    private void InitSites()
    {
        FillPresets(PanelPresetsCn, PresetsCn);
        FillPresets(PanelPresetsForeign, PresetsForeign);
        FillPresets(PanelPresetsGitHub, PresetsGitHub);

        TxtSites.Text = string.IsNullOrWhiteSpace(_cfg.Sites) ? "baidu.com" : _cfg.Sites;
    }


    private void FillPresets(Panel target, string[] sites)
    {
        target.Children.Clear();
        foreach (var site in sites)
        {
            var b = new Button
            {
                Content = site,
                Height = 26,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 8, 6),
                FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF3, 0xFC)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDF, 0xF0))
            };
            var captured = site;
            b.Click += async (_, _) =>
            {
                TxtSites.Text = captured;
                await RunSiteChecks(new List<string> { captured });
            };
            target.Children.Add(b);
        }
    }

    private List<string> ParseSites()
    {
        var raw = TxtSites.Text.Replace("，", ",");
        var list = new List<string>();
        foreach (var part in raw.Split(new[] { '\n', '\r', ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list.Distinct().ToList();
    }

    private async void OnCheckSites(object sender, RoutedEventArgs e) => await RunSiteChecks(ParseSites());

    private async void OnCheckAllPresets(object sender, RoutedEventArgs e)
    {
        var all = AllPresets.ToList();
        TxtSites.Text = $"（全部 {all.Count} 个常用站）";
        await RunSiteChecks(all);
    }

    private SiteResult?[] _lastSiteResults = Array.Empty<SiteResult?>();

    private async Task RunSiteChecks(List<string> sites)
    {
        if (_siteRunning) return;
        if (sites.Count == 0)
        {
            TxtSiteHint.Text = "先填个网址，比如 baidu.com";
            return;
        }

        _siteRunning = true;
        BtnCheckSites.IsEnabled = false;
        PanelSiteResults.Children.Clear();
        TxtSiteHint.Text = $"检测中 {sites.Count} 个…";
        AppPaths.Log($"网址检测开始 count={sites.Count}  {string.Join(",", sites)}");

        var results = new SiteResult?[sites.Count];
        var gate = new SemaphoreSlim(6);

        var tasks = sites.Select(async (s, i) =>
        {
            await gate.WaitAsync();
            try
            {
                results[i] = await SiteCheck.CheckAsync(s);
                RenderSites(results);
            }
            finally { gate.Release(); }
        }).ToList();

        try { await Task.WhenAll(tasks); }
        finally
        {
            _siteRunning = false;
            BtnCheckSites.IsEnabled = true;
            var done = results.Where(r => r != null).ToList();
            _lastSiteResults = results;
            int ok = done.Count(r => r!.Ok);
            TxtSiteHint.Text = $"完成：{ok}/{sites.Count} 个能连";
            TxtFoot.Text = $"网址检测 {ok}/{sites.Count} 能连";
            AppPaths.Log("网址检测完成 " + string.Join(" | ", done.Select(r =>
                $"{r!.Input}={(r.Ok ? r.Status.ToString() : r.Error)}")));
            SaveSites(TxtSites.Text);
        }
    }

    private void OnExportSites(object sender, RoutedEventArgs e)
    {
        var list = _lastSiteResults.Where(r => r != null).Select(r => r!).ToList();
        if (list.Count == 0)
        {
            MessageBox.Show("还没有检测结果。先点「测全部常用」或「检测」。", "导出",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            AppPaths.Ensure();
            var file = System.IO.Path.Combine(AppPaths.DataDir, $"sites_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("站点,结果,状态码,IP,DNS毫秒,连接毫秒,TLS毫秒,首字节毫秒,总计毫秒,说明");
            foreach (var r in list)
            {
                string result = r.Ok ? (r.Status >= 200 && r.Status < 400 ? "能连" : "能连但返回异常") : "不通";
                sb.AppendLine(string.Join(",",
                    Csv(r.Host.Length > 0 ? r.Host : r.Input),
                    Csv(result),
                    r.Status.ToString(),
                    Csv(r.Ip),
                    r.DnsMs.ToString("F0"),
                    r.TcpMs.ToString("F0"),
                    r.TlsMs.ToString("F0"),
                    r.TtfbMs.ToString("F0"),
                    r.TotalMs.ToString("F0"),
                    Csv(r.Error)));
            }
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));   // 带 BOM，Excel 中文不乱码
            TxtSiteHint.Text = $"已导出 {list.Count} 条";
            TxtFoot.Text = "√ 已导出 " + System.IO.Path.GetFileName(file);
            AppPaths.Log($"网址检测导出 {list.Count} 条 → {file}");

            if (MessageBox.Show($"已导出 {list.Count} 条到：\n{file}\n\n要打开所在文件夹吗？", "导出成功",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"");
        }
        catch (Exception ex)
        {
            AppPaths.Log("导出失败: " + ex);
            MessageBox.Show("导出失败：\n" + ex.Message, "导出", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Csv(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }

    private void RenderSites(SiteResult?[] results)
    {
        PanelSiteResults.Children.Clear();
        foreach (var r in results)
        {
            if (r == null)
            {
                var wait = new TextBlock { Text = "○ 等待中…", FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)) };
                PanelSiteResults.Children.Add(wait);
                continue;
            }

            // 判定颜色：能连=绿，能连但非 2xx/3xx=黄，不通=红
            Color color;
            string mark;
            if (!r.Ok) { color = Color.FromRgb(0xC0, 0x39, 0x2B); mark = "×"; }
            else if (r.Error.Length > 0) { color = Color.FromRgb(0xC0, 0x39, 0x2B); mark = "×"; }
            else if (r.Status >= 200 && r.Status < 400) { color = Color.FromRgb(0x18, 0x8A, 0x3E); mark = "√"; }
            else { color = Color.FromRgb(0xC8, 0x86, 0x00); mark = "!"; }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = mark, Width = 16, FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = r.Host.Length > 0 ? r.Host : r.Input,
                Width = 210, FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x34, 0x44)),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            });
            row.Children.Add(new TextBlock
            {
                Text = r.Ok ? $"{r.Status}  {SiteCheck.Describe(r)}" : SiteCheck.Describe(r),
                Width = 250, FontSize = 12.5, Foreground = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = r.Error
            });
            row.Children.Add(new TextBlock
            {
                Text = r.StageDetail(), FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x98)),
                VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas")
            });
            PanelSiteResults.Children.Add(row);
        }
    }

    private void SaveSites(string text)
    {
        _cfg.Sites = text;
        _cfg.Save();
    }

    // ---------------- 游戏延迟页 ----------------

    private void InitGameTab()
    {
        CmbGameTarget.ItemsSource = GamePing.Targets;
        CmbGameTarget.SelectedIndex = 0;
        CmbGameTarget.SelectionChanged += (_, _) =>
        {
            var t = CmbGameTarget.SelectedItem as PingTarget;
            TxtGameHost.Text = t?.Host ?? "";
            TxtGameHint.Text = t == null ? "" : $"{t.Note}   （目标 {t.Host}）";
        };

        CmbGameCount.ItemsSource = new[] { "20 包", "50 包", "100 包" };
        CmbGameCount.SelectedIndex = 0;
        CmbGameRounds.ItemsSource = new[] { "1 次", "3 次", "5 次" };
        CmbGameRounds.SelectedIndex = 0;

        var first = GamePing.Targets[0];
        TxtGameHost.Text = first.Host;
        TxtGameHint.Text = $"{first.Note}   （目标 {first.Host}）";
    }

    private async void OnStartGamePing(object sender, RoutedEventArgs e)
    {
        if (_gameRunning) return;

        string host = TxtGameHost.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show("先选一个目标，或者在右边填服务器 IP / 域名。", "缺少目标",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int count = CmbGameCount.SelectedIndex switch { 1 => 50, 2 => 100, _ => 20 };
        int rounds = CmbGameRounds.SelectedIndex switch { 1 => 3, 2 => 5, _ => 1 };

        _gameRunning = true;
        _packetLog.Clear();
        TxtGamePackets.Text = "";
        TxtGameGrade.Text = "…";
        BoxGameGrade.Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF8));
        TxtGameGrade.Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED));
        TxtGameVerdict.Text = $"正在测 {host}（{count} 包）…";
        SetDot(ImgDotGame, "wait");
        TxtGameStats.Text = "延迟 —   抖动 —   丢包 —";
        BtnGameStart.IsEnabled = false;
        BtnGameStop.IsEnabled = true;
        CmbGameTarget.IsEnabled = false;
        TxtGameHost.IsEnabled = false;
        CmbGameCount.IsEnabled = false;
        CmbGameRounds.IsEnabled = false;
        AppPaths.Log($"游戏延迟开始 host={host} count={count} rounds={rounds}");

        _gameCts = new CancellationTokenSource();

        int done = 0, lost = 0;
        var prog = new Progress<(int Done, int Rtt, int Total)>(p =>
        {
            done = p.Done;
            if (p.Rtt < 0) lost++;
            _packetLog.Add(p.Rtt < 0 ? "× " : $"{p.Rtt,4} ");
            TxtGamePackets.Text = string.Join("", _packetLog);
            TxtGameVerdict.Text = $"检测中 {p.Done}/{p.Total}  已丢 {lost} 个";
        });

        try
        {
            // 连测 rounds 轮取平均；每轮 20 包约 2 秒、50 包约 5 秒、100 包约 10 秒
            var avgList = new List<double>();
            double jitterSum = 0, lossSum = 0;
            PingStats? last = null;

            for (int round = 1; round <= rounds; round++)
            {
                if (round > 1)
                {
                    _packetLog.Clear();
                    TxtGamePackets.Text = "";
                    done = 0; lost = 0;
                    await Task.Delay(400, _gameCts.Token);
                }
                if (rounds > 1)
                    TxtGameVerdict.Text = $"第 {round}/{rounds} 轮：测 {host}（{count} 包）…";

                var st = await GamePing.RunAsync(host, count, 100, prog, _gameCts.Token);
                last = st;
                if (st.Avg >= 0) avgList.Add(st.Avg);
                jitterSum += st.Jitter;
                lossSum += st.LossPct;
            }

            var stats = last!;
            double avgAll = avgList.Count > 0 ? avgList.Average() : -1;
            double jitAvg = jitterSum / Math.Max(rounds, 1);
            double lossAvg = lossSum / Math.Max(rounds, 1);

            TxtGameGrade.Text = stats.Grade;
            SetDot(ImgDotGame, stats.Grade switch
            {
                "极佳" => "on",
                "良好" => "on",
                "勉强" => "wait",
                _ => "bad",
            });
            TxtGameVerdict.Text = rounds > 1
                ? $"{stats.Verdict}（{rounds} 轮平均）"
                : stats.Verdict;
            TxtGameStats.Text = stats.Avg < 0
                ? $"全部丢包（{stats.Sent} 个包一个没回）"
                : rounds > 1
                    ? $"平均 {avgAll:F0} ms（{rounds} 轮：{string.Join(" / ", avgList.Select(a => a.ToString("F0")))}）   抖动 {jitAvg:F1} ms   丢包 {lossAvg:F0}%"
                    : $"平均 {stats.Avg:F0} ms   最低 {stats.Min:F0}   最高 {stats.Max:F0}   抖动 {stats.Jitter:F1} ms   丢包 {stats.LossPct:F0}%";

            bool good = stats.Grade is "极佳" or "良好";
            BoxGameGrade.Background = new SolidColorBrush(good
                ? Color.FromRgb(0xE6, 0xF7, 0xEC)
                : Color.FromRgb(0xFD, 0xEC, 0xEC));
            TxtGameGrade.Foreground = new SolidColorBrush(good
                ? Color.FromRgb(0x18, 0x8A, 0x3E)
                : Color.FromRgb(0xC0, 0x39, 0x2B));

            TxtFoot.Text = stats.Avg < 0
                ? $"× {host} 全部丢包"
                : $"{stats.Grade}：{host} 平均 {avgAll:F0} ms，抖动 {jitAvg:F1} ms，丢包 {lossAvg:F0}%";
            AppPaths.Log($"游戏延迟完成 host={host} rounds={rounds} grade={stats.Grade} avg={avgAll:F1} jitter={jitAvg:F1} loss={lossAvg:F1}");
        }
        catch (Exception ex)
        {
            TxtGameVerdict.Text = "检测失败：" + ex.Message;
            AppPaths.Log("游戏延迟异常: " + ex);
        }
        finally
        {
            _gameRunning = false;
            BtnGameStart.IsEnabled = true;
            BtnGameStop.IsEnabled = false;
            CmbGameTarget.IsEnabled = true;
            TxtGameHost.IsEnabled = true;
            CmbGameCount.IsEnabled = true;
            CmbGameRounds.IsEnabled = true;
            _gameCts?.Dispose();
            _gameCts = null;
        }
    }

    private void OnStopGamePing(object sender, RoutedEventArgs e)
    {
        TxtGameVerdict.Text = "正在停止…";
        try { _gameCts?.Cancel(); } catch { }
    }

    // ---------------- 测速页 ----------------

    private void InitSpeedTab()
    {
        CmbSource.ItemsSource = SpeedTest.Sources;
        CmbSource.DisplayMemberPath = "Name";
        CmbSource.SelectedIndex = 0;

        CmbDuration.ItemsSource = new[] { "10 秒", "20 秒", "30 秒" };
        CmbDuration.SelectedIndex = 1;

        CmbConn.ItemsSource = new[] { "1 连接", "2 连接", "4 连接", "8 连接" };
        CmbConn.SelectedIndex = 2;   // 默认 4 连接

        ChartCanvas.SizeChanged += (_, _) => RedrawChart();
        ChartCanvas.Background = Brushes.White;
        RedrawChart();
    }

    /// <summary>每次测速结束追加一行到 data\speed.csv（首行自动写表头）</summary>
    private static void AppendSpeedCsv(SpeedResult r)
    {
        try
        {
            AppPaths.Ensure();
            var file = System.IO.Path.Combine(AppPaths.DataDir, "speed.csv");
            bool needHeader = !File.Exists(file);
            var sb = new StringBuilder();
            if (needHeader) sb.AppendLine("时间,测速源,连接数,平均Mbps,峰值Mbps,下载MB,时长秒");
            sb.AppendLine(string.Join(",",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Csv(r.Source),
                r.Connections.ToString(),
                r.AverageMbps.ToString("F1"),
                r.PeakMbps.ToString("F1"),
                (r.Bytes / 1048576.0).ToString("F1"),
                r.Seconds.ToString("F1")));
            File.AppendAllText(file, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex) { AppPaths.Log("写 speed.csv 失败: " + ex.Message); }
    }

    private async void OnStartSpeed(object sender, RoutedEventArgs e)
    {
        if (_speedRunning) return;

        var source = CmbSource.SelectedItem as SpeedSource ?? SpeedTest.Sources[0];
        _speedWindow = CmbDuration.SelectedIndex switch { 0 => 10, 2 => 30, _ => 20 };
        int conns = CmbConn.SelectedIndex switch { 0 => 1, 1 => 2, 3 => 8, _ => 4 };

        _samples.Clear();
        _speedRunning = true;
        BtnSpeedStart.IsEnabled = false;
        BtnSpeedStop.IsEnabled = true;
        CmbSource.IsEnabled = false;
        CmbDuration.IsEnabled = false;
        CmbConn.IsEnabled = false;
        TxtSpeedNow.Text = "0.0";
        TxtSpeedAvg.Text = "平均 —";
        TxtSpeedBytes.Text = "已下载 —";
        TxtSpeedPing.Text = "延迟 检测中…";
        RedrawChart();
        AppPaths.Log($"测速开始 source={source.Name} duration={_speedWindow}s conns={conns}");

        _speedCts = new CancellationTokenSource();

        try
        {
            int ping = await SpeedTest.PingAsync(source.Url);
            TxtSpeedPing.Text = ping >= 0 ? $"延迟 {ping} ms" : "延迟 失败";

            var progress = new Progress<SpeedSample>(s =>
            {
                _samples.Add(s);
                TxtSpeedNow.Text = s.Mbps.ToString("F1");
                TxtSpeedBytes.Text = $"已下载 {s.TotalBytes / 1024.0 / 1024.0:F1} MB";
                double avg = s.TotalBytes * 8.0 / Math.Max(s.Seconds, 0.1) / 1_000_000.0;
                TxtSpeedAvg.Text = $"平均 {avg:F1} Mbps";
                RedrawChart();
            });

            var result = await SpeedTest.RunAsync(source, TimeSpan.FromSeconds(_speedWindow), conns, progress, _speedCts.Token);

            TxtSpeedNow.Text = result.AverageMbps.ToString("F1");
            TxtSpeedAvg.Text = $"平均 {result.AverageMbps:F1} Mbps   峰值 {result.PeakMbps:F1}   {result.Connections} 连接";
            TxtSpeedBytes.Text = $"已下载 {result.Bytes / 1024.0 / 1024.0:F1} MB / {result.Seconds:F1} 秒";
            TxtFoot.Text = result.Cancelled
                ? "测速已停止"
                : $"√ 测速完成：平均 {result.AverageMbps:F1} Mbps（{result.Source}）";
            AppPaths.Log($"测速完成 avg={result.AverageMbps:F1} peak={result.PeakMbps:F1} bytes={result.Bytes} sec={result.Seconds:F1} cancelled={result.Cancelled}");
            AppendSpeedCsv(result);
        }
        catch (Exception ex)
        {
            TxtFoot.Text = "× 测速失败：" + ex.Message;
            AppPaths.Log("测速异常: " + ex);
            MessageBox.Show("测速失败：\n" + ex.Message + "\n\n换个测速源或稍后再试。", "失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _speedRunning = false;
            BtnSpeedStart.IsEnabled = true;
            BtnSpeedStop.IsEnabled = false;
            CmbSource.IsEnabled = true;
            CmbDuration.IsEnabled = true;
            CmbConn.IsEnabled = true;
            _speedCts?.Dispose();
            _speedCts = null;
        }
    }

    private void OnStopSpeed(object sender, RoutedEventArgs e)
    {
        if (_speedCts == null) return;
        TxtFoot.Text = "正在停止…";
        try { _speedCts.Cancel(); } catch { }
    }

    /// <summary>XY 折线图：横轴时间、纵轴 Mbps，边测边画</summary>
    private void RedrawChart()
    {
        ChartCanvas.Children.Clear();

        double w = ChartCanvas.ActualWidth;
        double h = ChartCanvas.ActualHeight;
        if (w < 60 || h < 60) return;

        const double left = 58, right = 16, top = 16, bottom = 30;
        double pw = w - left - right;
        double ph = h - top - bottom;

        // 纵轴刻度：按已观测峰值取整
        double peak = _samples.Count > 0 ? _samples.Max(s => s.Mbps) : 0;
        double maxY = NiceCeil(Math.Max(peak * 1.2, 5));

        var axisBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDB, 0xE5));
        var gridBrush = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF6));
        var textBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA3));

        // 网格 + 纵轴标签
        for (int i = 0; i <= 4; i++)
        {
            double y = top + ph * i / 4.0;
            ChartCanvas.Children.Add(new Line
            {
                X1 = left, Y1 = y, X2 = left + pw, Y2 = y,
                Stroke = i == 4 ? axisBrush : gridBrush,
                StrokeThickness = 1
            });
            var label = new TextBlock
            {
                Text = (maxY * (4 - i) / 4.0).ToString("0.#"),
                FontSize = 11,
                Foreground = textBrush,
                FontFamily = new FontFamily("Consolas")
            };
            Canvas.SetLeft(label, 8);
            Canvas.SetTop(label, y - 8);
            ChartCanvas.Children.Add(label);
        }

        // 纵轴单位（放右上角，避免和图顶刻度打架）
        var unit = new TextBlock
        {
            Text = "Mbps", FontSize = 11, Foreground = textBrush
        };
        Canvas.SetLeft(unit, left + pw - 46);
        Canvas.SetTop(unit, top - 15);
        ChartCanvas.Children.Add(unit);

        // 横轴：时间刻度
        double window = Math.Max(_speedWindow, 1);
        for (int i = 0; i <= 4; i++)
        {
            double x = left + pw * i / 4.0;
            var t = new TextBlock
            {
                Text = (window * i / 4.0).ToString("0") + "s",
                FontSize = 11,
                Foreground = textBrush,
                FontFamily = new FontFamily("Consolas")
            };
            Canvas.SetLeft(t, x - 8);
            Canvas.SetTop(t, top + ph + 6);
            ChartCanvas.Children.Add(t);
        }

        // 折线
        if (_samples.Count >= 2)
        {
            var poly = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            };
            foreach (var s in _samples)
            {
                double x = left + Math.Clamp(s.Seconds / window, 0, 1) * pw;
                double y = top + ph - Math.Clamp(s.Mbps / maxY, 0, 1) * ph;
                poly.Points.Add(new Point(x, y));
            }
            ChartCanvas.Children.Add(poly);

            // 末端小圆点
            var last = poly.Points[^1];
            var dot = new Ellipse
            {
                Width = 7, Height = 7,
                Fill = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED))
            };
            Canvas.SetLeft(dot, last.X - 3.5);
            Canvas.SetTop(dot, last.Y - 3.5);
            ChartCanvas.Children.Add(dot);
        }
        else
        {
            var hint = new TextBlock
            {
                Text = _speedRunning ? "正在采集数据…" : "点「开始测速」，这里会实时画出速度曲线",
                FontSize = 13,
                Foreground = textBrush
            };
            Canvas.SetLeft(hint, left + 10);
            Canvas.SetTop(hint, top + ph / 2 - 10);
            ChartCanvas.Children.Add(hint);
        }
    }

    /// <summary>把上限取成 5/10/20/25/50/100… 这种好读的数</summary>
    private static double NiceCeil(double v)
    {
        double[] steps = { 1, 2, 5, 10, 20, 25, 50, 75, 100, 150, 200, 300, 500, 1000 };
        foreach (var s in steps)
            if (v <= s) return s;
        return Math.Ceiling(v / 100.0) * 100;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x98)),
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0)
    };
}
