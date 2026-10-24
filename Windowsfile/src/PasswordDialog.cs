using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetPanel;

/// <summary>极简的 WiFi 密码输入框（WPF 没有内置 InputBox）</summary>
public sealed class PasswordDialog : Window
{
    private readonly PasswordBox _box = new();

    public string Password => _box.Password;

    public PasswordDialog(string ssid)
    {
        Title = "输入 WiFi 密码";
        Width = 440;
        Height = 216;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 13;

        var panel = new StackPanel { Margin = new Thickness(22) };

        panel.Children.Add(new TextBlock
        {
            Text = $"连接到「{ssid}」",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x30))
        });
        panel.Children.Add(new TextBlock
        {
            Text = "请输入 WiFi 密码（WPA2 个人版）。密码只会写进本次的配置文件，用完即删。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x98)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12)
        });

        _box.Height = 32;
        _box.Padding = new Thickness(8, 5, 8, 5);
        _box.FontSize = 14;
        panel.Children.Add(_box);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var ok = new Button
        {
            Content = "连接",
            Width = 90,
            Height = 32,
            IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        ok.Click += (_, _) =>
        {
            if (_box.Password.Length < 8)
            {
                MessageBox.Show("WPA2 密码至少 8 位。", "密码太短", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            Cursor = System.Windows.Input.Cursors.Hand
        };
        cancel.Click += (_, _) => DialogResult = false;

        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(btns);

        Content = panel;
        Loaded += (_, _) => _box.Focus();
    }
}
