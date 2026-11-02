using System.Windows;
using System.Windows.Media;

namespace NetPanel.Services;

/// <summary>
/// Lucide 图标（MIT，https://lucide.dev）转成的 WPF 图形。
/// 与设计图保持同一套线性图标风格：stroke 2px、圆角端点、24×24 视框。
/// </summary>
public static class LucideIcons
{
    public static DrawingImage Create(string kind, Brush color, double thickness = 2)
    {
        var pen = new Pen(color, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        var group = new DrawingGroup();
        void Add(string data) => group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(data)));
        void AddEllipse(double cx, double cy, double r) =>
            group.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new Point(cx, cy), r, r)));
        void AddRect(double x, double y, double w, double h, double radius) =>
            group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(x, y, w, h), radius, radius)));

        switch (kind)
        {
            case "globe":
                AddEllipse(12, 12, 10);
                Add("M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20");
                Add("M2 12h20");
                break;

            case "server":
                AddRect(2, 2, 20, 8, 2);
                AddRect(2, 14, 20, 8, 2);
                Add("M6 6h.01");
                Add("M6 18h.01");
                break;

            case "activity":
                Add("M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2");
                break;

            case "cpu":
                // 12 根引脚
                Add("M12 20v2"); Add("M12 2v2");
                Add("M17 20v2"); Add("M17 2v2");
                Add("M2 12h2"); Add("M2 17h2"); Add("M2 7h2");
                Add("M20 12h2"); Add("M20 17h2"); Add("M20 7h2");
                Add("M7 20v2"); Add("M7 2v2");
                AddRect(4, 4, 16, 16, 2);
                AddRect(8, 8, 8, 8, 1);
                break;

            case "users":
                Add("M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2");
                Add("M16 3.128a4 4 0 0 1 0 7.744");
                Add("M22 21v-2a4 4 0 0 0-3-3.87");
                AddEllipse(9, 7, 4);
                break;

            case "network":
                AddRect(16, 16, 6, 6, 1);
                AddRect(2, 16, 6, 6, 1);
                AddRect(9, 2, 6, 6, 1);
                Add("M5 16v-3a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v3");
                Add("M12 12V8");
                break;

            case "hard-drive":
                Add("M10 16h.01");
                Add("M2.212 11.577a2 2 0 0 0-.212.896V18a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-5.527a2 2 0 0 0-.212-.896L18.55 5.11A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z");
                Add("M21.946 12.013H2.054");
                Add("M6 16h.01");
                break;

            case "clock":
                AddEllipse(12, 12, 10);
                Add("M12 6v6l4 2");
                break;

            case "gauge":
                Add("m12 14 4-4");
                Add("M3.34 19a10 10 0 1 1 17.32 0");
                break;

            default:
                AddEllipse(12, 12, 10);
                break;
        }

        return new DrawingImage(group);
    }
}
