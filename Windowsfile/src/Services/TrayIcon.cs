using System;
using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace NetPanel.Services;

/// <summary>
/// 托盘常驻：窗口关掉后程序继续在后台跑，采样不停。
/// 右键菜单里可以改「× 按钮」的行为（最小化到托盘 / 直接退出），存进 settings.json。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _ni;
    private readonly ToolStripMenuItem _miTray;
    private readonly ToolStripMenuItem _miQuitMode;
    private readonly ToolStripMenuItem _miNotify;
    private readonly AppSettings _cfg;

    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action<AppSettings>? SettingsChanged;

    public TrayIcon(AppSettings cfg)
    {
        _cfg = cfg;
        var font = new Font("Microsoft YaHei UI", 9f);

        _ni = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "网络面板（后台运行中）",
            Visible = true,
        };

        var menu = new ContextMenuStrip { ShowImageMargin = false, Font = font };

        var miShow = new ToolStripMenuItem("显示面板", null, (_, _) => ShowRequested?.Invoke())
        {
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
        };
        menu.Items.Add(miShow);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("点 × 的时候") { Enabled = false });

        _miTray = new ToolStripMenuItem("缩到托盘，继续后台采样", null, (_, _) => SetCloseToTray(true))
        {
            Checked = cfg.CloseToTray,
        };
        menu.Items.Add(_miTray);

        _miQuitMode = new ToolStripMenuItem("直接退出程序", null, (_, _) => SetCloseToTray(false))
        {
            Checked = !cfg.CloseToTray,
        };
        menu.Items.Add(_miQuitMode);

        menu.Items.Add(new ToolStripSeparator());

        _miNotify = new ToolStripMenuItem("断网 / 恢复时提醒", null, (_, _) =>
        {
            _cfg.NotifyOnOffline = _miNotify.Checked;
            _cfg.Save();
            SettingsChanged?.Invoke(_cfg);
        })
        {
            Checked = cfg.NotifyOnOffline,
            CheckOnClick = true,
        };
        menu.Items.Add(_miNotify);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke()));

        _ni.ContextMenuStrip = menu;
        _ni.DoubleClick += (_, _) => ShowRequested?.Invoke();
        _ni.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowRequested?.Invoke(); };
    }

    private void SetCloseToTray(bool toTray)
    {
        _cfg.CloseToTray = toTray;
        _cfg.Save();
        _miTray.Checked = toTray;
        _miQuitMode.Checked = !toTray;
        Balloon("设置已更新", toTray
            ? "以后点 × 会缩到托盘，后台继续记录流量。真退出：右键托盘 → 退出。"
            : "以后点 × 会直接退出程序，流量采样也会停。");
        SettingsChanged?.Invoke(_cfg);
    }

    /// <summary>第一次缩到托盘时的说明气泡（只弹一次）</summary>
    public void ShowMinimizedTip()
    {
        if (_cfg.TrayTipShown) return;
        _cfg.TrayTipShown = true;
        _cfg.Save();
        Balloon("面板还在后台运行",
            "流量照常每 15 秒记录。要打开窗口：双击这个图标。要退出：右键 → 退出。");
    }

    public void Balloon(string title, string text, bool warn = false)
    {
        try
        {
            _ni.BalloonTipTitle = title;
            _ni.BalloonTipText = text;
            _ni.BalloonTipIcon = warn ? ToolTipIcon.Warning : ToolTipIcon.Info;
            _ni.ShowBalloonTip(5000);
        }
        catch { }
    }

    public void SetTooltip(string text)
    {
        try { _ni.Text = text.Length > 62 ? text[..62] : text; } catch { }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/icons/NetPanelTray.ico"));
            if (res != null) return new Icon(res.Stream, new System.Drawing.Size(16, 16));
        }
        catch { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        try { _ni.Visible = false; _ni.Dispose(); } catch { }
    }
}
