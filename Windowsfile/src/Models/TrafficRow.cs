using System;

namespace NetPanel.Models
{
    /// <summary>从 MainWindow 抽出来的数据类（2026-11-23）。字段与原来逐字一致。</summary>
    public sealed class TrafficRow
        {
            public string Adapter { get; set; } = "";
            public string Down { get; set; } = "";
            public string Up { get; set; } = "";
            public string Total { get; set; } = "";
            public string Share { get; set; } = "";
        }
}
