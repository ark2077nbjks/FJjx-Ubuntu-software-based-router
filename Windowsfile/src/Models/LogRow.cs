using System;
using System.Windows.Media;

namespace NetPanel.Models
{
    /// <summary>从 MainWindow 抽出来的数据类（2026-11-23）。字段与原来逐字一致。</summary>
    public sealed class LogRow
        {
            public string Time { get; set; } = "";
            public string Level { get; set; } = "INFO";
            public string Msg { get; set; } = "";
            public Brush BadgeBg { get; set; } = Brushes.Transparent;
            public Brush BadgeFg { get; set; } = Brushes.Gray;
        }
}
