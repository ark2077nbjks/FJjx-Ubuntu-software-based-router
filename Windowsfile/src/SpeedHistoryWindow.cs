using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetPanel.Services;

namespace NetPanel
{
    /// <summary>测速历史（对标 UniFi Insights / 爱快测速记录）：横条趋势 + 明细。</summary>
    public sealed class SpeedHistoryWindow : Window
    {
        public SpeedHistoryWindow()
        {
            Title = "测速历史";
            Width = 760; Height = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFB));

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new TextBlock
            {
                Text = "最近测速（下行 Mbps，条越长越快）",
                FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x3B)),
                Margin = new Thickness(0, 0, 0, 10),
            };
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new StackPanel();
            scroll.Content = body;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var tip = new TextBlock
            {
                Text = "数据来自每台机器本地的 speed_history.csv，只记本机测过的结果。",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                Margin = new Thickness(0, 10, 0, 0),
            };
            Grid.SetRow(tip, 2);
            root.Children.Add(tip);

            var rows = SpeedHistory.ReadLast(20);
            if (rows.Count == 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "还没有测速记录 · 去「测速」页跑一次，结果会自动记下来",
                    FontSize = 12.5,
                    Margin = new Thickness(0, 40, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                });
            }
            else
            {
                double max = Math.Max(1, rows.Max(r => r.Peak));
                foreach (var r in rows) body.Children.Add(Row(r, max));
            }

            Content = root;
        }

        private static FrameworkElement Row(SpeedRecord r, double max)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var time = new TextBlock
            {
                Text = r.When.ToString("MM-dd HH:mm"),
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            };

            var src = new TextBlock
            {
                Text = ShortSource(r.Source),
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
            };

            var bar = new Border
            {
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(4, 240 * (r.Down / max)),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var val = new TextBlock
            {
                Text = $"{r.Down:F1} / 峰值 {r.Peak:F1} Mbps",
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x3B)),
            };

            Grid.SetColumn(time, 0); Grid.SetColumn(src, 1); Grid.SetColumn(bar, 2); Grid.SetColumn(val, 3);
            g.Children.Add(time); g.Children.Add(src); g.Children.Add(bar); g.Children.Add(val);
            return g;
        }

        private static string ShortSource(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "—";
            var i = s.IndexOfAny(new[] { '（', '(' });
            return i > 0 ? s[..i] : (s.Length > 10 ? s[..10] : s);
        }
    }
}
