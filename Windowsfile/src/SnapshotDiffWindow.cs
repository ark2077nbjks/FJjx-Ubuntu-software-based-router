using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetPanel.Services;

namespace NetPanel;

/// <summary>快照对比：选两份快照，逐文件看差异（只读）。参照 OPNsense 的配置历史对比。</summary>
public sealed class SnapshotDiffWindow : Window
{
    private readonly ComboBox _a = new() { Width = 250, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _b = new() { Width = 250 };
    private readonly TextBlock _hint = new()
    {
        FontSize = 12,
        MaxWidth = 150,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(10, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6))
    };
    private readonly TextBox _out = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        Height = 300,
        Margin = new Thickness(0, 10, 0, 0),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly Button _go = new() { Content = "对比", Height = 30, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _roll = new() { Content = "回滚到 A", Height = 30, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0) };

    public SnapshotDiffWindow()
    {
        Title = "快照对比";
        Width = 720;
        Height = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC));

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(new TextBlock { Text = "A", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        top.Children.Add(_a);
        top.Children.Add(new TextBlock { Text = "B", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        top.Children.Add(_b);
        top.Children.Add(_go);
        top.Children.Add(_roll);
        top.Children.Add(_hint);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = "对比两份配置快照；需要时可以把配置回滚到 A（软恢复，不断网）",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        root.Children.Add(top);
        root.Children.Add(_out);
        Content = root;

        _go.Click += async (_, _) => await RunDiffAsync();
        _roll.Click += async (_, _) => await RollbackAsync();
        Loaded += async (_, _) => await LoadListAsync();
    }

    private async Task LoadListAsync()
    {
        var cfg = RemoteConfig.Load();
        if (!cfg.IsConfigured) { _hint.Text = "× 先配远端地址"; return; }

        _hint.Text = "读取快照列表…";
        var (ok, detail) = await RouterCtl.RunAsync(cfg, "snap-list");
        _a.Items.Clear(); _b.Items.Clear();
        if (!ok) { _hint.Text = "× 读取失败"; _hint.ToolTip = detail; return; }

        var names = new List<string>();
        foreach (var line in detail.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("还没有")) continue;
            var name = t.Split(' ')[0];
            if (name.Length > 0) names.Add(name);
        }

        foreach (var n in names) { _a.Items.Add(n); _b.Items.Add(n); }
        if (_a.Items.Count >= 2) { _a.SelectedIndex = 1; _b.SelectedIndex = 0; }
        else if (_a.Items.Count == 1) { _a.SelectedIndex = 0; _b.SelectedIndex = 0; }

        _hint.Text = names.Count == 0 ? "还没有快照" : $"共 {names.Count} 份（A 旧 / B 新）";
    }

    private async Task RunDiffAsync()
    {
        if (_a.SelectedItem is not string na || _b.SelectedItem is not string nb)
        {
            _hint.Text = "× 先选两份快照";
            return;
        }

        _hint.Text = "对比中…";
        _out.Text = "";
        var (ok, detail) = await RouterCtl.RunAsync(RemoteConfig.Load(), $"snap-diff {na} {nb}");
        _out.Text = detail;
        _out.ScrollToHome();
        _hint.Text = ok ? "√ 完成" : "× 失败";
    }

    private async Task RollbackAsync()
    {
        if (_a.SelectedItem is not string na) { _hint.Text = "× 先选 A 快照"; return; }
        var r = MessageBox.Show(
            $"回滚到快照 {na}？\n\n① 会先把当前配置存一份 pre-restore（可再回滚）\n② 只覆盖配置文件，软恢复：不重启网络、不断网\n③ 个别设置在下次重连后才完全生效\n\n继续？",
            "回滚配置", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        _hint.Text = "回滚中…";
        var (ok, detail) = await RouterCtl.RunAsync(RemoteConfig.Load(), $"snapshot-restore-soft {na}");
        _out.Text = detail;
        _out.ScrollToHome();
        _hint.Text = ok ? "√ 已回滚（未断网）" : "× 失败";
    }
}
