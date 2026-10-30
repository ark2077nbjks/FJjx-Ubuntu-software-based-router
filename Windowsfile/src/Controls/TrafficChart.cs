using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NetPanel.Controls
{
    /// <summary>
    /// 实时流量折线（参考 LuCI realtime graph）：60 个采样点，双线（下行蓝 / 上行绿），
    /// 自带网格与纵轴刻度。只做绘制，不做采样——采样由调用方 2 秒推一次。
    /// </summary>
    public class TrafficChart : FrameworkElement
    {
        private const int Capacity = 60;
        private readonly List<(double Down, double Up)> _points = new(Capacity);

        private static readonly Brush DownBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xFF)));
        private static readonly Brush UpBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0xB8, 0x94)));
        private static readonly Brush DownFill = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x2D, 0x8C, 0xFF)));
        private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0xF3, 0xFA)));
        private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6)));

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        public void Push(double downMbit, double upMbit)
        {
            if (double.IsNaN(downMbit) || double.IsInfinity(downMbit)) downMbit = 0;
            if (double.IsNaN(upMbit) || double.IsInfinity(upMbit)) upMbit = 0;
            _points.Add((Math.Max(0, downMbit), Math.Max(0, upMbit)));
            while (_points.Count > Capacity) _points.RemoveAt(0);
            InvalidateVisual();
        }

        public void Reset()
        {
            _points.Clear();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 4 || h <= 4) return;

            const double padTop = 8, padBottom = 16, padRight = 6;
            double plotW = Math.Max(1, w - padRight);
            double plotH = Math.Max(1, h - padTop - padBottom);

            double peak = 1;
            foreach (var p in _points) peak = Math.Max(peak, Math.Max(p.Down, p.Up));
            double top = NiceCeil(peak);

            // 网格：4 条横线
            var gridPen = new Pen(GridBrush, 1);
            for (int i = 0; i <= 3; i++)
            {
                double y = padTop + plotH * i / 3.0;
                dc.DrawLine(gridPen, new Point(0, y), new Point(plotW, y));
            }

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double scaleY = plotH / top;

            // 刻度：顶部标最大值，底部 0
            DrawLabel(dc, top >= 10 ? top.ToString("F0") : top.ToString("F1"), new Point(2, 0), dpi);
            DrawLabel(dc, "0", new Point(2, padTop + plotH - 11), dpi);
            DrawLabel(dc, "近 2 分钟", new Point(Math.Max(2, plotW - 58), 0), dpi);

            if (_points.Count < 2)
            {
                DrawLabel(dc, "采集中…", new Point(w / 2 - 20, padTop + plotH / 2 - 7), dpi);
                return;
            }

            double stepX = plotW / (Capacity - 1);
            int offset = Capacity - _points.Count;   // 数据不足时靠右补空

            var downGeo = new StreamGeometry();
            using (var ctx = downGeo.Open())
            {
                bool first = true;
                for (int i = 0; i < _points.Count; i++)
                {
                    double x = i * stepX;
                    double y = padTop + plotH - _points[i].Down * scaleY;
                    if (first) { ctx.BeginFigure(new Point(x, y), true, false); first = false; }
                    else ctx.LineTo(new Point(x, y), true, false);
                }
            }
            downGeo.Freeze();
            dc.DrawGeometry(DownFill, null, downGeo);

            dc.DrawGeometry(null, new Pen(DownBrush, 1.6), BuildLine(stepX, offset, padTop, plotH, scaleY, true));
            dc.DrawGeometry(null, new Pen(UpBrush, 1.4), BuildLine(stepX, offset, padTop, plotH, scaleY, false));
        }

        private Geometry BuildLine(double stepX, int offset, double padTop, double plotH, double scaleY, bool down)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int i = 0; i < _points.Count; i++)
                {
                    double x = i * stepX;
                    double v = down ? _points[i].Down : _points[i].Up;
                    double y = padTop + plotH - v * scaleY;
                    if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                    else ctx.LineTo(new Point(x, y), true, false);
                }
            }
            geo.Freeze();
            return geo;
        }

        private void DrawLabel(DrawingContext dc, string text, Point at, double dpi)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                10, LabelBrush, dpi);
            dc.DrawText(ft, at);
        }

        /// <summary>把峰值向上取整到 1 / 2 / 5 × 10^n，避免刻度跳变。</summary>
        private static double NiceCeil(double v)
        {
            if (v <= 0.5) return 0.5;
            double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
            double f = v / exp;
            double nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
            return nf * exp;
        }
    }
}
