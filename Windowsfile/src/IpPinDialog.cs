using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetPanel;

/// <summary>把某台设备的 IP 钉死（写 dnsmasq 静态租约）</summary>
public sealed class IpPinDialog : Window
{
    private readonly TextBox _ip = new();
    private readonly TextBox _name = new();

    public string Ip => _ip.Text.Trim();
    public string DeviceName => _name.Text.Trim();

    public IpPinDialog(string mac, string deviceName, string currentIp, string? pinnedIp)
    {
        Title = "固定 IP";
        Width = 460;
        Height = 268;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 13;

        var panel = new StackPanel { Margin = new Thickness(22) };

        panel.Children.Add(new TextBlock
        {
            Text = $"固定「{deviceName}」的 IP",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x30)),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"MAC：{mac}\n当前 IP：{(string.IsNullOrWhiteSpace(currentIp) ? "—" : currentIp)}" +
                   (string.IsNullOrWhiteSpace(pinnedIp) ? "" : $"\n已固定：{pinnedIp}"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x98)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "固定为（留空则取消固定）",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x68)),
        });
        _ip.Height = 32;
        _ip.Padding = new Thickness(8, 5, 8, 5);
        _ip.FontSize = 14;
        _ip.Text = string.IsNullOrWhiteSpace(pinnedIp) ? currentIp : pinnedIp;
        _ip.Margin = new Thickness(0, 4, 0, 8);
        panel.Children.Add(_ip);

        panel.Children.Add(new TextBlock
        {
            Text = "备注名（可选，字母数字，别带逗号）",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x68)),
        });
        _name.Height = 30;
        _name.Padding = new Thickness(8, 4, 8, 4);
        _name.FontSize = 13;
        _name.Text = deviceName;
        _name.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(_name);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };

        var ok = new Button
        {
            Content = "确定",
            Width = 90,
            Height = 32,
            IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        ok.Click += (_, _) =>
        {
            if (_ip.Text.Trim().Length > 0 && !IPAddress.TryParse(_ip.Text.Trim(), out _))
            {
                MessageBox.Show("IP 格式不对，例如 192.168.50.20。", "固定 IP", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
        };

        var cancel = new Button
        {
            Content = "取消",
            Width = 90,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        cancel.Click += (_, _) => DialogResult = false;

        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(btns);

        Content = panel;
        Loaded += (_, _) => { _ip.Focus(); _ip.SelectAll(); };
    }
}
