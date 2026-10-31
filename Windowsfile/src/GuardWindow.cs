using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace NetPanel;

/// <summary>切模式后的“120 秒内确认”窗口；不确认由远端守卫自动回滚。</summary>
public sealed class GuardWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _left;
    private readonly TextBlock _txt;
    private readonly string _undoMode;

    public GuardWindow(int seconds, string undoMode, string doneText, Func<Task> onKeep, Func<Task> onUndo)
    {
        _left = seconds;
        _undoMode = undoMode;
        Title = "变更待确认";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = true;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC));
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        panel.Children.Add(new TextBlock
        {
            Text = doneText,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x3B)),
            TextWrapping = TextWrapping.Wrap,
        });
        _txt = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 14),
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(_txt);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var keep = new Button { Content = "一切正常，保留", Height = 30, Padding = new Thickness(14, 0, 14, 0), Cursor = System.Windows.Input.Cursors.Hand };
        keep.Click += async (_, _) => { _timer.Stop(); await onKeep(); Close(); };
        var undo = new Button { Content = "立即还原", Height = 30, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 0, 14, 0), Cursor = System.Windows.Input.Cursors.Hand };
        undo.Click += async (_, _) => { _timer.Stop(); await onUndo(); Close(); };
        row.Children.Add(keep);
        row.Children.Add(undo);
        panel.Children.Add(row);
        Content = panel;

        UpdateText();
        _timer.Tick += (_, _) =>
        {
            _left--;
            if (_left <= 0) { _timer.Stop(); Close(); return; }
            UpdateText();
        };
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void UpdateText()
    {
        _txt.Text = _left > 0
            ? $"{_left} 秒内不确认，将自动回滚到 {(_undoMode == "hotspot" ? "热点模式" : "软路由模式")}。\n如果一切正常，点「保留」；如果连不上/有问题，点「立即还原」。"
            : "已超时，等待远端自动回滚。";
    }
}
