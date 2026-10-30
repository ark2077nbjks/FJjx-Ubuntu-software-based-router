using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace NetPanel.Controls
{
    /// <summary>轻量交互反馈：右下角 Toast + 按钮忙碌转圈（不引第三方库）。</summary>
    public static class UiFeedback
    {
        /// <summary>右下角浮出提示（窗口内悬浮层，非 Popup，截图可见），约 3 秒自动消失。</summary>
        public static void Toast(Window? owner, string text, bool ok = true, int seconds = 3)
        {
            if (owner?.Content is not Panel root || string.IsNullOrWhiteSpace(text)) return;

            var dot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(ok ? Color.FromRgb(0x00, 0xB8, 0x94) : Color.FromRgb(0xE5, 0x48, 0x4D)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            var label = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(label);

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x3B)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                Child = row,
                Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 26, 26),
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.25, Color = Colors.Black },
            };
            Panel.SetZIndex(card, 999);
            if (card is FrameworkElement fe && root is Grid)
            {
                Grid.SetRow(fe, 0);
                Grid.SetRowSpan(fe, 3);
            }

            root.Children.Add(card);
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
                fade.Completed += (_, _) => root.Children.Remove(card);
                card.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            timer.Start();
        }

        /// <summary>按钮进入忙碌态：禁用 + 转圈 + 可选文案；Dispose 还原。</summary>
        public static IDisposable Busy(Button button, string? busyText = null)
        {
            if (button == null) return new Noop();
            object? prevContent = button.Content;
            bool prevEnabled = button.IsEnabled;

            var text = new TextBlock
            {
                Text = busyText ?? "处理中…",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = button.Foreground,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Spinner { Margin = new Thickness(0, 0, 6, 0), Stroke = button.Foreground });
            row.Children.Add(text);

            button.Content = row;
            button.IsEnabled = false;
            return new Restore(button, prevContent, prevEnabled);
        }

        private sealed class Restore : IDisposable
        {
            private readonly Button _b;
            private readonly object? _content;
            private readonly bool _enabled;
            public Restore(Button b, object? content, bool enabled) { _b = b; _content = content; _enabled = enabled; }
            public void Dispose()
            {
                _b.Content = _content;
                _b.IsEnabled = _enabled;
            }
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
