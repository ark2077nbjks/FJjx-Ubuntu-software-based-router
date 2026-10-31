using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetPanel;

/// <summary>DHCP 租约（对标爱快「终端监控 · 租约」）：IP / MAC / 名字 / 到期时间。</summary>
public sealed class LeasesWindow : Window
{
    private readonly Func<Task<(bool Ok, string Text)>> _load;
    private readonly StackPanel _body = new();
    private readonly TextBlock _status = new();

    public LeasesWindow(Func<Task<(bool Ok, string Text)>> load)
    {
        _load = load;
        Title = "DHCP 租约";
        Width = 640; Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFB));
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock
        {
            Text = "当前租约", FontSize = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x3B)),
        });
        var btn = new Button { Content = "刷新", Width = 76, Height = 30, Margin = new Thickness(14, 0, 0, 0) };
        btn.Click += async (_, _) => await ReloadAsync();
        head.Children.Add(btn);
        _status.Text = "";
        _status.FontSize = 12; _status.Margin = new Thickness(14, 0, 0, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        head.Children.Add(_status);
        Grid.SetRow(head, 0); root.Children.Add(head);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.Content = _body;
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        var tip = new TextBlock
        {
            Text = "租约由 minipc 上的 dnsmasq 分配；到期时间到了设备还没续租就会释放。",
            FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
        };
        Grid.SetRow(tip, 2); root.Children.Add(tip);

        Content = root;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _status.Text = "读取中…";
        var (ok, text) = await _load();
        _body.Children.Clear();
        if (!ok)
        {
            _status.Text = "读取失败";
            _body.Children.Add(new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(4, 8, 0, 0), Foreground = Brushes.Red });
            return;
        }
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        _status.Text = lines.Length == 0 ? "没有租约" : $"{lines.Length} 条";
        if (lines.Length == 0 || (lines.Length == 1 && lines[0].Contains("没有")))
        {
            _body.Children.Add(new TextBlock
            {
                Text = "当前没有有效租约 · 设备连上 Wi-Fi 后这里会出现记录",
                FontSize = 12.5, Margin = new Thickness(0, 40, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            });
            return;
        }
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            if (f.Length < 4) continue;
            _body.Children.Add(Row(f[0], f[1], f[2], f[3]));
        }
    }

    private static FrameworkElement Row(string ip, string mac, string name, string exp)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

        TextBlock T(string s, bool bold) => new()
        {
            Text = s, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(bold ? Color.FromRgb(0x1E, 0x2A, 0x3B) : Color.FromRgb(0x4B, 0x55, 0x63)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var a = T(ip, true); var b = T(mac, false); var c = T(name == "*" ? "—" : name, false); var d = T(exp, false);
        Grid.SetColumn(a, 0); Grid.SetColumn(b, 1); Grid.SetColumn(c, 2); Grid.SetColumn(d, 3);
        g.Children.Add(a); g.Children.Add(b); g.Children.Add(c); g.Children.Add(d);
        return g;
    }
}
