using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetPanel;

/// <summary>每设备下行限速（对标 UniFi / 爱快「终端限速」）。填 0 或留空 = 取消限速。</summary>
public sealed class LimitDialog : Window
{
    private readonly TextBox _mb = new();
    public int Kbit { get; private set; }

    public LimitDialog(string name, string ip, string mac, int currentKbit)
    {
        Title = "设备限速";
        Width = 460;
        Height = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 13;

        var panel = new StackPanel { Margin = new Thickness(22) };

        panel.Children.Add(new TextBlock
        {
            Text = $"限制「{name}」的下行带宽",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x3B)),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"IP：{ip}　MAC：{mac}\n" +
                   (currentKbit > 0 ? $"当前限速：{currentKbit / 1000.0:F1} Mbit" : "当前：不限速"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 14),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "下行上限", VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
        });
        _mb.Width = 110;
        _mb.Height = 30;
        _mb.Margin = new Thickness(10, 0, 8, 0);
        _mb.VerticalContentAlignment = VerticalAlignment.Center;
        _mb.Text = currentKbit > 0 ? (currentKbit / 1000.0).ToString("0.#") : "";
        row.Children.Add(_mb);
        row.Children.Add(new TextBlock
        {
            Text = "Mbit/s（填 0 = 取消限速）", VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)), FontSize = 12,
        });
        panel.Children.Add(row);

        panel.Children.Add(new TextBlock
        {
            Text = "说明：只限制这台设备从局域网获取的下行带宽（在 minipc 的 LAN 网卡上按目的 IP 做 HTB 分类）。\n" +
                   "IP 会变的设备建议先「固定」再限速，否则换 IP 后规则不生效。\n" +
                   "限速对整机所有流量生效，不做分应用。",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
            LineHeight = 18,
        });

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var cancel = new Button { Content = "取消", Width = 84, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = new Button { Content = "应用", Width = 96, Height = 32, IsDefault = true };
        ok.Click += (_, _) => Confirm();
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        panel.Children.Add(btns);

        Content = panel;
        Loaded += (_, _) => _mb.Focus();
    }

    private void Confirm()
    {
        var text = _mb.Text.Trim();
        if (text.Length == 0 || text == "0")
        {
            Kbit = 0;
            DialogResult = true;
            Close();
            return;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) || mb < 0)
        {
            MessageBox.Show(this, "请输入数字，例如 8 表示 8 Mbit/s；填 0 表示取消限速。", "格式不对",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Kbit = (int)Math.Round(mb * 1000);
        if (Kbit > 0 && Kbit < 512)
        {
            MessageBox.Show(this, "太小了（< 0.5 Mbit），会导致设备基本没法上网。", "值太小",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }
}
