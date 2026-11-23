using System;

namespace NetPanel.Models
{
    /// <summary>从 MainWindow 抽出来的数据类（2026-11-23）。字段与原来逐字一致。</summary>
    public sealed class DevTrafficRow
        {
            public string Name { get; set; } = "";
            public string Mac { get; set; } = "";
            public string Ip { get; set; } = "";
            public string State { get; set; } = "";
            public string Share { get; set; } = "";
            public string TodayUp { get; set; } = "";
            public string TodayDown { get; set; } = "";
            public string SpanUp { get; set; } = "";
            public string SpanDown { get; set; } = "";
            public string Last { get; set; } = "";
            public long _todayBytes { get; set; }
        }
}
