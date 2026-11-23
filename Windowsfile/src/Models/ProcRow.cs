using System;

namespace NetPanel.Models
{
    /// <summary>从 MainWindow 抽出来的数据类（2026-11-23）。字段与原来逐字一致。</summary>
    public sealed class ProcRow { public string Name { get; set; } = ""; public string Cpu { get; set; } = ""; public string Mem { get; set; } = ""; }
}
