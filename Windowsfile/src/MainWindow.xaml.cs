using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NetPanel.Services;
using Microsoft.Win32;
using System.IO.Compression;

namespace NetPanel;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(60) };
    // 网卡热插拔轮询（8 秒一次，只查名字，很轻）
    // 网卡页改成纯手动：不再 8 秒轮询，进页查一次 + 点「刷新」才查
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
    private readonly DispatcherTimer _trafficTick = new() { Interval = TimeSpan.FromSeconds(60) };
    // 无线频段页：2 秒一次 iw 读取（只读，离页即停）
    // 页面级自动刷新节拍：设备 3 分钟、连接 3 分钟、日志 10 分钟（只对当前页生效）
    private readonly DispatcherTimer _pageTick = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTime _devAutoStamp = DateTime.MinValue;
    private DateTime _connAutoStamp = DateTime.MinValue;
    private DateTime _logAutoStamp = DateTime.MinValue;

    private readonly DispatcherTimer _apTick = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _apBusy;
    private static readonly SolidColorBrush ApGray = new(Color.FromRgb(0x9A, 0xA3, 0xB2));
    private static readonly SolidColorBrush ApWarn = new(Color.FromRgb(0xC0, 0x50, 0x2F));
    private static readonly SolidColorBrush ApInk = new(Color.FromRgb(0x1F, 0x27, 0x33));
    private static readonly SolidColorBrush ApSub = new(Color.FromRgb(0x6B, 0x74, 0x82));

    // 代理检测：本地层 10 秒（零联网），出口/延迟 60 秒
    private readonly DispatcherTimer _proxyLocalTick = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _proxyRemoteTick = new() { Interval = TimeSpan.FromSeconds(60) };
    private string _pLocal = "", _pRemote = "", _pLat = "";
    private bool _pLocalBusy, _pRemoteBusy;
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
        TxtBadge.Text = $"{Ver.Text()} · 网段自适应";   // 版本号跟着程序集走，不再写死
        AppPaths.Log($"窗口创建  {Ver.Text()}  数据目录={AppPaths.DataDir}");

        Loaded += async (_, _) =>
        {
            TxtFoot.Text = "正在初始化…";
            TxtFoot.ToolTip = $"数据目录：{AppPaths.DataDir}";
            await RefreshAsync();
            await LoadSavedWifiAsync();
            LoadLog();
            _timer.Start();

            if (App.DeployWin != null)   // --deploywin=主机：直接开部署窗口（截图/自测）
            {
                var host = string.IsNullOrWhiteSpace(App.DeployWin)
                    ? RemoteConfig.Load().Host
                    : App.DeployWin!;
                new DeployWindow(host, RemoteConfig.Load().User) { Owner = this }.Show();
            }
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        _nicPoll.Tick += async (_, _) => await PollAdaptersAsync();   // 已停用（网卡页改手动），保留以免空引用
        InitSpeedTab();
        Activated += (_, _) =>
        {
            if (CurrentPage == "状态" && (DateTime.Now - _lastNicProbe).TotalSeconds > 25)
                _ = WatchNicsAsync();
            if (CurrentPage == "设备") _ = RefreshScanCidrAsync(false);   // 换网回来顺手校正
        };
        InitGameTab();
        InitOtherTab();
        LoadCampusUi();
        InitSites();
        InitProxyTab();
        InitApTab();
        InitUpd();

        if (App.StartPage is string spRaw)
        {
            var sp = spRaw.Trim();
            sp = sp.ToLowerInvariant() switch
            {
                "ap" or "wifi" => "无线频段",
                "other" => "其他",
                "status" => "状态",
                "log" or "logs" => "日志",
                "nic" => "网卡",
                "device" => "设备",
                "traffic" => "流量",
                "conn" => "连接",
                "campus" => "校园网",
                _ => sp,
            };
            var matched = false;
            foreach (var item in NavTabs.Items)
            {
                if (item is TabItem ti &&
                    (System.Windows.Automation.AutomationProperties.GetName(ti) ?? "") == sp)
                {
                    NavTabs.SelectedItem = ti;
                    matched = true;
                    break;
                }
            }
            AppPaths.Log($"[启动] --page={spRaw} 匹配={matched}");
        }
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
        _pageTick.Tick += async (_, _) => await PageTickAsync();
        _pageTick.Start();
        _trafficTick.Start();
        Traffic.Sample();                 // 第一次只记基线
        RefreshTrafficView();
    }

    /// <summary>按页面给自动刷新节拍：设备 3 分钟、连接 3 分钟、日志 10 分钟；其它页不动</summary>
    private async Task PageTickAsync()
    {
        try
        {
            var page = CurrentPage;
            var now = DateTime.Now;

            if (page == "设备" && (now - _devAutoStamp).TotalSeconds >= 180)
            {
                _devAutoStamp = now;
                await RefreshDevStateAsync();          // 先拉黑名单/流量（轻）
                await ScanDevicesAsync(false, true);   // 再快速扫一遍（温和，不发大包）
            }
            else if (page == "连接" && (now - _connAutoStamp).TotalSeconds >= 180)
            {
                _connAutoStamp = now;
                OnConnRefresh(this, new RoutedEventArgs());
            }
            else if (page == "流量" && (now - _devTrafficStamp).TotalSeconds >= 60)
            {
                await LoadDevTrafficAsync(false);
            }
            else if (page == "日志" && (now - _logAutoStamp).TotalSeconds >= 600)
            {
                _logAutoStamp = now;
                LoadLog();
            }
        }
        catch (Exception ex) { AppPaths.Log("[自动刷新] " + ex.Message); }
    }

    public sealed class DevTrafficRow
    {
        public string Name { get; set; } = "";
        public string Mac { get; set; } = "";
        public string Ip { get; set; } = "";
        public string State { get; set; } = "";
        public string Share { get; set; } = "";
        public string TodayUp { get; set; } = "";
        public string TodayDown { get; set; } = "";
        public string SpanUp { get; set; } = "";
        public string SpanDown { get; set; } = "";
        public string Last { get; set; } = "";
        public long _todayBytes { get; set; }
    }

    private static readonly Dictionary<string, string> _devNames = new(StringComparer.OrdinalIgnoreCase);
    private static bool _devNamesLoaded;

    private readonly long[] _devHourDown = new long[24];
    private readonly long[] _devHourUp = new long[24];
    private bool _devHourHasData;

    private DateTime _devTrafficStamp = DateTime.MinValue;

    private string DevNamesFile => System.IO.Path.Combine(AppPaths.DataDir, "devnames.json");

    private void LoadDevNames()
    {
        if (_devNamesLoaded) return;
        _devNamesLoaded = true;
        try
        {
            var f = DevNamesFile;
            if (!File.Exists(f)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value.GetString();
                if (!string.IsNullOrWhiteSpace(v)) _devNames[p.Name] = v!;
            }
        }
        catch { }
    }

    private void SaveDevNames()
    {
        try
        {
            var dict = _devNames.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(x => x.Key, x => x.Value);
            File.WriteAllText(DevNamesFile, System.Text.Json.JsonSerializer.Serialize(dict,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>给没名字的设备做 PTR 反查（最多 8 台，1.2 秒超时），结果缓存到 data\devnames.json</summary>
    private async Task FillDevNamesAsync(List<(string Mac, string Ip)> need, string gateway)
    {
        if (need.Count == 0) return;
        var gate = new SemaphoreSlim(4);
        var got = 0;
        var tasks = need.Take(8).Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                var nm = await LanScan.PtrNameAsync(gateway, item.Ip);
                if (!string.IsNullOrWhiteSpace(nm) && !_devNames.ContainsKey(item.Mac))
                {
                    _devNames[item.Mac] = nm;
                    Interlocked.Increment(ref got);
                }
            }
            catch { }
            finally { gate.Release(); }
        }).ToList();
        try { await Task.WhenAll(tasks); } catch { }
        if (got > 0) { SaveDevNames(); AppPaths.Log($"[流量] 设备名补到 {got} 个"); }
    }

    // ---------------- 网口角色（WAN / LAN）----------------

    private bool _rolesLoading;

    private async Task LoadRolesAsync()
    {
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) { TxtRoleInfo.Text = "软路由没连上"; return; }

        _rolesLoading = true;
        try
        {
            var (ok, detail) = await RouterCtl.RunAsync(cfg, "roles-get");
            if (!ok) { TxtRoleInfo.Text = "× " + detail; return; }

            string mode = "auto", wan = "", lan = "", wanAuto = "", lanAuto = "";
            var ifs = new List<(string Name, string Kind, string State, string Ip)>();
            foreach (var raw in detail.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("MODE=")) mode = line.Substring(5);
                else if (line.StartsWith("WAN_IF=")) wan = line.Substring(7);
                else if (line.StartsWith("LAN_IF=")) lan = line.Substring(7);
                else if (line.StartsWith("WAN_AUTO=")) wanAuto = line.Substring(9);
                else if (line.StartsWith("LAN_AUTO=")) lanAuto = line.Substring(9);
                else if (line.StartsWith("IF="))
                {
                    var parts = line.Substring(3).Split('|');
                    if (parts.Length >= 4)
                        ifs.Add((parts[0], parts[1], parts[3], parts.Length > 4 ? parts[4] : ""));
                }
            }

            CmbRoleWan.Items.Clear();
            CmbRoleLan.Items.Clear();
            foreach (var it in ifs)
            {
                var tag = it.Kind == "wifi" ? "无线" : "有线";
                var st = it.State == "AP" ? " · AP" : (it.State == "-" ? "" : " · " + it.State);
                var label = $"{it.Name}（{tag}{st}）";
                CmbRoleWan.Items.Add(new ComboBoxItem { Content = label, Tag = it.Name });
                CmbRoleLan.Items.Add(new ComboBoxItem { Content = label, Tag = it.Name });
            }
            SelectRole(CmbRoleWan, wan);
            SelectRole(CmbRoleLan, lan);

            TxtRoleInfo.Text = mode == "manual"
                ? $"手动：WAN={wan} LAN={lan}（自动探测：{wanAuto} / {lanAuto}）"
                : $"自动：WAN={wan} LAN={lan}";
        }
        catch (Exception ex) { TxtRoleInfo.Text = "× 解析失败：" + ex.Message; }
        finally { _rolesLoading = false; }
    }

    // ---------------- 兜底恢复 ----------------

    private bool _rescuing;

    private async void OnRescue(object sender, RoutedEventArgs e)
    {
        if (_rescuing) return;
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) { MessageBox.Show("还没配置软路由地址。", "恢复上网"); return; }

        var r = MessageBox.Show(
            "会做这些事：\n\n" +
            "① 关掉「自动切换」（免得边修边被切走）\n" +
            "② 删掉面板加的临时默认路由，交回系统\n" +
            "③ 主网口没路由就重连一次\n" +
            "④ 校园网重新认证一次\n\n" +
            "不会重启、不会断 Wi-Fi 热点，随时可以再开自动切换。\n\n继续？",
            "恢复上网（兜底重置）", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
        if (r != MessageBoxResult.Yes) return;

        _rescuing = true; BtnRescue.IsEnabled = false;
        BtnRescue.Content = "恢复中…";
        try
        {
            var (ok, detail) = await RouterCtl.RunAsync(cfg, "rescue",
                msg => AppPaths.Log("[恢复] " + msg));
            AppPaths.Log($"[恢复] 结果：{(ok ? "成功" : "失败")} · {detail}");
            MessageBox.Show(detail, ok ? "恢复完成" : "恢复没完全成功",
                            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (ok) { await RefreshAsync(true); }
        }
        finally
        {
            BtnRescue.Content = "恢复上网";
            BtnRescue.IsEnabled = true;
            _rescuing = false;
        }
    }

    // ---------------- 多 WAN 故障转移 ----------------

    private bool _wanFailEnabled;
    private bool _wanFailBusy;

    private async Task LoadWanFailAsync()
    {
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) { TxtWanFailInfo.Text = "软路由没连上"; return; }
        var (ok, detail) = await RouterCtl.RunAsync(cfg, "wanfail-get");
        if (!ok) { TxtWanFailInfo.Text = "× " + detail; return; }

        _wanFailEnabled = false; var prefer = "auto"; var active = "";
        var rows = new List<(string Iface, bool Up, string Lat)>();
        var last = ""; var reason = "";
        foreach (var raw in detail.Split('\n'))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var k = line.Substring(0, eq); var v = line.Substring(eq + 1);
            if (k == "ENABLE") _wanFailEnabled = v == "1";
            else if (k == "PREFER") prefer = v;
            else if (k == "ACTIVE") active = v;
            else if (k == "LAST_SWITCH") last = v;
            else if (k == "LAST_REASON") reason = v;
            else if (k.StartsWith("WAN"))
            {
                var f = v.Split('|');
                if (f.Length >= 3) rows.Add((f[0], f[1] == "1", f[2]));
            }
        }

        BtnWanFailToggle.Content = _wanFailEnabled ? "关闭自动切换" : "开启自动切换";

        // 优先网口下拉：auto + 所有候选
        var keep = (CmbWanPrefer.SelectedItem as ComboBoxItem)?.Tag as string ?? prefer;
        CmbWanPrefer.Items.Clear();
        CmbWanPrefer.Items.Add(new ComboBoxItem { Content = "自动（按优先级）", Tag = "auto" });
        foreach (var r in rows) CmbWanPrefer.Items.Add(new ComboBoxItem { Content = r.Iface, Tag = r.Iface });
        foreach (ComboBoxItem it in CmbWanPrefer.Items)
            if ((string?)it.Tag == keep) { CmbWanPrefer.SelectedItem = it; break; }
        if (CmbWanPrefer.SelectedItem == null && CmbWanPrefer.Items.Count > 0) CmbWanPrefer.SelectedIndex = 0;

        var parts = rows.Select(r => $"{r.Iface} {(r.Up ? "√ " + r.Lat + " ms" : "×")}").ToList();
        var list = parts.Count == 0 ? "没发现能上网的网口" : string.Join("  ·  ", parts);
        var cur = string.IsNullOrEmpty(active) ? "跟随系统" : active;
        TxtWanFailInfo.Text = $"候选：{list}    当前出口：{cur}"
            + (_wanFailEnabled ? "" : "（自动切换未开）")
            + (string.IsNullOrEmpty(last) ? "" : $"    最近切换 {last}（{reason}）");
    }

    private async void OnWanFailToggle(object sender, RoutedEventArgs e)
    {
        if (_wanFailBusy) return;
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) { TxtWanFailInfo.Text = "软路由没连上"; return; }
        _wanFailBusy = true; BtnWanFailToggle.IsEnabled = false;
        var (ok, detail) = await RouterCtl.RunAsync(cfg, _wanFailEnabled ? "wanfail-set disable" : "wanfail-set enable");
        BtnWanFailToggle.IsEnabled = true; _wanFailBusy = false;
        if (!ok) { TxtWanFailInfo.Text = "× " + detail; return; }
        AppPaths.Log($"[多WAN] {(_wanFailEnabled ? "关闭" : "开启")}自动切换");
        await LoadWanFailAsync();
    }

    private async void OnWanToBackup(object sender, RoutedEventArgs e)   // 切到所选
    {
        if (_wanFailBusy) return;
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) return;
        var sel = (CmbWanPrefer.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        _wanFailBusy = true; BtnWanToBackup.IsEnabled = false;
        var (ok, detail) = await RouterCtl.RunAsync(cfg, "wanfail-switch " + sel);
        BtnWanToBackup.IsEnabled = true; _wanFailBusy = false;
        TxtWanFailInfo.Text = ok ? "√ " + detail.Trim() : "× " + detail;
        AppPaths.Log($"[多WAN] 切到 {sel} → {(ok ? "成功" : detail)}");
        await LoadWanFailAsync();
    }

    private async void OnWanToPrimary(object sender, RoutedEventArgs e)  // 恢复自动
    {
        if (_wanFailBusy) return;
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) return;
        _wanFailBusy = true; BtnWanToPrimary.IsEnabled = false;
        await RouterCtl.RunAsync(cfg, "wanfail-set prefer auto");
        var (ok, detail) = await RouterCtl.RunAsync(cfg, "wanfail-switch auto");
        BtnWanToPrimary.IsEnabled = true; _wanFailBusy = false;
        TxtWanFailInfo.Text = ok ? "√ 已交回系统路由" : "× " + detail;
        AppPaths.Log("[多WAN] 恢复自动");
        await LoadWanFailAsync();
    }

    private static void SelectRole(ComboBox cb, string iface)
    {
        foreach (ComboBoxItem item in cb.Items)
            if ((string?)item.Tag == iface) { cb.SelectedItem = item; return; }
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
    }

    private async void OnRoleSave(object sender, RoutedEventArgs e)
    {
        if (_rolesLoading) return;
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) { TxtRoleInfo.Text = "软路由没连上"; return; }
        var wan = (CmbRoleWan.SelectedItem as ComboBoxItem)?.Tag as string;
        var lan = (CmbRoleLan.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(wan) || string.IsNullOrWhiteSpace(lan))
        { TxtRoleInfo.Text = "先选好两个网口"; return; }

        BtnRoleSave.IsEnabled = false;
        TxtRoleInfo.Text = "保存中…";
        var (ok, detail) = await RouterCtl.RunAsync(cfg, $"roles-set {wan} {lan}");
        BtnRoleSave.IsEnabled = true;
        TxtRoleInfo.Text = ok ? "√ " + detail.Trim() : "× " + detail;
        AppPaths.Log($"[网口] 手动指定 WAN={wan} LAN={lan} → {(ok ? "成功" : detail)}");
        if (ok) await LoadRolesAsync();
    }

    private async void OnRoleAuto(object sender, RoutedEventArgs e)
    {
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) { TxtRoleInfo.Text = "软路由没连上"; return; }
        BtnRoleAuto.IsEnabled = false;
        var (ok, detail) = await RouterCtl.RunAsync(cfg, "roles-set auto");
        BtnRoleAuto.IsEnabled = true;
        TxtRoleInfo.Text = ok ? "√ 已恢复自动" : "× " + detail;
        AppPaths.Log("[网口] 恢复自动探测");
        if (ok) await LoadRolesAsync();
    }

    /// <summary>设备流量：sample=true 时先让远端立刻采一次，再取汇总</summary>
    private async Task LoadDevTrafficAsync(bool sample)
    {
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) { TxtDevTrafficInfo.Text = "软路由没连上"; return; }

        TxtDevTrafficInfo.Text = sample ? "正在采样…" : "读取中…";
        var detail = "";
        LoadDevNames();
        try
        {
            var (ok, d) = await RouterCtl.RunAsync(cfg, sample ? "traffic-now 30" : "traffic-summary 30");
            detail = d;
            if (!ok) { TxtDevTrafficInfo.Text = "× " + detail; return; }

            using var doc = System.Text.Json.JsonDocument.Parse(detail.Trim());
            var rows = new List<DevTrafficRow>();
            var pending = new List<(string Mac, string Ip)>();
            foreach (var el in doc.RootElement.GetProperty("devices").EnumerateArray())
            {
                var mac = el.GetProperty("mac").GetString() ?? "";
                var name = (el.GetProperty("name").GetString() ?? "").Trim();
                if (name.Length == 0)
                    name = (_devices.FirstOrDefault(x => string.Equals(x.Mac, mac,
                        StringComparison.OrdinalIgnoreCase))?.Name ?? "").Trim();
                if (name.Length == 0 && _devNames.TryGetValue(mac, out var cachedName)) name = cachedName;
                var ipv = el.GetProperty("ip").GetString() ?? "";
                if (name.Length == 0 && ipv.Length > 0) pending.Add((mac, ipv));
                var online = el.GetProperty("online").GetInt32() == 1;
                rows.Add(new DevTrafficRow
                {
                    Name = name.Length > 0 ? name : mac,
                    Mac = mac,
                    Ip = ipv,
                    State = online ? "在线" : "离线",
                    TodayUp = FmtShort(el.GetProperty("today_up").GetInt64()),
                    TodayDown = FmtShort(el.GetProperty("today_down").GetInt64()),
                    SpanUp = FmtShort(el.GetProperty("span_up").GetInt64()),
                    SpanDown = FmtShort(el.GetProperty("span_down").GetInt64()),
                    Last = el.GetProperty("last").GetString() ?? "",
                    _todayBytes = el.GetProperty("today_down").GetInt64() + el.GetProperty("today_up").GetInt64(),
                });
            }
            if (pending.Count > 0)
            {
                // ① 先问远端 DHCP 租约（最准：手机=机型名，电脑=计算机名）
                var (nok, ndetail) = await RouterCtl.RunAsync(cfg, "dev-names");
                if (nok)
                {
                    var added = 0;
                    foreach (var line in ndetail.Split('\n'))
                    {
                        var parts = line.Trim().Split('\t');
                        if (parts.Length < 3) continue;
                        var m2 = parts[0].Trim().ToLowerInvariant();
                        var n2 = parts[2].Trim();
                        if (m2.Length == 0 || n2.Length == 0 || n2 == "*") continue;
                        if (!_devNames.ContainsKey(m2)) { _devNames[m2] = n2; added++; }
                    }
                    if (added > 0) { SaveDevNames(); AppPaths.Log($"[流量] 租约里读到 {added} 个设备名"); }
                }
                // ② 还没名字的再 PTR 兜底
                await FillDevNamesAsync(pending.Where(x => !_devNames.ContainsKey(x.Mac)).ToList(), cfg.Host);
                foreach (var r in rows)
                    if (_devNames.TryGetValue(r.Mac, out var nm2) && nm2.Length > 0) r.Name = nm2;
            }
            DgDevTraffic.ItemsSource = rows;

            Array.Clear(_devHourDown);
            Array.Clear(_devHourUp);
            _devHourHasData = false;
            long todayD = 0, todayU = 0, spanD = 0, spanU = 0;
            foreach (var el in doc.RootElement.GetProperty("devices").EnumerateArray())
            {
                todayD += el.GetProperty("today_down").GetInt64();
                todayU += el.GetProperty("today_up").GetInt64();
                spanD += el.GetProperty("span_down").GetInt64();
                spanU += el.GetProperty("span_up").GetInt64();
            }
            if (doc.RootElement.TryGetProperty("hours", out var hrs))
            {
                foreach (var h in hrs.EnumerateArray())
                {
                    var idx = h.GetProperty("h").GetInt32();
                    if (idx < 0 || idx > 23) continue;
                    _devHourDown[idx] = h.GetProperty("down").GetInt64();
                    _devHourUp[idx] = h.GetProperty("up").GetInt64();
                    if (_devHourDown[idx] > 0 || _devHourUp[idx] > 0) _devHourHasData = true;
                }
            }
            var totalToday = todayD + todayU;
            foreach (var r in rows)
            {
                var v = r._todayBytes;
                r.Share = totalToday <= 0 ? "—" : $"{v * 100.0 / totalToday:F1}%";
            }
            DgDevTraffic.ItemsSource = null;
            DgDevTraffic.ItemsSource = rows;

            TxtTrafficInfo.Text =
                $"今天 ↓{Traffic.SizeText(todayD / 1048576.0)} ↑{Traffic.SizeText(todayU / 1048576.0)}" +
                $"   近 30 天 ↓{Traffic.SizeText(spanD / 1048576.0)} ↑{Traffic.SizeText(spanU / 1048576.0)}" +
                $"   ·   每小时自动采一次 · 保留 32 天";
            RenderTrafficChart();

            TxtDevTrafficInfo.Text = $"√ {rows.Count} 台 · {DateTime.Now:HH:mm:ss} 更新";
            _devTrafficStamp = DateTime.Now;
        }
        catch (Exception ex)
        {
            TxtDevTrafficInfo.Text = "× 解析失败，看日志";
            AppPaths.Log("[流量] 设备流量解析失败：" + ex.Message + " 原文=" + detail);
        }
        finally { }
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
                TxtTrafficLive.Text = $"本机 ↓{Traffic.SizeText(d)}  ↑{Traffic.SizeText(u)}";
            }
            else TxtTrafficLive.Text = "本机空闲";

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
                "   ·   软路由没连上，图上暂时是本机网卡数据";

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

        double[][] buckets;
        if (_devHourHasData)
        {
            buckets = new double[24][];
            for (int i = 0; i < 24; i++)
                buckets[i] = new[] { _devHourDown[i] / 1048576.0, _devHourUp[i] / 1048576.0 };
        }
        else buckets = Traffic.HourlyBuckets(DateTime.Today);
        double max = 0;
        foreach (var b in buckets) max = Math.Max(max, Math.Max(b[0], b[1]));
        if (max <= 0) max = 0.1;

        const double padLeft = 56, padRight = 10, padTop = 8, padBottom = 30;
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

        // Y 轴刻度：0 必画；顶部总量刻度如果会和「最新小时」的标注撞位，就不画（避免糊成一团）
        int _lastHour = -1;
        for (int i = 0; i < 24; i++)
            if (buckets[i][0] > 0 || buckets[i][1] > 0) _lastHour = i;
        bool topBusy = _lastHour >= 0 &&
                       Math.Max(buckets[_lastHour][0], buckets[_lastHour][1]) >= max * 0.85;
        if (!topBusy) AddCanvasText(Traffic.SizeText(max), 4, padTop - 4, 11, "#9AA5B4");
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

        }

        // ---- 左侧 Y 轴：把最右边那组柱子（最新一小时）的数值标上去 ----
        {
            int lastHour = -1;
            for (int i = 0; i < 24; i++)
                if (buckets[i][0] > 0 || buckets[i][1] > 0) lastHour = i;

            if (lastHour >= 0)
            {
                double vd = buckets[lastHour][0], vu = buckets[lastHour][1];
                double baseY = padTop + plotH;

                void YLabel(double val, Color col, bool isDown)
                {
                    if (val <= 0) return;
                    double y = baseY - val / max * plotH;
                    var tb = new TextBlock
                    {
                        Text = Traffic.SizeText(val),
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(col),
                    };
                    tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(tb, Math.Max(0, padLeft - 8 - tb.DesiredSize.Width));
                    Canvas.SetTop(tb, Math.Max(0, y - tb.DesiredSize.Height / 2));
                    CvTraffic.Children.Add(tb);

                    CvTraffic.Children.Add(new System.Windows.Shapes.Line
                    {
                        X1 = padLeft - 5, X2 = padLeft, Y1 = y, Y2 = y,
                        Stroke = new SolidColorBrush(col), StrokeThickness = 1,
                    });
                }

                YLabel(vd, Color.FromRgb(0x2F, 0xB8, 0x6B), true);
                YLabel(vu, Color.FromRgb(0x2F, 0x6F, 0xED), false);
            }
        }

        // ---- 时间轴：底部基线 + 每个整点标签（窄了自动隔一个/隔两个） ----
        CvTraffic.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = padLeft, X2 = padLeft + plotW,
            Y1 = padTop + plotH, Y2 = padTop + plotH,
            Stroke = new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE8)),
            StrokeThickness = 1,
        });

        var label = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        int step = slot >= 40 ? 1 : slot >= 26 ? 2 : 3;
        for (int hour = 0; hour < 24; hour += step)
        {
            var txt = $"{hour:D2}:00";
            var ft = new FormattedText(txt, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, label, 10.5, new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB4)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double cx = padLeft + hour * slot + slot / 2;
            double x = Math.Max(padLeft - 2, Math.Min(padLeft + plotW - ft.Width, cx - ft.Width / 2));
            var t = new TextBlock { Text = txt, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB4)) };
            Canvas.SetLeft(t, x);
            Canvas.SetTop(t, padTop + plotH + 7);
            CvTraffic.Children.Add(t);
        }

        // 每小时竖线（淡淡的，帮眼睛对齐柱子）
        if (slot >= 30)
        {
            for (int hour = 1; hour < 24; hour++)
            {
                double x = padLeft + hour * slot;
                CvTraffic.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = x, X2 = x, Y1 = padTop, Y2 = padTop + plotH,
                    Stroke = new SolidColorBrush(Color.FromRgb(0xF3, 0xF5, 0xF9)),
                    StrokeThickness = 1,
                });
            }
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

    private async void OnTrafficRefresh(object sender, RoutedEventArgs e)
    {
        var rows = Traffic.Sample();
        if (rows.Count > 0) Traffic.Append(rows);
        TxtTrafficLive.Text = "已刷新 " + DateTime.Now.ToString("HH:mm:ss");
        await LoadDevTrafficAsync(true);       // 远端立刻采一次 + 取汇总，图也跟着重画
    }

    /// <summary>导出当前设备流量表（CSV，位置自选）</summary>
    private void OnTrafficExport(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DgDevTraffic.ItemsSource is not System.Collections.IEnumerable src)
            { TxtDevTrafficInfo.Text = "没有数据可导出"; return; }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出设备流量",
                Filter = "CSV 文件|*.csv",
                FileName = $"设备流量-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            };
            if (dlg.ShowDialog() != true) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("设备,IP,状态,今日上行,今日下行,占比,近30天上行,近30天下行,最后在线");
            int n = 0;
            foreach (var item in src)
            {
                if (item is not DevTrafficRow r) continue;
                sb.AppendLine($"{r.Name},{r.Ip},{r.State},{r.TodayUp},{r.TodayDown},{r.Share},{r.SpanUp},{r.SpanDown},{r.Last}");
                n++;
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
            TxtDevTrafficInfo.Text = $"√ 已导出 {n} 台 → {System.IO.Path.GetFileName(dlg.FileName)}";
            AppPaths.Log($"[流量] 导出 {n} 台 → {dlg.FileName}");
        }
        catch (Exception ex)
        {
            TxtDevTrafficInfo.Text = "× 导出失败，看日志";
            AppPaths.Log("[流量] 导出失败：" + ex.Message);
        }
    }

    /// <summary>清空软路由上的设备流量历史（二次确认，默认否）</summary>
    private async void OnTrafficClear(object sender, RoutedEventArgs e)
    {
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) { TxtDevTrafficInfo.Text = "软路由没连上"; return; }

        var ans = MessageBox.Show(
            "清空软路由上保存的设备流量历史（最近 32 天）？\n\n" +
            "· 只删流量记录，不动黑名单\n· 不动本机网卡流量\n· 删了找不回来",
            "清空设备流量历史", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (ans != MessageBoxResult.Yes) return;

        TxtDevTrafficInfo.Text = "正在清空…";
        var (ok, detail) = await RouterCtl.RunAsync(cfg, "traffic-reset");
        if (!ok) { TxtDevTrafficInfo.Text = "× " + detail; return; }
        AppPaths.Log("[流量] 已清空远端设备流量历史");
        await LoadDevTrafficAsync(true);
        TxtDevTrafficInfo.Text = "√ 已清空 · " + DateTime.Now.ToString("HH:mm:ss");
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

    private bool _cidrUserEdited;     // 用户手动改过网段就不再自动覆盖
    private bool _settingCidr;        // 程序自己写文本框时的护栏
    private bool _cidrHooked;
    private string _lastCidr = "";

    private async void InitDeviceTab()
    {
        if (!_cidrHooked)
        {
            _cidrHooked = true;
            TxtScanCidr.TextChanged += (_, _) => { if (!_settingCidr) _cidrUserEdited = true; };
        }
        await RefreshScanCidrAsync(force: true);
    }

    /// <summary>按当前网络重新填扫描网段；用户手改过就只提示不改</summary>
    private async Task RefreshScanCidrAsync(bool force)
    {
        if (_cidrUserEdited && !force) return;
        var cidr = await LanScan.GuessLocalCidrAsync();
        if (string.IsNullOrWhiteSpace(cidr)) cidr = "192.168.1.0/24";
        if (cidr == _lastCidr && !force) return;
        _lastCidr = cidr;
        _settingCidr = true;
        TxtScanCidr.Text = cidr;
        _settingCidr = false;
        _cidrUserEdited = false;
    }

    private async void OnScanDevices(object sender, RoutedEventArgs e)
        => await ScanDevicesAsync(ChkDeep.IsChecked == true, false);

    /// <summary>扫描设备（deep=深度扫描；quiet=true 时用于后台自动刷新）</summary>
    private async Task ScanDevicesAsync(bool deep, bool quiet)
    {
        if (_scanCts != null) return;                 // 已经在扫，别叠加
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

        var locals = LanScan.LocalCidrs();
        var cidrMismatch = locals.Count > 0 && !locals.Contains(cidr);
        if (cidrMismatch)
            TxtScanInfo.Text = $"注意：本机在 {string.Join("、", locals)}，扫 {cidr} 大概率 0 台";

        BtnScan.IsEnabled = false;
        BtnScanStop.IsEnabled = true;
        _scanCts = new CancellationTokenSource();
        var cfg = RemoteConfig.Load();

        try
        {
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
            if (list.Count == 0 && cidrMismatch)
                TxtScanInfo.Text += $"　← 本机在 {string.Join("、", locals)}，网段是不是填错了";
            await RefreshDevStateAsync();
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

    private async void OnDevAction(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LanDevice d || string.IsNullOrWhiteSpace(d.Mac)) return;

        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) { TxtScanInfo.Text = "× 「连接」页还没配远端地址"; return; }

        var blocking = !d.Blocked;
        if (blocking)
        {
            var r = MessageBox.Show(
                $"拉黑这台设备？\n\n{d.Name}  {d.Ip}\n{d.Mac}\n\n" +
                "拉黑后它照样连着 WiFi，但完全上不了网。随时可以点「解除」。",
                "拉黑设备", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
        }

        TxtScanInfo.Text = (blocking ? "正在拉黑 " : "正在解除 ") + d.Mac + " …";
        var (ok, detail) = await RouterCtl.RunAsync(cfg, (blocking ? "dev-block " : "dev-unblock ") + d.Mac);
        TxtScanInfo.Text = (ok ? "√ " : "× ") + detail;
        AppPaths.Log($"[设备] {(blocking ? "拉黑" : "解除")} {d.Mac} → {detail}");

        if (ok) await RefreshDevStateAsync();
    }

    private async void OnDevUnblockAll(object sender, RoutedEventArgs e)
    {
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) { TxtScanInfo.Text = "× 「连接」页还没配远端地址"; return; }

        var r = MessageBox.Show("把所有拉黑的设备全部解除？", "全部解除",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;

        TxtScanInfo.Text = "正在全部解除 …";
        var (ok, detail) = await RouterCtl.RunAsync(cfg, "dev-unblock-all");
        TxtScanInfo.Text = (ok ? "√ " : "× ") + detail;
        AppPaths.Log("[设备] 全部解除 → " + detail);
        if (ok) await RefreshDevStateAsync();
    }

    /// <summary>拉一次远端的黑名单/流量计数，刷进表格（不重新扫描）</summary>
    private async Task RefreshDevStateAsync()
    {
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured || _devices.Count == 0) return;

        var (ok, detail) = await RouterCtl.RunAsync(cfg, "dev-list");
        if (!ok)
        {
            if (detail.Contains("不认识") || detail.Contains("没接受"))
                TxtScanInfo.Text += "　（设备管理助手还没装到软路由）";
            AppPaths.Log("[设备] dev-list 失败：" + detail);
            return;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(detail.Trim());
            var map = new Dictionary<string, (bool Blocked, long Up, long Down)>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in doc.RootElement.GetProperty("devices").EnumerateArray())
            {
                var mac = (el.GetProperty("mac").GetString() ?? "").ToLowerInvariant();
                map[mac] = (el.GetProperty("blocked").GetBoolean(),
                            el.GetProperty("up").GetInt64(),
                            el.GetProperty("down").GetInt64());
            }

            foreach (var d in _devices)
            {
                if (map.TryGetValue((d.Mac ?? "").ToLowerInvariant(), out var v))
                {
                    d.Blocked = v.Blocked;
                    d.Traffic = FmtShort(v.Up) + " / " + FmtShort(v.Down);
                }
            }

            DgDevices.ItemsSource = null;
            DgDevices.ItemsSource = _devices;

            var blocked = _devices.Count(x => x.Blocked);
            if (blocked > 0) TxtScanInfo.Text += $"　（已拉黑 {blocked} 台）";
        }
        catch (Exception ex)
        {
            AppPaths.Log("[设备] 解析 dev-list 失败：" + ex.Message + " 原文=" + detail);
            TxtScanInfo.Text += "　（流量/黑名单读取失败，看日志）";
        }
    }

    private static string FmtShort(long b)
    {
        const long G = 1024L * 1024 * 1024, M = 1024L * 1024, K = 1024;
        if (b >= G) return (b / (double)G).ToString("F1") + "G";
        if (b >= M) return (b / (double)M).ToString("F0") + "M";
        if (b >= K) return (b / (double)K).ToString("F0") + "K";
        return b + "B";
    }

    private static string FmtBytes(long b)
    {
        if (b < 1024) return b + " B";
        if (b < 1024L * 1024) return (b / 1024.0).ToString("F0") + " KB";
        if (b < 1024L * 1024 * 1024) return (b / 1024.0 / 1024).ToString("F1") + " MB";
        return (b / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
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
    /// <summary>当前页名（按 AutomationProperties.Name 取，插页不会串位）</summary>
    private string CurrentPage =>
        (NavTabs.SelectedItem as TabItem)?.GetValue(System.Windows.Automation.AutomationProperties.NameProperty) as string ?? "";

    private async void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl) return;
        var page = CurrentPage;

        // 状态页：切进来顺手查一次远端网口（一次 ssh，不常驻）
        if (page == "状态" && (DateTime.Now - _lastNicProbe).TotalSeconds > 25)
            _ = WatchNicsAsync();

        // 代理检测页：进来开始实时刷新，离开就停（省资源）
        if (page == "代理检测") StartProxyWatch();
        else StopProxyWatch();

        // 无线频段页：进页开始 2 秒热检测，离页立刻停
        if (page == "无线频段") StartApWatch();
        else StopApWatch();

        // 流量页：进来读一次设备流量汇总（不采样，秒出）
        if (page == "流量") _ = LoadDevTrafficAsync(false);

        // 设备页：进来按当前网络重填扫描网段（换过网就不会再扫旧网段）
        if (page == "设备") _ = RefreshScanCidrAsync(false);

        // 网卡页：只在看得见时做热插拔轮询（8 秒一次本地查询），切走立刻停
        _nicPoll.Stop();                       // 网卡页：只进页查一次，手动刷新
        if (page == "网卡") { _ = PollAdaptersAsync(); _ = LoadRolesAsync(); _ = LoadWanFailAsync(); }

        // 校园网页：第一次进来，账号空着就从 minipc 的 Config.yaml 读一份（只读，不动配置）
        if (page == "校园网" && TxtCampusUser.Text.Trim().Length == 0 && !_campusPrefilled)
        {
            _campusPrefilled = true;
            _ = PrefillCampusUserAsync();
            _ = RefreshCampusIfacesAsync();
        }

        if (page == "其他") RefreshOtherRuntime();

        if (page != "连接" || _detectTried) return;
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

    private void OnDeploy(object sender, RoutedEventArgs e)
    {
        if (_deployBusy) { ConnLog("× 部署窗口已经开着，别连点"); return; }
        var cfg = CollectRemote();
        if (!cfg.IsConfigured) { ConnLog("× 先把地址和用户名填上"); return; }

        _deployBusy = true;
        BtnDeploy.IsEnabled = false;
        try
        {
            var w = new DeployWindow(cfg.Host, cfg.User) { Owner = this };
            w.Closed += (_, _) =>
            {
                _deployBusy = false;
                BtnDeploy.IsEnabled = true;
                ConnLog("部署窗口已关闭；要核对结果到「状态」页点刷新");
            };
            w.Show();
            ConnLog("已打开一键部署窗口（密码在那里面输，只在本机内存）");
        }
        catch (Exception ex)
        {
            _deployBusy = false;
            BtnDeploy.IsEnabled = true;
            ConnLog("× 打不开部署窗口：" + ex.Message);
        }
    }

    // ---------------- 代理检测 ----------------

    // ---------------- 无线频段 ----------------

    private void InitApTab()
    {
        _apTick.Tick += async (_, _) => await ApTickAsync();
    }

    private void StartApWatch()
    {
        if (!_apTick.IsEnabled) _apTick.Start();
        _ = ApTickAsync();
    }

    private void StopApWatch() => _apTick.Stop();

    private async Task ApTickAsync()
    {
        if (_apBusy) return;
        _apBusy = true;
        try
        {
            var ap = await ApGather.ReadAsync();
            _apLast = ap;
            if (CmbApBand.Items.Count == 0) { if (ap.Ok) PopulateApSelectors(ap); }
            else UpdateApplyState();
            TxtApBand.Text = ap.BandText;
            TxtApCh.Text = ap.ChText;
            TxtApWidth.Text = ap.WidthText;
            TxtApPower.Text = ap.PowerText;
            TxtApSsid.Text = ap.SsidText;
            TxtApSta.Text = ap.Ok ? $"{ap.StaCount} 台" : "—";

            if (ap.Ok)
            {
                TxtApHint.Text = $"网卡 {ap.Iface} · AP 模式 · {DateTime.Now:HH:mm:ss} 更新";
                TxtApHint.Foreground = ApGray;
            }
            else
            {
                TxtApHint.Text = string.IsNullOrWhiteSpace(ap.Err) ? "读不到远端" : ap.Err;
                TxtApHint.Foreground = ApWarn;
            }

            StkApSta.Children.Clear();
            if (!ap.Ok || ap.Stas.Count == 0)
            {
                StkApSta.Children.Add(new TextBlock
                {
                    Text = ap.Ok ? "没有设备连接" : "—",
                    FontSize = 12,
                    Foreground = ApGray
                });
                return;
            }
            foreach (var st in ap.Stas) StkApSta.Children.Add(BuildApStaRow(st));
        }
        catch (Exception ex) { AppPaths.Log("[无线频段] " + ex.Message); }
        finally { _apBusy = false; }
    }

    private bool _apSwitchBusy;
    private bool _apSelUpdating;
    private ApInfo? _apLast;
    private static readonly int[] Ch24 = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
    private static readonly int[] Ch5 = { 36, 40, 44, 48, 149, 153, 157, 161, 165 };

    private void OnApSelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_apSelUpdating) return;
        if (ReferenceEquals(sender, CmbApBand)) RebuildChannelItems();
        UpdateApplyState();
    }

    /// <summary>按当前频段重建信道下拉（自动 + 实测可用信道）</summary>
    private void RebuildChannelItems()
    {
        var is5 = CmbApBand.SelectedIndex == 1;
        var auto = is5 ? 36 : 6;
        var list = is5 ? Ch5 : Ch24;

        _apSelUpdating = true;
        try
        {
            CmbApCh.Items.Clear();
            CmbApCh.Items.Add($"自动（{auto}）");
            foreach (var c in list) CmbApCh.Items.Add(c.ToString());

            var cur = _apLast is { Ok: true } a && (a.Band == "5G") == is5 ? a.Ch : "";
            var idx = 0;
            if (!string.IsNullOrEmpty(cur) && int.TryParse(cur, out var cc))
            {
                var at = Array.IndexOf(list, cc);
                if (at >= 0) idx = at + 1;
            }
            CmbApCh.SelectedIndex = idx;
        }
        finally { _apSelUpdating = false; }
    }

    private (bool Is5, int Ch) ApTarget()
    {
        var is5 = CmbApBand.SelectedIndex == 1;
        var auto = is5 ? 36 : 6;
        var list = is5 ? Ch5 : Ch24;
        if (CmbApCh.SelectedIndex <= 0) return (is5, auto);
        var i = CmbApCh.SelectedIndex - 1;
        return (is5, i < list.Length ? list[i] : auto);
    }

    private void UpdateApplyState()
    {
        if (_apSwitchBusy) { BtnApApply.IsEnabled = false; return; }
        if (CmbApBand.SelectedIndex < 0) { BtnApApply.IsEnabled = false; return; }
        var (is5, ch) = ApTarget();
        var same = _apLast is { Ok: true } a && (a.Band == "5G") == is5 && a.Ch == ch.ToString();
        BtnApApply.IsEnabled = !same;
    }

    private void PopulateApSelectors(ApInfo ap)
    {
        _apSelUpdating = true;
        try
        {
            CmbApBand.Items.Clear();
            CmbApBand.Items.Add("2.4 GHz");
            CmbApBand.Items.Add("5 GHz");
            CmbApBand.SelectedIndex = ap.Band == "5G" ? 1 : 0;
        }
        finally { _apSelUpdating = false; }

        RebuildChannelItems();
        UpdateApplyState();
    }

    private async void OnApApply(object sender, RoutedEventArgs e)
    {
        if (_apSwitchBusy) return;

        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) { MessageBox.Show("先在「连接」页填远端地址。", "无线频段"); return; }

        var (is5, ch) = ApTarget();
        var target = is5 ? $"5 GHz / 信道 {ch}" : $"2.4 GHz / 信道 {ch}";
        var ask = MessageBox.Show(
            $"切换到 {target}？\n\n" +
            "过程：先上 150 秒死手保险 → 改参数 → 重启 AP。WiFi 会断 15~25 秒，本面板也会短暂失去连接。\n" +
            "5 GHz 起不来时，远端会自动回滚到备份的原设置。",
            "无线频段", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (ask != MessageBoxResult.OK) return;

        _apSwitchBusy = true;
        BtnApApply.IsEnabled = false;
        try
        {
            TxtApProgress.Text = "检查远端助手…";
            if (!await RouterCtl.IsInstalledAsync(cfg))
            {
                var yes = MessageBox.Show(
                    "这个按钮需要在远端装一个权限助手（一次 sudo 密码，之后就不用再输）。\n现在就装吗？",
                    "需要安装", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (yes != MessageBoxResult.Yes) { TxtApProgress.Text = "已取消（没装助手）"; return; }

                var inst = await RouterCtl.InstallAsync(cfg);
                TxtApProgress.Text = inst.Detail;
                if (inst.Ok)
                    MessageBox.Show("文件已传到远端，终端窗口里输一次密码。\n装完看到 ✅ 之后，再点一次「应用」。", "无线频段");
                return;
            }

            TxtApProgress.Text = "正在切换…";
            var (ok, detail) = is5
                ? await RouterCtl.Set5GAsync(cfg, ch, m => TxtApProgress.Text = m)
                : await RouterCtl.Set2GAsync(cfg, ch, m => TxtApProgress.Text = m);

            TxtApProgress.Text = (ok ? "✅ " : "❌ ") + detail;
            await Task.Delay(4000);

            CmbApBand.Items.Clear();
            CmbApCh.Items.Clear();
            _apLast = null;
            StopApWatch();
            StartApWatch();
        }
        catch (Exception ex)
        {
            TxtApProgress.Text = "❌ " + ex.Message;
            AppPaths.Log("[无线频段] 切换失败: " + ex.Message);
        }
        finally
        {
            _apSwitchBusy = false;
            UpdateApplyState();
        }
    }

    // ---------------- 版本更新 ----------------

    private Updater.ReleaseInfo? _upd;

    private void InitUpd()
    {
        TxtUpdCur.Text = "v" + Updater.CurrentVersion();
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        BtnUpdCheck.IsEnabled = false;
        TxtUpdTip.Text = "正在查询 GitHub…";
        try
        {
            var (info, err) = await Updater.CheckAsync();
            if (info == null)
            {
                TxtUpdLatest.Text = "—";
                BtnUpdGo.IsEnabled = false;
                _upd = null;
                TxtUpdTip.Text = "❌ " + err;
                return;
            }

            TxtUpdLatest.Text = info.Tag;
            if (Updater.IsNewer(info.Tag))
            {
                _upd = info;
                BtnUpdGo.IsEnabled = true;
                TxtUpdTip.Text = $"发现新版本，大小 {info.Size / 1024.0 / 1024.0:F1} MB。点「立即更新」下载并重启面板。";
            }
            else
            {
                _upd = null;
                BtnUpdGo.IsEnabled = false;
                TxtUpdTip.Text = "✅ 已经是最新版本。";
            }
        }
        catch (Exception ex)
        {
            TxtUpdTip.Text = "❌ " + ex.Message;
        }
        finally { BtnUpdCheck.IsEnabled = true; }
    }

    private async void OnDoUpdate(object sender, RoutedEventArgs e)
    {
        if (_upd == null) return;
        var ask = MessageBox.Show(
            $"下载 {_upd.Tag} 并替换当前程序？\n\n下载完面板会自己退出、替换 exe、再重新打开（约 5 秒）。",
            "版本更新", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (ask != MessageBoxResult.OK) return;

        BtnUpdGo.IsEnabled = false;
        BtnUpdCheck.IsEnabled = false;
        try
        {
            var (ok, msg, path) = await Updater.DownloadAsync(_upd, (got, total) =>
            {
                TxtUpdTip.Text = total > 0
                    ? $"下载中 {got * 100.0 / total:F0}%（{got / 1024.0 / 1024.0:F1} MB）"
                    : $"下载中 {got / 1024.0 / 1024.0:F1} MB";
            });

            if (!ok)
            {
                TxtUpdTip.Text = "❌ " + msg;
                BtnUpdGo.IsEnabled = true;
                BtnUpdCheck.IsEnabled = true;
                return;
            }

            var target = Updater.CurrentExePath();
            if (!Updater.LaunchUpdater(path, target, out var err))
            {
                TxtUpdTip.Text = "❌ 起更新器失败：" + err;
                BtnUpdGo.IsEnabled = true;
                BtnUpdCheck.IsEnabled = true;
                return;
            }

            TxtUpdTip.Text = "✅ 下载完成，面板即将退出并替换…";
            await Task.Delay(900);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            TxtUpdTip.Text = "❌ " + ex.Message;
            BtnUpdGo.IsEnabled = true;
            BtnUpdCheck.IsEnabled = true;
        }
    }

    private Border BuildApStaRow(ApSta st)
    {
        var box = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0xEA, 0xF0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = st.Mac,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = ApInk
        });
        var tail = $"信号 {st.Signal} · 上行 {st.Tx} Mbit/s";
        if (!string.IsNullOrEmpty(st.Rx)) tail += $" · 下行 {st.Rx} Mbit/s";
        if (int.TryParse(st.Inactive, out var sec) && sec >= 120)
            tail += $" · 空闲 {sec / 60} 分钟";
        sp.Children.Add(new TextBlock
        {
            Text = tail,
            FontSize = 12,
            Foreground = ApSub,
            Margin = new Thickness(0, 3, 0, 0)
        });
        box.Child = sp;
        return box;
    }

    private void InitProxyTab()
    {
        _proxyLocalTick.Tick += async (_, _) => await ProxyLocalTickAsync();
        _proxyRemoteTick.Tick += async (_, _) => await ProxyRemoteTickAsync();
    }

    private void StartProxyWatch()
    {
        if (!_proxyLocalTick.IsEnabled) _proxyLocalTick.Start();
        if (!_proxyRemoteTick.IsEnabled) _proxyRemoteTick.Start();
        _ = ProxyLocalTickAsync();       // 立刻来一次，不用等 10 秒
        _ = ProxyRemoteTickAsync();
    }

    private void StopProxyWatch()
    {
        _proxyLocalTick.Stop();
        _proxyRemoteTick.Stop();
    }

    private async Task ProxyLocalTickAsync()
    {
        if (_pLocalBusy) return;
        _pLocalBusy = true;
        try
        {
            var loc = await ProxyCheck.LocalAsync();
            _pLocal = loc.Text;
            ComposeProxyDetail();
            UpdateProxyVerdict(loc.Likely, loc.Findings);
        }
        catch (Exception ex) { AppPaths.Log("[代理] 本地检测失败: " + ex.Message); }
        finally { _pLocalBusy = false; }
    }

    private async Task ProxyRemoteTickAsync()
    {
        if (_pRemoteBusy) return;
        _pRemoteBusy = true;
        try
        {
            var (rem, lat) = await ProxyCheck.RemoteAsync();
            _pRemote = rem;
            _pLat = lat;
            ComposeProxyDetail();
        }
        catch (Exception ex) { AppPaths.Log("[代理] 出口检测失败: " + ex.Message); }
        finally { _pRemoteBusy = false; }
    }

    private void ComposeProxyDetail()
    {
        var parts = new[] { _pLocal, _pRemote, _pLat }.Where(x => !string.IsNullOrWhiteSpace(x));
        TxtProxyDetail.Text = string.Join("\n\n", parts);
    }

    private void UpdateProxyVerdict(bool likely, List<string> findings)
    {
        var head = likely ? "! 检测到代理/加速器相关配置（实时）" : "✓ 没发现本地代理（实时）";
        var mid = findings.Count > 0 ? "：" + string.Join("、", findings) : "";
        var ipLine = _pRemote.Split('\n').FirstOrDefault(x => x.Contains("← "))?.Trim() ?? "出口信息获取中…";
        TxtProxyVerdict.Text = $"{head}{mid}\n{ipLine}";
        SetDot(ImgProxyDot, likely ? "bad" : "on");
    }

    private async void OnProxyCheck(object sender, RoutedEventArgs e)
    {
        BtnProxyCheck.IsEnabled = false;
        TxtFoot.Text = "正在完整检测…";
        try
        {
            _pRemote = "（正在查出口 IP…）";
            await ProxyLocalTickAsync();
            await ProxyRemoteTickAsync();
            AppPaths.Log("[代理] 手动检测完成，检测到代理=" + (_pLocal.Contains("√") ? "是" : "否"));
            TxtFoot.Text = "代理检测完成";
        }
        finally { BtnProxyCheck.IsEnabled = true; }
    }

    private void OnProxyCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(TxtProxyVerdict.Text + "\n\n" + TxtProxyDetail.Text);
            TxtFoot.Text = "结果已复制到剪贴板";
        }
        catch (Exception ex) { TxtFoot.Text = "复制失败：" + ex.Message; }
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
            int rolls = 0;
            for (int k = 1; k < AppPaths.MaxLogFiles; k++)
                if (File.Exists(AppPaths.RolledLogFile(k))) rolls++;
            TxtLogInfo.Text = $"{AppPaths.Human(fi.Length)} / 上限 {AppPaths.Human(AppPaths.MaxLogBytes)} · 历史 {rolls} 份 · {lines.Length} 行 · 最后修改 {fi.LastWriteTime:MM-dd HH:mm:ss}";
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
            var size = AppPaths.CurrentLogSize;
            if (size <= 0) { TxtFoot.Text = "没有日志可清"; return; }
            if (MessageBox.Show(
                    $"清空当前日志？当前 {AppPaths.Human(size)}。\n\n旧内容会变成 app.log.1 保留下来（最多保留 {AppPaths.MaxLogFiles} 份，更旧的自动丢弃）。",
                    "清空日志", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            AppPaths.RotateNow();
            AppPaths.Log("日志已手动清空（旧内容在 app.log.1）");
            LoadLog();
            TxtFoot.Text = "√ 日志已清空，旧内容在 app.log.1";
        }
        catch (Exception ex)
        {
            AppPaths.Log("清空日志失败: " + ex.Message);
            MessageBox.Show("清空失败：\n" + ex.Message, "清空日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExportLog(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出日志",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt",
            FileName = $"NetPanel-日志-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            DefaultExt = ".log",
        };
        if (!string.IsNullOrWhiteSpace(_cfg.LastExportDir) && System.IO.Directory.Exists(_cfg.LastExportDir))
            dlg.InitialDirectory = _cfg.LastExportDir;
        if (dlg.ShowDialog() != true) return;

        try
        {
            // UTF-8 带 BOM：记事本打开中文不乱码
            File.WriteAllText(dlg.FileName, AppPaths.BuildExportText(), new UTF8Encoding(true));
            _cfg.LastExportDir = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
            _cfg.Save();
            AppPaths.Log("日志已导出: " + dlg.FileName);
            TxtFoot.Text = "√ 日志已导出到 " + dlg.FileName;
            MessageBox.Show("已导出到：\n" + dlg.FileName, "导出日志", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppPaths.Log("导出日志失败: " + ex.Message);
            MessageBox.Show("导出失败：\n" + ex.Message, "导出日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDeleteAllLogs(object sender, RoutedEventArgs e)
    {
        var size = AppPaths.CurrentLogSize;
        int rolls = 0;
        for (int k = 1; k < AppPaths.MaxLogFiles; k++)
            if (File.Exists(AppPaths.RolledLogFile(k))) rolls++;

        if (MessageBox.Show(
                $"把 app.log 和 {rolls} 份历史日志全部删掉？\n\n当前文件 {AppPaths.Human(size)}，删除后不可恢复。",
                "删除全部日志", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var (ok, n) = AppPaths.DeleteAllLogs();
        AppPaths.Log("已删除全部日志（" + n + " 个文件）");
        LoadLog();
        TxtFoot.Text = ok ? $"√ 已删除 {n} 个日志文件" : "× 部分文件删除失败（可能被占用）";
    }

    private void OnOpenLogDir(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", AppPaths.DataDir); }
        catch (Exception ex) { AppPaths.Log("打开目录失败: " + ex.Message); }
    }

    // ---------------- 一键网络诊断 ----------------

    // ===== 看门狗（grayrouter-watch）：一键装到远端 / 触发重启 / 切模式 =====

    private async void OnWatchInstall(object sender, RoutedEventArgs e)
    {
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured)
        {
            MessageBox.Show("先在「连接」页填好远端地址和用户名。", "安装看门狗",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        BtnWatchInstall.IsEnabled = false;
        try
        {
            AppPaths.Log("[看门狗] 开始安装到 " + cfg.Target);
            var (ok, detail) = await Watchdog.InstallAsync(cfg);
            AppPaths.Log("[看门狗] " + (ok ? "上传成功" : "失败") + "：" + detail);
            WaitWatchInstall(detail);
        }
        catch (Exception ex)
        {
            AppPaths.Log("[看门狗] 安装异常: " + ex.Message);
            MessageBox.Show("装不上：" + ex.Message, "安装看门狗", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnWatchInstall.IsEnabled = true;
        }
    }

    /// <summary>终端里输密码装完后，等几秒自动刷新状态（不做轮询，只等一次）</summary>
    private async void WaitWatchInstall(string detail)
    {
        MessageBox.Show(detail + "\n\n装完关掉那个窗口后，这里会自动刷新。", "安装看门狗",
            MessageBoxButton.OK, MessageBoxImage.Information);
        await Task.Delay(TimeSpan.FromSeconds(25));
        try { await RefreshAsync(); } catch { }
    }

    private async void OnWatchRestart(object sender, RoutedEventArgs e)
    {
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) return;
        BtnWatchRestart.IsEnabled = false;
        try
        {
            var (ok, detail) = await Watchdog.RestartApAsync(cfg);
            AppPaths.Log("[看门狗] 重启 AP 请求：" + detail);
            MessageBox.Show(detail, "重启 AP", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show("发不出去：" + ex.Message, "重启 AP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnWatchRestart.IsEnabled = true;
        }
    }

    private async void OnWatchAuto(object sender, RoutedEventArgs e)
    {
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) return;

        var target = StatusGather.LastWatchMode == "act" ? "observe" : "act";
        BtnWatchAuto.IsEnabled = false;
        try
        {
            AppPaths.Log("[看门狗] 切换 → " + target);
            var (ok, detail) = await Watchdog.SetModeAsync(cfg, target);
            if (!ok)
                MessageBox.Show(detail, "自动执行", MessageBoxButton.OK, MessageBoxImage.Warning);
            await Task.Delay(1200);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show("切不了：" + ex.Message, "自动执行", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnWatchAuto.IsEnabled = true;
        }
    }

    /// <summary>把开关和文字刷成远端当前模式</summary>
    private void UpdateWatchUi()
    {
        var act = StatusGather.LastWatchMode == "act";
        ImgWatchSwitch.Source = new System.Windows.Media.Imaging.BitmapImage(
            new Uri(act ? "pack://application:,,,/icons/switch_on.png" : "pack://application:,,,/icons/switch_off.png"));
        TxtWatchAuto.Text = act ? "自动执行：开" : "自动执行：关";
    }

    // ---------------- 软路由模式（远端遥控） ----------------

    private async void OnSwitchRouter(object sender, RoutedEventArgs e) =>
        await RunModeAsync("mode-router", "切到软路由模式",
            "切到软路由模式前，请先确认：\n\n" +
            "① 主力机已经从校园网注销（一号一设备，不注销会互相挤断）\n" +
            "② 主力机的有线网卡已禁用 / 网线已拔\n" +
            "③ minipc 的有线口已经插上校园网网线\n\n" +
            "切过去之后，这台电脑要连 Gray-Router 才能继续管理它。\n\n如果 5 分钟后连不上：接显示器 + 键盘，执行 sudo bash ~/router-down.sh 回滚。\n\n继续吗？");

    private async void OnSwitchHotspot(object sender, RoutedEventArgs e) =>
        await RunModeAsync("mode-hotspot", "回滚到热点模式",
            "回滚到热点模式前，请先确认：\n\n" +
            "① 主力机已经开好手机热点（名字要能对上）\n" +
            "② 回滚后 minipc 会去连那个热点\n\n" +
            "如果热点没开，minipc 会掉线，只能接显示器救回来。\n\n继续吗？");

    private async void OnHarden(object sender, RoutedEventArgs e) =>
        await RunModeAsync("harden", "加固伪装",
            "加固伪装会改：主机名随机化 / 关 mDNS / TTL 规则等。\n\n" +
            "不会断网，但相关服务会重启一下。\n\n继续吗？");

    private void SetModeButtons(bool enabled)
    {
        BtnSwitchRouter.IsEnabled = enabled;
        BtnSwitchHotspot.IsEnabled = enabled;
        BtnHarden.IsEnabled = enabled;
    }

    private async Task RunModeAsync(string cmd, string title, string ask)
    {
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) return;

        if (!await RouterCtl.IsInstalledAsync(cfg))
        {
            var yes = MessageBox.Show(
                "这个按钮需要在远端装一个权限助手（一次 sudo 密码，之后就不用再输）。\n\n现在就装吗？",
                title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
            if (!yes) return;

            var (iok, idetail) = await RouterCtl.InstallAsync(cfg);
            TxtModeHint.Text = idetail;
            AppPaths.Log("[遥控] 安装：" + idetail);
            if (!iok)
            {
                MessageBox.Show(idetail, "安装助手", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            MessageBox.Show("文件已传好，请在刚弹出的终端里输一次密码。\n装完再点一次这个按钮就行。",
                title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(ask, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        SetModeButtons(false);
        TxtModeHint.Text = "正在执行…";
        try
        {
            AppPaths.Log("[遥控] 发送 " + cmd);
            var (ok, detail) = await RouterCtl.RunAsync(cfg, cmd, p => TxtModeHint.Text = p);
            TxtModeHint.Text = (ok ? "√ " : "× ") + detail;
            AppPaths.Log("[遥控] " + cmd + " → " + (ok ? "OK" : "FAIL") + "：" + detail);
            if (!ok)
                MessageBox.Show(detail, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            await Task.Delay(1500);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            TxtModeHint.Text = "× " + ex.Message;
        }
        finally { SetModeButtons(true); }
    }

    /// <summary>把「当前：…」刷成远端模式</summary>
    private void UpdateModeUi()
    {
        TxtModeNow.Text = StatusGather.LastMode switch
        {
            "router" => "当前：软路由（AP）",
            "hotspot" => "当前：热点模式（客户端）",
            _ => "当前：— 读不到（远端没连上？）",
        };
    }

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
    private string _lastNicSet = "";
    private DateTime _lastNicProbe = DateTime.MinValue;
    private bool _probingNic;

    private void SetNicHint(string text, bool ok)
    {
        TxtNicHint.Text = text;
        TxtNicHint.Foreground = new SolidColorBrush(ok ? Color.FromRgb(0x2F, 0x8F, 0x5B) : Color.FromRgb(0xC0, 0x39, 0x2B));
    }



    // ---------------- 其他页 ----------------
    private void InitOtherTab()
    {
        try
        {
            TxtOtherVer.Text = $"{Ver.Text()}  ·  单文件版";
            TxtOtherExe.Text = Environment.ProcessPath ?? AppPaths.BaseDir;
            TxtOtherData.Text = AppPaths.DataDir;
            TxtOtherOs.Text = RuntimeInformation.OSDescription + $"（{(Environment.Is64BitProcess ? "64" : "32")} 位）";
            TxtOtherNet.Text = RuntimeInformation.FrameworkDescription.Replace(".NET ", "");
            TxtOtherRepo.Text = "https://github.com/ark2077njbks/FJjx-Ubuntu-software-based-router";
            RefreshOtherRuntime();
            RefreshLogInfo();
        }
        catch (Exception ex) { AppPaths.Log("[其他] 初始化失败: " + ex.Message); }
    }

    private void RefreshOtherRuntime()
    {
        try
        {
            TxtMemAvail.Text = $"当前可用 {MemOptimize.AvailPhys() / 1073741824.0:F1} GB / 共 {MemOptimize.TotalPhys() / 1073741824.0:F1} GB";
            var up = DateTime.Now - Process.GetCurrentProcess().StartTime;
            TxtOtherUp.Text = $"{(int)up.TotalHours} 小时 {up.Minutes} 分 {up.Seconds} 秒";
            using var p = Process.GetCurrentProcess();
            TxtOtherMem.Text = $"{p.WorkingSet64 / 1048576.0:F0} MB（峰值 {p.PeakWorkingSet64 / 1048576.0:F0} MB）";
        }
        catch { }
    }


    private async void OnMemOptimize(object sender, RoutedEventArgs e)
    {
        if (_memBusy) return;
        _memBusy = true;
        BtnMemOptimize.IsEnabled = false;
        BtnMemOptimize.Content = "优化中…";
        TxtMemResult.Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x74));
        TxtMemResult.Text = "正在优化…";
        AppPaths.Log("[内存] 开始优化");
        try
        {
            var r = await Task.Run(() => MemOptimize.Run(m => Dispatcher.Invoke(() => TxtMemResult.Text = m)));
            if (r.Error.Length > 0 && r.Steps == 0)
            {
                TxtMemResult.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                TxtMemResult.Text = "优化失败：" + r.Error;
                AppPaths.Log("[内存] 失败：" + r.Error);
            }
            else
            {
                var saved = MemOptimize.AvailPhys() - r.AvailBefore;
                string delta = r.FreedMB > 0 ? $"释放约 {r.FreedMB:F0} MB" : "内存本来就很充裕，变化不大";
                TxtMemResult.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x5A));
                TxtMemResult.Text = $"完成：可用 {r.BeforeGB:F1} GB → {r.AfterGB:F1} GB（{delta}）";
                AppPaths.Log($"[内存] 完成：可用 {r.BeforeGB:F1} GB → {r.AfterGB:F1} GB，释放 {r.FreedMB:F0} MB，命令成功 {r.Steps}/3");
                RefreshOtherRuntime();
            }
            TxtMemAvail.Text = $"当前可用 {MemOptimize.AvailPhys() / 1073741824.0:F1} GB / 共 {MemOptimize.TotalPhys() / 1073741824.0:F1} GB";
        }
        catch (Exception ex)
        {
            TxtMemResult.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            TxtMemResult.Text = "出错了：" + ex.Message;
            AppPaths.Log("[内存] 异常：" + ex.Message);
        }
        finally
        {
            BtnMemOptimize.Content = "优化内存";
            BtnMemOptimize.IsEnabled = true;
            _memBusy = false;
        }
    }

    private bool _memBusy;

    private static void OpenPath(string path, bool isDir)
    {
        try
        {
            if (isDir) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (System.IO.File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else MessageBox.Show("文件不存在：\n" + path, "其他", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show("打不开：" + ex.Message, "其他", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnOpenExeDir(object sender, RoutedEventArgs e) => OpenPath(AppPaths.BaseDir, true);
    private void OnOpenDataDir(object sender, RoutedEventArgs e) => OpenPath(AppPaths.DataDir, true);
    private void OnOpenLogFile(object sender, RoutedEventArgs e) => OpenPath(AppPaths.LogFile, false);
    private void OnOpenSettingsFile(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!System.IO.File.Exists(AppPaths.SettingsFile)) _cfg.Save();   // 第一次点就先写出来
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppPaths.SettingsFile}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show("打不开记事本：" + ex.Message, "设置文件", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void OnOpenExportDir(object sender, RoutedEventArgs e)
    {
        var dir = _cfg.LastExportDir;
        if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir))
        {
            MessageBox.Show("还没有导出过测速结果。\n\n在「测速」页跑完一次后，点「导出本次结果」选个位置。", "测速导出目录",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenPath(dir, true);
    }

    private void RefreshLogInfo()
    {
        try
        {
            TxtLogDir.Text = AppPaths.LogDir;
            var size = System.IO.File.Exists(AppPaths.LogFile) ? new System.IO.FileInfo(AppPaths.LogFile).Length : 0;
            TxtLogSize.Text = size > 0 ? $"{AppPaths.Human(size)}（{System.IO.File.ReadLines(AppPaths.LogFile).Count()} 行）" : "还没有日志";
        }
        catch { }
    }

    private void OnChangeLogDir(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选一个文件夹，日志 app.log 会搬过去",
            UseDescriptionForTitle = true,
            SelectedPath = AppPaths.LogDir,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        ApplyLogDir(dlg.SelectedPath);
    }

    private void OnResetLogDir(object sender, RoutedEventArgs e) => ApplyLogDir("");

    private void ApplyLogDir(string target)
    {
        var (ok, msg) = AppPaths.MoveLogTo(string.IsNullOrWhiteSpace(target) ? null : target);
        if (!ok)
        {
            MessageBox.Show(msg + "\n\n日志位置没有改。", "日志", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _cfg.LogDir = string.IsNullOrWhiteSpace(target) ? "" : target;
        _cfg.Save();
        AppPaths.Log("[日志] " + msg);
        RefreshLogInfo();
    }

    private void OnClearLogFile(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定清空日志？\n\n只会清掉 app.log 里的内容，不会删文件本身。", "清空日志",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            System.IO.File.WriteAllText(AppPaths.LogFile, "", new UTF8Encoding(false));
            AppPaths.Log($"日志已清空（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
            RefreshLogInfo();
            LoadLog();
        }
        catch (Exception ex) { MessageBox.Show("清空失败：" + ex.Message, "日志", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnOpenUninstaller(object sender, RoutedEventArgs e)
    {
        var exe = System.IO.Path.Combine(AppPaths.BaseDir, "..", "卸载程序.exe");
        exe = System.IO.Path.GetFullPath(exe);
        if (!System.IO.File.Exists(exe))
        {
            MessageBox.Show("没找到卸载程序（应在软件根目录：卸载程序.exe）", "其他", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenPath(exe, false);
    }

    private void OnOpenRepo(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/ark2077njbks/FJjx-Ubuntu-software-based-router") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("打不开浏览器：" + ex.Message, "其他", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ---------------- 校园网登录 / 注销 ----------------
    private void LoadCampusUi()
    {
        try
        {
            var c = CampusLogin.LoadCreds();
            TxtCampusUser.Text = c.Username;
            CmbCampusIsp.SelectionChanged += (_, _) =>
                TxtCampusIspCustom.Visibility = CmbCampusIsp.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
            CmbCampusIsp.SelectedIndex = c.UserType switch
            {
                "unicom" => 0, "cmcc" => 1, "telecom" => 2,
                "" => 3,
                _ => 4,
            };
            if (CmbCampusIsp.SelectedIndex == 4) TxtCampusIspCustom.Text = c.UserType;
            TxtCampusIspCustom.Visibility = CmbCampusIsp.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
            var pw = CampusLogin.UnprotectPassword(c);
            if (!string.IsNullOrEmpty(pw))
            {
                PwdCampus.Password = pw;
                TxtCampusHint.Text = "已记住账号（密码本机加密保存），直接点登录";
            }
        }
        catch { }
    }

    private DateTime _cifStamp = DateTime.MinValue;

    /// <summary>校园网网卡下拉：优先用远端探测到的 WAN 口，其次本机物理网卡（5 分钟缓存）</summary>
    private async Task RefreshCampusIfacesAsync(bool force = false)
    {
        if (!force && CmbCampusIf.Items.Count > 0 && (DateTime.Now - _cifStamp).TotalSeconds < 300) return;

        var names = new List<string>();
        var wan = "";
        try
        {
            var cfg = RemoteConfig.Load();
            if (cfg.IsConfigured)
            {
                var cmd = "ip -o -4 route show default 2>/dev/null | awk '{print \"WAN=\" $5; exit}'; "
                        + "ip -o link show up 2>/dev/null | sed -n 's/^[0-9]*: \\([^:@]*\\).*/IF=\\1/p' | grep -vE '^(IF=(lo|docker|veth|br-|virbr|tun|tap|wg))'";
                var args = Remote.BaseArgs(cfg, true) + cfg.Target + " \"" + cmd + "\"";
                var run = await Remote.RunAsync("ssh.exe", args, 8);
                foreach (var raw in (run.Out ?? "").Split('\n'))
                {
                    var t = raw.Trim();
                    if (t.StartsWith("WAN=")) wan = t.Substring(4).Trim();
                    else if (t.StartsWith("IF="))
                    {
                        var n = t.Substring(3).Trim();
                        if (n.Length > 0 && n != "lo" && !names.Contains(n)) names.Add(n);
                    }
                }
            }
        }
        catch { }

        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var ty = ni.NetworkInterfaceType;
                if (ty == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                    || ty == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                {
                    var n = ni.Name;
                    if (!names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase))) names.Add(n);
                }
            }
        }
        catch { }

        if (names.Count == 0) return;

        var saved = "";
        try { saved = CampusLogin.LoadCreds().Interface ?? ""; } catch { }
        var want = names.Contains(saved) ? saved
                 : (!string.IsNullOrWhiteSpace(wan) && names.Contains(wan) ? wan
                 : (names.Contains("eno1") ? "eno1" : names[0]));

        CmbCampusIf.Items.Clear();
        foreach (var n in names) CmbCampusIf.Items.Add(new ComboBoxItem { Content = n });
        foreach (ComboBoxItem it in CmbCampusIf.Items)
            if ((string)it.Content == want) { CmbCampusIf.SelectedItem = it; break; }

        _cifStamp = DateTime.Now;
        AppPaths.Log($"[校园网] 网卡候选 {string.Join("/", names)}，默认 {want}（远端 WAN={wan}）");
    }

    private bool _campusPrefilled;

    /// <summary>账号空着时从远端 Config.yaml 读账号（不含密码），省得手打</summary>
    private async Task PrefillCampusUserAsync()
    {
        try
        {
            var rc = RemoteConfig.Load();
            if (!rc.IsConfigured) return;
            var yaml = await CampusLogin.ReadRemoteConfigAsync(rc);
            var m = System.Text.RegularExpressions.Regex.Match(yaml, "username\\s*:\\s*\"?([0-9A-Za-z_]+)");
            if (m.Success && TxtCampusUser.Text.Trim().Length == 0)
            {
                TxtCampusUser.Text = m.Groups[1].Value;
                TxtCampusHint.Text = "账号已从 minipc 读出来，补密码就能登录（密码只存本机，加密）";
            }
        }
        catch { }
    }

    private string CampusIface => (CmbCampusIf.SelectedItem as ComboBoxItem)?.Content as string ?? "eno1";

    /// <summary>运营商 → srun 的 user_type（空 = 校园网/校内认证）</summary>
    private string CampusUserType => CmbCampusIsp.SelectedIndex switch
    {
        0 => "unicom",
        1 => "cmcc",
        2 => "telecom",
        3 => "",
        _ => TxtCampusIspCustom.Text.Trim().ToLowerInvariant(),
    };

    private string CampusIspName => CmbCampusIsp.SelectedIndex == 4
        ? (CampusUserType == "" ? "校园网" : CampusUserType)
        : (CmbCampusIsp.SelectedItem as ComboBoxItem)?.Content as string ?? "中国联通";

    private async void OnCampusLogin(object sender, RoutedEventArgs e)
    {
        var user = TxtCampusUser.Text.Trim();
        var pwd = PwdCampus.Password;
        if (pwd.Length == 0)   // 密码框空着就用本机存的那份
            pwd = CampusLogin.UnprotectPassword(CampusLogin.LoadCreds());
        if (user.Length == 0) { TxtCampusLog.Text = "× 先填账号。"; return; }
        if (pwd.Length == 0) { TxtCampusLog.Text = "× 先填密码（第一次登录必须填一次）。"; return; }

        BtnCampusLogin.IsEnabled = false;
        BtnCampusLogout.IsEnabled = false;
        TxtCampusLog.Text = "正在登录：先把凭据写到 minipc 的 ~/Config.yaml，再执行 bitsrun…";
        try
        {
            var (ok, msg) = await CampusLogin.LoginAsync(user, pwd, CampusIface, CampusUserType);
            CampusLogin.SaveCreds(user, pwd, CampusIface, CampusUserType);
            TxtCampusLog.Text = $"{DateTime.Now:HH:mm:ss}  {(ok ? "√" : "×")} {msg}\n\n网卡 {CampusIface} · 账号 {user}\n"
                              + $"运营商 {CampusIspName} · 网卡 {CampusIface}\n"
                              + (ok ? "已在线。状态页「校园网出口」会自动更新。" : "没成功的话看上面那行报错原文；运营商选错也会被拒。");
            AppPaths.Log($"[校园网] 登录 {(ok ? "成功" : "失败")} user={user} isp={CampusUserType} iface={CampusIface} msg={msg}");
            if (ok) TxtCampusHint.Text = "已登录，密码已在本机加密保存";
        }
        catch (Exception ex)
        {
            TxtCampusLog.Text = "× 异常：" + ex.Message;
            AppPaths.Log("[校园网] 登录异常: " + ex.Message);
        }
        finally { BtnCampusLogin.IsEnabled = true; BtnCampusLogout.IsEnabled = true; }
    }

    private async void OnCampusLogout(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(
            "注销会真的断网。\n\nminipc 的校园网会话会被结束，接在它下面的设备（包括这台笔记本）都会掉线，\n要重新上网得再点一次「登录」。\n\n确定注销吗？",
            "校园网注销", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;

        BtnCampusLogin.IsEnabled = false;
        BtnCampusLogout.IsEnabled = false;
        TxtCampusLog.Text = "正在调门户注销接口…（注销成功后网络会断）";
        try
        {
            var (ok, msg) = await CampusLogin.LogoutAsync(TxtCampusUser.Text.Trim());
            TxtCampusLog.Text = $"{DateTime.Now:HH:mm:ss}  {(ok ? "√" : "×")} {msg}";
            AppPaths.Log($"[校园网] 注销 {(ok ? "成功" : "失败")} msg={msg}");
        }
        catch (Exception ex)
        {
            TxtCampusLog.Text = "× 异常：" + ex.Message;
            AppPaths.Log("[校园网] 注销异常: " + ex.Message);
        }
        finally { BtnCampusLogin.IsEnabled = true; BtnCampusLogout.IsEnabled = true; }
    }

    private async void OnCampusStatus(object sender, RoutedEventArgs e)
    {
        BtnCampusStatus.IsEnabled = false;
        TxtCampusLog.Text = "正在查（只读，不断网）…";
        try
        {
            var txt = await CampusLogin.StatusAsync();
            TxtCampusLog.Text = $"{DateTime.Now:HH:mm:ss}  {txt}";
        }
        catch (Exception ex) { TxtCampusLog.Text = "× 异常：" + ex.Message; }
        finally { BtnCampusStatus.IsEnabled = true; }
    }

    /// <summary>静默探测：只在切回状态页/窗口重新激活时用一次 ssh 比对，不常驻轮询</summary>
    private async Task WatchNicsAsync()
    {
        if (_probingNic || _busy) return;
        _probingNic = true;
        try
        {
            var rc = RemoteConfig.Load();
            if (!rc.IsConfigured) return;

            var names = await StatusGather.RemoteNicNamesAsync(rc);
            if (string.IsNullOrEmpty(names)) return;

            var real = names.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(n => !(n.StartsWith("docker") || n.StartsWith("veth") || n.StartsWith("br-")
                                       || n.StartsWith("virbr") || n.StartsWith("tun") || n.StartsWith("tap") || n == "lo"))
                            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var set = string.Join(",", real);
            _lastNicProbe = DateTime.Now;

            if (_lastNicSet != "" && set != _lastNicSet)
            {
                var added = real.Except(_lastNicSet.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToArray();
                var removed = _lastNicSet.Split(',', StringSplitOptions.RemoveEmptyEntries).Except(real).ToArray();
                AppPaths.Log($"[网口] 变化：新增「{string.Join("、", added)}」 消失「{string.Join("、", removed)}」");
                await RefreshAsync(false);          // 变了才整页刷新，刷新里会写绿字提示
                return;
            }
            _lastNicSet = set;
        }
        catch (Exception ex) { AppPaths.Log("[网口] 探测异常: " + ex.Message); }
        finally { _probingNic = false; }
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
            RenderSections(StpLocal, snap.LocalSections);
            RenderSections(StpCampus, snap.CampusSections);
            RenderSections(StpMini, snap.MiniSections);

            // 刷新本身就带网口检测：远端新插的 USB 网卡会在这里冒出来
            var nicNames = snap.RemoteNics.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var nicSet = string.Join(",", nicNames);
            var beforeSet = _lastNicSet.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var nicAdded = nicNames.Except(beforeSet).ToArray();
            if (_lastNicSet != "" && nicSet != _lastNicSet)
                AppPaths.Log($"[网口] 变化：新增「{string.Join("、", nicAdded)}」 "
                    + $"消失「{string.Join("、", beforeSet.Except(nicNames))}」");
            _lastNicSet = nicSet;
            _lastNicProbe = DateTime.Now;
            if (nicNames.Length > 0)
                SetNicHint(nicAdded.Length > 0
                    ? $"√ 发现新网口：{string.Join("、", nicAdded)}"
                    : $"√ 远端网口 {nicNames.Length} 个：{string.Join("、", nicNames)}", true);

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
            UpdateWatchUi();
            UpdateModeUi();
        }
    }

    // ---------------- 网卡页 ----------------


    /// <summary>网卡热插拔检测：名字集合变了就提示并自动刷新列表</summary>
    private async Task PollAdaptersAsync()
    {
        if (_nicPolling) return;
        _nicPolling = true;
        if (Environment.GetEnvironmentVariable("NETPANEL_NICDEBUG") == "1") AppPaths.Log("[网卡] 轮询一次");
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
                Text = $"{a.Name}     [{StatusGather.LocalStatus(a.Status, a.IsDisabled)}]",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = a.IsDisabled
                    ? new SolidColorBrush(Color.FromRgb(0xB4, 0x3B, 0x3B))
                    : a.Status.Equals("Up", StringComparison.OrdinalIgnoreCase)
                        ? new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x5A))
                        : new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x98))
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

            bool absent = !a.IsDisabled && a.Status.Equals("Not Present", StringComparison.OrdinalIgnoreCase);
            var btnEnable = MakeButton("启用", true, a.IsDisabled);
            btnEnable.ToolTip = a.IsDisabled ? "在设备管理器层面重新启用这块网卡" : "这块网卡现在是启用的";
            btnEnable.Click += async (_, _) => await ToggleAdapterAsync(a.Name, true);
            var btnDisable = MakeButton("禁用", false, !a.IsDisabled && !absent);
            btnDisable.ToolTip = absent ? "设备当前未就绪（设备管理器里看得到黄色感叹号/已禁用），先在那儿处理" : "";
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

    /// <summary>后台把每个测速源的 IP 解析出来（单域名 3 秒超时，个别域名慢不拖累整体）</summary>
    private async Task ResolveSourceIpsAsync()
    {
        try
        {
            await Task.WhenAll(SpeedTest.Sources.Select(async src =>
            {
                if (!string.IsNullOrEmpty(src.Ip)) return;
                try
                {
                    var t = SpeedTest.ResolveIpAsync(src.Url);
                    var done = await Task.WhenAny(t, Task.Delay(3000));
                    src.Ip = done == t ? await t : "";
                }
                catch { src.Ip = ""; }
            }));
            var got = SpeedTest.Sources.Count(x => !string.IsNullOrEmpty(x.Ip) && x.Ip != "—");
            AppPaths.Log($"[测速] 源 IP 解析完成 {got}/{SpeedTest.Sources.Length}");
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RefreshSourceList); return; }
            RefreshSourceList();
        }
        catch (Exception ex) { AppPaths.Log("[测速] 源 IP 解析异常: " + ex.Message); }
    }

    private void RefreshSourceList()
    {
        var idx = Math.Max(CmbSource.SelectedIndex, 0);
        CmbSource.ItemsSource = SpeedTest.Sources.Select(x => x.ToString()).ToList();
        if (idx >= 0 && idx < SpeedTest.Sources.Length) CmbSource.SelectedIndex = idx;
        AppPaths.Log($"[测速] 下拉 {CmbSource.Items.Count} 条就绪，选中「{CmbSource.Text}」");
    }

    /// <summary>把结构化状态渲染成「标签列 + 值列」，中文混排也不会错位</summary>
    private void RenderSections(Panel host, List<NetPanel.Services.StatusSection> sections)
    {
        host.Children.Clear();
        if (sections == null || sections.Count == 0) return;

        foreach (var sec in sections)
        {
            if (!string.IsNullOrWhiteSpace(sec.Title))
            {
                host.Children.Add(new TextBlock
                {
                    Text = sec.Title,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x74)),
                    Margin = new Thickness(0, host.Children.Count == 0 ? 0 : 14, 0, 6),
                });
            }

            foreach (var row in sec.Rows)
            {
                if (string.IsNullOrEmpty(row.Label) && string.IsNullOrEmpty(row.Value))
                {
                    host.Children.Add(new TextBlock { Text = "", Height = 4 });
                    continue;
                }

                if (row.Children != null && row.Children.Count > 0)
                {
                    host.Children.Add(BuildCollapsible(row));
                    continue;
                }

                host.Children.Add(BuildRow(row));
            }
        }
    }

    /// <summary>普通一行：图标 + 标签 + 值</summary>
    private FrameworkElement BuildRow(NetPanel.Services.StatusRow row)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 有没有图标都占同一格，保证所有行结构一致、标签列永远对齐
        FrameworkElement iconCell;
        if (!string.IsNullOrEmpty(row.Icon))
            iconCell = new Image
            {
                Source = new BitmapImage(new Uri($"pack://application:,,,/icons/{row.Icon}")),
                Width = 14, Height = 14,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 0, 0),
            };
        else
            iconCell = new Border { Width = 14, Height = 14 };
        Grid.SetColumn(iconCell, 0);
        g.Children.Add(iconCell);

        var lbl = new TextBlock
        {
            Text = row.Label,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA3)),
            TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var val = new TextBlock
        {
            Text = row.Value,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(row.Sub ? Color.FromRgb(0x8A, 0x94, 0xA3) : Color.FromRgb(0x1F, 0x2A, 0x3D)),
            TextWrapping = row.Sub ? TextWrapping.NoWrap : TextWrapping.Wrap,
            TextTrimming = row.Sub ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            LineHeight = 19,
            ToolTip = row.Value,
        };
        Grid.SetColumn(lbl, 1);
        Grid.SetColumn(val, 2);
        g.Children.Add(lbl);
        g.Children.Add(val);
        return g;
    }

    /// <summary>可折叠组：一行灰底标题 + 箭头，点一下展开/收起（默认收起）</summary>
    private FrameworkElement BuildCollapsible(NetPanel.Services.StatusRow row)
    {
        var wrap = new StackPanel();

        var hg = new Grid();
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var txt = new TextBlock
        {
            Text = row.Label + "  " + row.Value,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x45, 0x58)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var arrow = new TextBlock
        {
            Text = "▸",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x98)),
            Margin = new Thickness(8, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(txt, 0);
        Grid.SetColumn(arrow, 1);
        hg.Children.Add(txt);
        hg.Children.Add(arrow);

        var head = new Button
        {
            Content = hg,
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE6, 0xEF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(0, 2, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = "点一下展开 / 收起",
        };
        System.Windows.Automation.AutomationProperties.SetName(head, (row.Label + " " + row.Value).Trim());

        var body = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(10, 3, 0, 2),
        };
        foreach (var kid in row.Children!) body.Children.Add(BuildRow(kid));

        head.Click += (_, _) =>
        {
            var show = body.Visibility != Visibility.Visible;
            body.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = show ? "▾" : "▸";
        };

        wrap.Children.Add(head);
        wrap.Children.Add(body);
        return wrap;
    }

    private void InitSpeedTab()
    {
        CmbSource.ItemsSource = SpeedTest.Sources.Select(x => x.ToString()).ToList();
        _ = ResolveSourceIpsAsync();
        if (CmbSource.SelectedIndex < 0) CmbSource.SelectedIndex = 0;

        CmbDuration.ItemsSource = new[] { "10 秒", "20 秒", "30 秒" };
        CmbDuration.SelectedIndex = 1;

        CmbConn.ItemsSource = new[] { "1 连接", "2 连接", "4 连接", "8 连接" };
        CmbConn.SelectedIndex = 2;   // 默认 4 连接

        CmbSource.ToolTip = "列表里选，或直接填 URL（http/https）";
        DrawGauge(0, "待机");
        ChartCanvas.SizeChanged += (_, _) => RedrawChart();
        ChartCanvas.Background = Brushes.White;
        RedrawChart();
    }

    /// <summary>    // 测速结果不再自动写盘，等用户手动导出
    private SpeedResult? _lastSpeed;
    private string _lastUp = "-", _lastPing = "-", _lastJitter = "-";

    private void OnExportSpeed(object sender, RoutedEventArgs e)
    {
        if (_lastSpeed == null)
        {
            MessageBox.Show("还没有测速结果。\n\n先跑一次测速，再点导出。", "导出测速结果", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存测速结果",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt",
            FileName = $"测速-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            DefaultExt = ".log",
        };
        if (!string.IsNullOrWhiteSpace(_cfg.LastExportDir) && System.IO.Directory.Exists(_cfg.LastExportDir))
            dlg.InitialDirectory = _cfg.LastExportDir;

        if (dlg.ShowDialog() != true) return;

        try
        {
            var r = _lastSpeed;
            var sb = new StringBuilder();
            sb.AppendLine("网络面板 · 测速记录");
            sb.AppendLine("========================");
            sb.AppendLine($"时间      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"测速源    {r.Source}");
            sb.AppendLine($"连接数    {r.Connections}");
            sb.AppendLine();
            sb.AppendLine($"下行平均  {r.AverageMbps:F1} Mbps");
            sb.AppendLine($"下行峰值  {r.PeakMbps:F1} Mbps");
            sb.AppendLine($"上行      {_lastUp} Mbps");
            sb.AppendLine($"下载量    {r.Bytes / 1048576.0:F1} MB");
            sb.AppendLine($"用时      {r.Seconds:F1} 秒");
            sb.AppendLine();
            sb.AppendLine($"延迟      {_lastPing} ms");
            sb.AppendLine($"抖动      {_lastJitter} ms");
            sb.AppendLine($"丢包      {_pingLoss}/5");
            sb.AppendLine();
            sb.AppendLine($"状态      {(r.Cancelled ? "中途停止（数据不完整）" : "正常完成")}");
            System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));

            _cfg.LastExportDir = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
            _cfg.Save();
            AppPaths.Log($"[测速] 已导出结果到 {dlg.FileName}");
            TxtFoot.Text = "√ 已导出：" + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：\n" + ex.Message, "导出测速结果", MessageBoxButton.OK, MessageBoxImage.Warning);
            AppPaths.Log("[测速] 导出失败：" + ex.Message);
        }
    }

    private double _gaugeMax = 100;
    private int _pingLoss;

    /// <summary>仪表盘：半圆弧 + 指针，按当前速度换算角度</summary>
    private void DrawGauge(double mbps, string phase)
    {
        if (mbps > _gaugeMax)
            _gaugeMax = mbps <= 200 ? 200 : mbps <= 500 ? 500 : mbps <= 1000 ? 1000 : 2000;

        const double cx = 180, cy = 180, r = 132;
        double frac = Math.Clamp(mbps / _gaugeMax, 0, 1);

        static PathGeometry Arc(double cx, double cy, double r, double frac)
        {
            double a = Math.PI * (1 - frac);
            var end = new Point(cx + r * Math.Cos(a), cy - r * Math.Sin(a));
            var fig = new PathFigure { StartPoint = new Point(cx - r, cy) };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true));
            return new PathGeometry(new[] { fig });
        }

        PthGaugeBg.Data = Arc(cx, cy, r, 1);
        PthGaugeFg.Data = Arc(cx, cy, r, frac);
        NeedleRotate.Angle = frac * 180 - 90;
        TxtGaugeBig.Text = mbps.ToString("F1");
        TxtGaugeScaleR.Text = _gaugeMax.ToString("F0");
        if (!string.IsNullOrEmpty(phase)) TxtPhase.Text = phase;
    }

    private async void OnStartSpeed(object sender, RoutedEventArgs e)
    {
        if (_speedRunning) return;

        // 自定义地址优先；没填就用下拉里选的
        SpeedSource source;
        var custom = (TxtCustomUrl.Text ?? "").Trim();
        if (custom.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            source = new SpeedSource { Name = "自定义", Url = custom };
        else
        {
            var pick = CmbSource.SelectedIndex;
            if (pick < 0 || pick >= SpeedTest.Sources.Length) pick = 0;
            source = SpeedTest.Sources[pick];
        }

        _speedWindow = CmbDuration.SelectedIndex switch { 0 => 10, 2 => 30, _ => 20 };
        int conns = CmbConn.SelectedIndex switch { 0 => 1, 1 => 2, 3 => 8, _ => 4 };

        _samples.Clear();
        _upPoints.Clear();
        _downSeconds = 0;
        _speedRunning = true;
        BtnSpeedStart.IsEnabled = false;
        BtnSpeedStop.IsEnabled = true;
        CmbSource.IsEnabled = false;
        CmbDuration.IsEnabled = false;
        CmbConn.IsEnabled = false;
        _gaugeMax = 100;
        TxtDown.Text = "—"; TxtUp.Text = "—"; TxtPing.Text = "—"; TxtJitter.Text = "—";
        TxtSpeedAvg.Text = "平均 —";
        TxtSrcInfo.Text = "测速源 " + source.Name;
        DrawGauge(0, "延迟");
        RedrawChart();
        AppPaths.Log($"测速开始 source={source.Name} url={source.Url} duration={_speedWindow}s conns={conns}");

        _speedCts = new CancellationTokenSource();
        var ct = _speedCts.Token;

        try
        {
            // ① 延迟 / 抖动
            var ip = await SpeedTest.ResolveIpAsync(source.Url);
            TxtSrcInfo.Text = $"{source.Name} · {new Uri(source.Url).Host} ({ip})";
            var ping = await SpeedTest.MeasurePingAsync(source.Url, 5, ct);
            if (ping.Avg >= 0)
            {
                TxtPing.Text = ping.Avg.ToString();
                TxtJitter.Text = ping.Jitter.ToString();
                _pingLoss = ping.Loss;
            }
            else { TxtPing.Text = "失败"; TxtJitter.Text = "—"; }

            // ② 下载
            DrawGauge(0, "下行测速");
            var progress = new Progress<SpeedSample>(sm =>
            {
                if (sm.Mbps <= 0.01) return;      // 收尾空采样会把曲线拉出一条零线
                _samples.Add(sm);
                TxtDown.Text = sm.Mbps.ToString("F1");
                DrawGauge(sm.Mbps, "下行测速");
                double avg = sm.TotalBytes * 8.0 / Math.Max(sm.Seconds, 0.1) / 1_000_000.0;
                TxtSpeedAvg.Text = $"平均 {avg:F1} Mbps · 已下载 {sm.TotalBytes / 1048576.0:F1} MB";
                RedrawChart();
            });

            // 选中源挂了（403/404/被墙/超时）就自动换下一个，最多试 3 个，别让整次测速白跑
            SpeedResult? result = null;
            var tried = new List<string>();
            foreach (var cand in new[] { source }.Concat(SpeedTest.Sources.Where(x => x.Name != source.Name)))
            {
                if (tried.Count >= 3 || ct.IsCancellationRequested) break;
                tried.Add(cand.Name);
                try
                {
                    _samples.Clear();
                    TxtSrcInfo.Text = $"{cand.Name} · {new Uri(cand.Url).Host}";
                    DrawGauge(0, "下行测速");
                    var r = await SpeedTest.RunAsync(cand, TimeSpan.FromSeconds(_speedWindow), conns, progress, ct);
                    if (r.Bytes >= 1_000_000) { result = r; source = cand; break; }
                    AppPaths.Log($"[测速] {cand.Name} 没测出数据，换下一个源");
                    TxtFoot.Text = $"「{cand.Name}」没测出数据，自动换源…";
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AppPaths.Log($"[测速] {cand.Name} 失败：{ex.Message}");
                    TxtFoot.Text = $"「{cand.Name}」连不上，自动换源…";
                }
            }
            if (result == null) throw new Exception(ct.IsCancellationRequested ? "测速已停止" : "试了 3 个测速源都连不上，检查一下网络");

            _downSeconds = _samples.Count > 0 ? _samples.Max(x => x.Seconds) : _speedWindow;
            TxtDown.Text = result.AverageMbps.ToString("F1");
            TxtSpeedAvg.Text = $"平均 {result.AverageMbps:F1} Mbps · 峰值 {result.PeakMbps:F1} · {result.Connections} 连接 · 下载 {result.Bytes / 1048576.0:F1} MB · 丢包 {_pingLoss}/5";

            // ③ 上传（Cloudflare __up，境外；国内镜像不收 POST）
            if (!ct.IsCancellationRequested)
            {
                DrawGauge(0, "上行测速");
                _gaugeMax = 100;
                var upSw = System.Diagnostics.Stopwatch.StartNew();
                var upMbps = await SpeedTest.UploadAsync(TimeSpan.FromSeconds(Math.Max(6, _speedWindow / 2)),
                    Math.Min(conns, 4),
                    v => Dispatcher.Invoke(() =>
                    {
                        TxtUp.Text = v.ToString("F1");
                        DrawGauge(v, "上行测速");
                        if (v > 0.01) { _upPoints.Add((_downSeconds + upSw.Elapsed.TotalSeconds, v)); RedrawChart(); }
                    }), ct);
                TxtUp.Text = upMbps.ToString("F1");
                DrawGauge(result.AverageMbps, "完成");
                TxtPhase.Text = "完成";
            }
            else TxtPhase.Text = "已停止";

            TxtFoot.Text = result.Cancelled
                ? "测速已停止"
                : $"√ 测速完成：下行 {result.AverageMbps:F1} Mbps / 上行 {TxtUp.Text} Mbps / 延迟 {TxtPing.Text} ms（{source.Name}）";
            AppPaths.Log($"测速完成 down={result.AverageMbps:F1} up={TxtUp.Text} ping={TxtPing.Text} jitter={TxtJitter.Text} bytes={result.Bytes} cancelled={result.Cancelled}");
            // 不自动落盘：只记在内存里，等用户点「导出本次结果」才写文件
            _lastSpeed = result;
            _lastUp = TxtUp.Text; _lastPing = TxtPing.Text; _lastJitter = TxtJitter.Text;
        }
        catch (Exception ex)
        {
            TxtFoot.Text = "× 测速失败：" + ex.Message;
            TxtPhase.Text = "失败";
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
    private readonly List<(double Sec, double Mbps)> _upPoints = new();
    private double _downSeconds;

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
        double peakDown = _samples.Count > 0 ? _samples.Max(s => s.Mbps) : 0;
        double peakUp = _upPoints.Count > 0 ? _upPoints.Max(p => p.Mbps) : 0;
        double maxY = NiceCeil(Math.Max(Math.Max(peakDown, peakUp) * 1.2, 5));

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
        double lastDown = _samples.Count > 0 ? _samples[^1].Seconds : 0;
        double lastUp = _upPoints.Count > 0 ? _upPoints[^1].Sec : 0;
        double window = Math.Max(Math.Max(_speedWindow, lastDown), Math.Max(lastUp, 1));
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

        // 图例（安兔兔那种两根线：下载蓝、上传绿）
        var lgBrushDown = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED));
        var lgBrushUp = new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x5A));
        AddLegend(left + 8, top + ph - 24, lgBrushDown, "下载");
        AddLegend(left + 78, top + ph - 24, lgBrushUp, "上传");

        bool any = _samples.Count >= 2 || _upPoints.Count >= 2;
        if (!any)
        {
            var hint = new TextBlock
            {
                Text = _speedRunning ? "正在采集数据…" : "点「开始测速」，这里会实时画出下载/上传两条曲线",
                FontSize = 13,
                Foreground = textBrush
            };
            Canvas.SetLeft(hint, left + 10);
            Canvas.SetTop(hint, top + ph / 2 - 10);
            ChartCanvas.Children.Add(hint);
            return;
        }

        // 下载/上行的分界线（安兔兔那种分段感）
        if (_upPoints.Count > 0 && _downSeconds > 0)
        {
            double xd = left + Math.Clamp(_downSeconds / window, 0, 1) * pw;
            ChartCanvas.Children.Add(new Line
            {
                X1 = xd, Y1 = top, X2 = xd, Y2 = top + ph,
                Stroke = new SolidColorBrush(Color.FromRgb(0xD5, 0xDB, 0xE5)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            });
            var lbl = new TextBlock
            {
                Text = "上行开始", FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2))
            };
            Canvas.SetLeft(lbl, xd + 4);
            Canvas.SetTop(lbl, top + 2);
            ChartCanvas.Children.Add(lbl);
        }

        // 下载曲线
        if (_samples.Count >= 2)
        {
            var poly = new Polyline
            {
                Stroke = lgBrushDown,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            };
            foreach (var sm in _samples)
            {
                double x = left + Math.Clamp(sm.Seconds / window, 0, 1) * pw;
                double y = top + ph - Math.Clamp(sm.Mbps / maxY, 0, 1) * ph;
                poly.Points.Add(new Point(x, y));
            }
            ChartCanvas.Children.Add(poly);
            AddDot(poly.Points[^1], lgBrushDown);
        }

        // 上传曲线（时间轴接着下载往后走）
        if (_upPoints.Count >= 2)
        {
            var polyUp = new Polyline
            {
                Stroke = lgBrushUp,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            };
            foreach (var p in _upPoints)
            {
                double x = left + Math.Clamp(p.Sec / window, 0, 1) * pw;
                double y = top + ph - Math.Clamp(p.Mbps / maxY, 0, 1) * ph;
                polyUp.Points.Add(new Point(x, y));
            }
            ChartCanvas.Children.Add(polyUp);
            AddDot(polyUp.Points[^1], lgBrushUp);
        }
    }

    /// <summary>曲线末端的小圆点</summary>
    private void AddDot(Point p, Brush brush)
    {
        var dot = new Ellipse { Width = 7, Height = 7, Fill = brush };
        Canvas.SetLeft(dot, p.X - 3.5);
        Canvas.SetTop(dot, p.Y - 3.5);
        ChartCanvas.Children.Add(dot);
    }

    /// <summary>图例：一根短线 + 文字</summary>
    private void AddLegend(double x, double y, Brush brush, string text)
    {
        ChartCanvas.Children.Add(new Line
        {
            X1 = x, Y1 = y + 7, X2 = x + 16, Y2 = y + 7,
            Stroke = brush, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        });
        var t = new TextBlock
        {
            Text = text, FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x74))
        };
        Canvas.SetLeft(t, x + 21);
        Canvas.SetTop(t, y);
        ChartCanvas.Children.Add(t);
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
