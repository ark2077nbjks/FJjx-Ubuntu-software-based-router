using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NetPanel.Controls
{
    /// <summary>小型转圈指示器（14px 弧线，1 秒一圈），用于按钮忙碌态。</summary>
    public class Spinner : FrameworkElement
    {
        private readonly RotateTransform _rotate = new(0, 7, 7);

        public Spinner()
        {
            Width = 14;
            Height = 14;
            RenderTransform = _rotate;
            Loaded += (_, _) => Start();
        }

        public Brush Stroke { get; set; } = new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xFF));

        private void Start()
        {
            var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            _rotate.BeginAnimation(RotateTransform.AngleProperty, anim);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var pen = new Pen(Stroke, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(7, 1.5), false, false);
                ctx.ArcTo(new Point(12.5, 7), new Size(5.5, 5.5), 0, false, SweepDirection.Clockwise, true, false);
                ctx.ArcTo(new Point(7, 12.5), new Size(5.5, 5.5), 0, false, SweepDirection.Clockwise, true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }
    }
}
