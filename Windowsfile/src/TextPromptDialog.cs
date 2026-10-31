using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetPanel;

/// <summary>单行文本输入框（设备改名等用）。</summary>
public sealed class TextPromptDialog : Window
{
    private readonly TextBox _box = new();
    public string Value => _box.Text.Trim();

    public TextPromptDialog(string title, string label, string initial, string hint = "")
    {
        Title = title;
        Width = 440; Height = hint.Length > 0 ? 250 : 210;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 13;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x3B)),
            TextWrapping = TextWrapping.Wrap,
        });
        _box.Text = initial;
        _box.Height = 30; _box.Margin = new Thickness(0, 12, 0, 0);
        _box.VerticalContentAlignment = VerticalAlignment.Center;
        _box.MaxLength = 24;
        panel.Children.Add(_box);
        if (hint.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = hint, FontSize = 11.5, Margin = new Thickness(0, 10, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                TextWrapping = TextWrapping.Wrap,
            });

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button { Content = "取消", Width = 84, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = new Button { Content = "确定", Width = 90, Height = 30, IsDefault = true };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        btns.Children.Add(cancel); btns.Children.Add(ok);
        panel.Children.Add(btns);

        Content = panel;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }
}
