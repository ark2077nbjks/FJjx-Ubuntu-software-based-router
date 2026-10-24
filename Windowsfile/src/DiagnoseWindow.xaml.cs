using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetPanel.Services;

namespace NetPanel;

public partial class DiagnoseWindow : Window
{
    private readonly RemoteConfig? _rc;
    private readonly List<DiagItem> _items = new();
    private CancellationTokenSource? _cts;
    private string _verdict = "";

    public DiagnoseWindow(RemoteConfig? rc)
    {
        InitializeComponent();
        _rc = rc;
        Loaded += async (_, _) => await RunAsync();
    }

    private async System.Threading.Tasks.Task RunAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _items.Clear();
        PanelItems.Children.Clear();
        TxtVerdict.Text = "正在检查…";
        TxtSub.Text = DateTime.Now.ToString("HH:mm:ss");
        _verdict = "";
        BtnRerun.IsEnabled = false;

        try
        {
            var res = await Diagnose.RunAsync(_rc, item =>
            {
                _items.Add(item);
                AddRow(item);
            }, ct);

            _verdict = res.Verdict;
            TxtVerdict.Text = res.Verdict;
            TxtSub.Text = (string.IsNullOrEmpty(res.Egress) ? "" : $"出口 IP {res.Egress}   ·   ") +
                          $"{DateTime.Now:HH:mm:ss} 完成，共 {_items.Count} 项";
            var color = _items.Any(x => x.State == "fail") ? "#E5484D"
                      : _items.Any(x => x.State == "warn") ? "#D97706"
                      : "#1B9E5A";
            TxtVerdict.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            AppPaths.Log("[诊断] " + res.Verdict);
        }
        catch (OperationCanceledException) { TxtVerdict.Text = "已取消"; }
        catch (Exception ex)
        {
            TxtVerdict.Text = "诊断出错：" + ex.Message;
            AppPaths.Log("[诊断] 异常: " + ex);
        }
        finally { BtnRerun.IsEnabled = true; }
    }

    private void AddRow(DiagItem it)
    {
        var color = it.State switch
        {
            "ok" => "#1B9E5A",
            "fail" => "#E5484D",
            "warn" => "#D97706",
            "skip" => "#9AA5B4",
            _ => "#2F6FED",
        };
        var row = new Border
        {
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(2, 1, 2, 1),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFD)),
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = new TextBlock
        {
            Text = it.Glyph,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(glyph, 0); g.Children.Add(glyph);

        var layer = new TextBlock
        {
            Text = it.Layer,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB4)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(layer, 1); g.Children.Add(layer);

        var name = new TextBlock
        {
            Text = it.Name,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 2); g.Children.Add(name);

        var detail = new TextBlock
        {
            Text = it.Detail + (it.Ms > 0 ? $"   ({it.Ms} ms)" : ""),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(detail, 3); g.Children.Add(detail);

        row.Child = g;
        PanelItems.Children.Add(row);
    }

    private async void OnRerun(object sender, RoutedEventArgs e) => await RunAsync();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"网络诊断  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(_verdict);
        sb.AppendLine();
        foreach (var it in _items)
            sb.AppendLine($"{it.Glyph} [{it.Layer}] {it.Name}  {it.Detail}" + (it.Ms > 0 ? $"  ({it.Ms}ms)" : ""));
        try
        {
            Clipboard.SetText(sb.ToString());
            TxtSub.Text = "已复制到剪贴板";
        }
        catch (Exception ex) { AppPaths.Log("[诊断] 复制失败: " + ex.Message); }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
