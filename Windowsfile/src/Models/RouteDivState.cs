using System;

namespace NetPanel.Models
{
    /// <summary>
    /// 分流前的原状态，用于「恢复自动」精确还原。
    /// ⚠️ 必须用属性不能用手段：System.Text.Json 默认只序列化属性（IncludeFields 默认 false），
    /// 用字段会写成 {} —— 早期 routediv.json 一直是空对象，精确还原从来没生效过。
    /// </summary>
    public sealed class RouteDivState
    {
        public string OutNic { get; set; } = "";
        public int OrigMetric { get; set; }
        public string CreatedAt { get; set; } = "";

        public string InNic { get; set; } = "";              // 内网网卡名（DNS 防泄漏要还原它）
        public string OrigInDns { get; set; } = "";          // 内网网卡原 DNS（逗号分隔；空 = 原本是 DHCP）
        public string InNet { get; set; } = "";              // 内网网段地址，如 192.168.60.0
        public string InGw { get; set; } = "";               // 内网网关，如 192.168.60.1
        public int InIf { get; set; } = -1;                  // 内网网卡接口索引（route add 必须指定）
        public string InIp { get; set; } = "";               // 内网网卡本机 IP（重建 On-link 用）
        public int OrigNetbios { get; set; } = -1;           // 原 NetBIOS 设置（0/1/2；-1 = 没动过）

        public string InGuid { get; set; } = "";             // 内网网卡 GUID（索引会漂移，GUID 不会）
        public string OutGuid { get; set; } = "";            // 外网网卡 GUID

        public bool InHadDefaultGw { get; set; }             // 内网原本有没有默认路由（回滚要按原样重建）
        public string InOrigDefaultGw { get; set; } = "";    // 内网原默认网关
        public bool InHadNetRoute { get; set; }              // 内网原本有没有网段路由
        public string InOrigNetNextHop { get; set; } = "";   // 原网段路由下一跳（On-link 时为网卡自身 IP）

        public bool InOrigIpV6 { get; set; } = true;         // IPv6 绑定原状态
        public string InOrigiNetbios { get; set; } = "";     // NetBIOS 原值原始文本
        public bool InDnsIsDhcp { get; set; }                // 内网 DNS 原本是 DHCP 下发还是静态

        public bool OutAutomaticMetric { get; set; } = true; // 外网卡原来是不是「自动跃点」（决定还原方式）
    }
}
