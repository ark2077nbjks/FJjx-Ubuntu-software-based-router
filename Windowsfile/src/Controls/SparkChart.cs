using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NetPanel.Controls
{
    /// <summary>百分比曲线（0-100%），双序列：CPU 蓝 / 内存 绿。只绘制不做采样。</summary>
    public class SparkChart : FrameworkElement
    {
        private const int Capacity = 60;
        private readonly List<(double A, double B)> _pts = new(Capacity);

        private static readonly Brush ABrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xFF)));
        private static readonly Brush BBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0xB8, 0x94)));
        private static readonly Brush AFill = Freeze(new SolidColorBrush(Color.FromArgb(0x1E, 0x2D, 0x8C, 0xFF)));
        private static readonly Brush Grid = Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0xF3, 0xFA)));
        private static readonly Brush LabelBr = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6)));
        private static readonly Pen APen = Freeze(new Pen(ABrush, 1.6));
        private static readonly Pen BPen = Freeze(new Pen(BBrush, 1.6));

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
        private static Pen Freeze(Pen p) { p.Freeze(); return p; }

        public void Push(double a, double b)
        {
            if (double.IsNaN(a) || double.IsInfinity(a)) a = 0;
            if (double.IsNaN(b) || double.IsInfinity(b)) b = 0;
            _pts.Add((Math.Clamp(a, 0, 100), Math.Clamp(b, 0, 100)));
            if (_pts.Count > Capacity) _pts.RemoveAt(0);
            InvalidateVisual();
        }

        public void Clear() { _pts.Clear(); InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 30) return;
            const double padL = 30, padB = 14, padT = 6, padR = 4;
            double gw = w - padL - padR, gh = h - padT - padB;

            var gridPen = new Pen(Grid, 1);
            for (int i = 0; i <= 4; i++)
            {
                double y = padT + gh * i / 4.0;
                dc.DrawLine(gridPen, new Point(padL, y), new Point(padL + gw, y));
                var ft = Text(100 - i * 25);
                dc.DrawText(ft, new Point(2, y - ft.Height / 2));
            }

            if (_pts.Count < 2) return;
            double step = gw / (Capacity - 1);
            int start = Capacity - _pts.Count;
            var ga = new StreamGeometry(); var gb = new StreamGeometry();
            using (var ca = ga.Open()) using (var cb = gb.Open())
            {
                for (int i = 0; i < _pts.Count; i++)
                {
                    double x = padL + (start + i) * step;
                    var pa = new Point(x, padT + gh * (1 - _pts[i].A / 100.0));
                    var pb = new Point(x, padT + gh * (1 - _pts[i].B / 100.0));
                    if (i == 0) { ca.BeginFigure(pa, false, false); cb.BeginFigure(pb, false, false); }
                    else { ca.LineTo(pa, true, false); cb.LineTo(pb, true, false); }
                }
            }
            dc.DrawGeometry(null, APen, ga);
            dc.DrawGeometry(null, BPen, gb);
        }

        private FormattedText Text(int v) => new(v.ToString(CultureInfo.InvariantCulture),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, LabelBr, 1.0);
    }
}
