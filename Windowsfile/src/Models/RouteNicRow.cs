using System;

namespace NetPanel.Models
{
    /// <summary>从 MainWindow 抽出来的数据类（2026-11-23）。字段与原来逐字一致。</summary>
    public sealed class RouteNicRow
        {
            public string Name = "";
            public string Ip = "";
            public string Gw = "";
            public int Metric;
            public string State = "";
            public string Prefix = "";   // 真实网段，形如 192.168.60.0/24（由 IP + 掩码算出）
            public int IfIndex = -1;     // 接口索引：route add ... if <n> 必须用它（会漂移，不能持久化）
            public string Guid = "";     // 网卡 GUID：重启/换卡都不变，状态文件用它做键
        }
}
