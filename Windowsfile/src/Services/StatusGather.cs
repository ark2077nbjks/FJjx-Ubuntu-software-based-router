using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace NetPanel.Services;

public sealed class Snapshot
{
    public string Local = "";
    public string Campus = "";
    public string Mini = "";
    public string Raw = "";
    public bool MiniOnline;
}

public static class StatusGather
{

    public static async Task<Snapshot> CollectAsync()
    {
        var snap = new Snapshot();
        var local = new StringBuilder();
        var campus = new StringBuilder();
        var mini = new StringBuilder();

        // ---------------- 本机网卡（只列物理网卡，虚拟/过滤器不算） ----------------
        local.AppendLine("【网卡】状态 / 速率 / IP / 网关");
        var adapters = await GetPhysicalAdaptersAsync();
        var nicByName = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .GroupBy(n => n.Name)
            .ToDictionary(g => g.Key, g => g.First());

        if (adapters.Count == 0)
        {
            local.AppendLine("  （读不到物理网卡，可能需要管理员权限）");
        }

        foreach (var a in adapters)
        {
            nicByName.TryGetValue(a.Name, out var ni);
            var v4 = new List<string>();
            string gw = "-";
            if (ni != null)
            {
                var props = ni.GetIPProperties();
                v4 = props.UnicastAddresses
                    .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(x => x.Address.ToString())
                    .ToList();
                gw = props.GatewayAddresses
                    .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(g => g.Address.ToString())
                    .FirstOrDefault() ?? "-";
            }

            local.AppendLine($"  {a.Name}   [{a.Status}]   {a.LinkSpeed}");
            local.AppendLine($"    IP {(v4.Count > 0 ? string.Join(", ", v4) : "无")}   网关 {gw}");
            local.AppendLine($"    {a.Description}");
        }

        // ---------------- 无线 ----------------
        local.AppendLine();
        local.AppendLine("【无线连接】");
        var profiles = await GetWifiProfilesAsync();
        if (profiles.Count == 0) local.AppendLine("  （当前没有已连接的无线网络）");
        foreach (var pf in profiles)
            local.AppendLine($"  SSID {pf.Name}    接口 {pf.Alias}");

        var w = await Proc.CmdAsync("netsh wlan show interfaces");
        string[] wifiKeys = { "SSID", "BSSID", "Signal", "Receive rate", "Transmit rate", "State",
                              "信号", "接收速率", "传输速率", "状态" };
        bool got = false;
        foreach (var raw in w.Output.Split('\n'))
        {
            var t = raw.Trim();
            var i = t.IndexOf(':');
            if (i <= 0) continue;
            var k = t[..i].Trim();
            var v = t[(i + 1)..].Trim();
            if (wifiKeys.Any(x => k == x)) { local.AppendLine($"  {k,-13}: {v}"); got = true; }
        }
        if (!got)
            local.AppendLine("  信号强度读不到：需要「管理员权限 + 系统位置服务开启」");


        // ---------------- 校园网 ----------------
        campus.AppendLine("【出口测试】");
        string code;
        string egress = "-";
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await c.GetAsync("http://connect.rom.miui.com/generate_204");
            code = ((int)resp.StatusCode).ToString();
        }
        catch (Exception ex) { code = "失败：" + ex.GetType().Name; }

        try
        {
            using var c2 = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            egress = (await c2.GetStringAsync("http://ip.3322.net")).Trim();
        }
        catch { }

        campus.AppendLine($"  generate_204 : {code}   {(code == "204" ? "√ 能上网" : "× 不通")}");
        campus.AppendLine($"  公网出口 IP  : {egress}");
        campus.AppendLine();
        campus.AppendLine("【判定】");
        campus.AppendLine("  204 = 已认证可上网；超时/失败 = 没认证或网断了");
        campus.AppendLine("  （校园网认证仍需手动，本软件不代做）");

        // ---------------- 远端软路由 ----------------
        var rc = RemoteConfig.Load();
        mini.AppendLine("【远端软路由】");
        if (!rc.IsConfigured)
        {
            mini.AppendLine("  还没配置");
            mini.AppendLine("  去「连接」页填地址和用户名");
        }
        else
        {
            // 多地址：先探一遍，把能用的那个选出来（HTTP 层不管，纯 TCP 探测很快）
            await Remote.ResolveAsync(rc);

            var (rc2, out2, err2) = await Remote.RunAsync("ssh.exe",
                $"-o ConnectTimeout=4 -o BatchMode=yes -o StrictHostKeyChecking=accept-new " +
                $"-o NumberOfPasswordPrompts=0 -p {rc.Port} {rc.Target} " +
                "\"bash /opt/grayrouter/bin/grayrouter-info.sh 2>/dev/null " +
                "|| bash ~/grayrouter-info.sh 2>/dev/null " +
                "|| bash ~/netpanel-info.sh 2>/dev/null || echo NOINFO\"",
                25);

            var d = ParseKv(out2);
            bool anyKey = d.ContainsKey("TOPOLOGY") || d.ContainsKey("AP_TYPE");

            if (rc2 == 0 && anyKey)
            {
                snap.MiniOnline = true;
                mini.AppendLine($"  地址 {rc.Host}  （通）");

                if (d.ContainsKey("TOPOLOGY"))
                {
                    // 通用版 grayroutor-info.sh
                    var topo = d.GetValueOrDefault("TOPOLOGY", "?");
                    mini.AppendLine($"  拓扑 {topo}   出口 {d.GetValueOrDefault("GATEWAY")}");
                    mini.AppendLine($"  WAN {d.GetValueOrDefault("WAN_IF")} {d.GetValueOrDefault("WAN_IP")}");
                    var ssid = d.GetValueOrDefault("SSID", "");
                    mini.AppendLine($"  LAN {d.GetValueOrDefault("LAN_IF")} {d.GetValueOrDefault("LAN_IP")}"
                                    + (string.IsNullOrWhiteSpace(ssid) ? "" : $"  热点 {ssid}"));
                    mini.AppendLine($"  {(d.GetValueOrDefault("LAN_ACTIVE") == "1" ? "√ 转发服务已启动" : "× 转发服务没起来")}"
                                    + $"   客户端 {d.GetValueOrDefault("CLIENTS")}");
                    mini.AppendLine($"  ip_forward={d.GetValueOrDefault("IP_FORWARD")}   出网 HTTP {d.GetValueOrDefault("OUT_HTTP")}");
                    mini.AppendLine($"  采集时间 {d.GetValueOrDefault("TS")}");
                }
                else
                {
                    // 兼容旧版 netpanel-info.sh
                    bool isAp = d.GetValueOrDefault("AP_TYPE") == "AP";
                    mini.AppendLine($"  模式 {(isAp ? "软路由（AP）" : "热点模式（客户端）")}");
                    mini.AppendLine($"  已运行 {d.GetValueOrDefault("UPTIME")}");
                    mini.AppendLine($"  有线 {d.GetValueOrDefault("WAN_IP")}  {(d.GetValueOrDefault("CARRIER") == "1" ? "已插线" : "没插线")}  {d.GetValueOrDefault("SPEED")} Mb/s");
                    mini.AppendLine($"  无线 {d.GetValueOrDefault("AP_TYPE")} / {d.GetValueOrDefault("AP_SSID")}  {d.GetValueOrDefault("AP_IP")}");
                    mini.AppendLine($"  客户端 {d.GetValueOrDefault("CLIENTS")}   转发 ip_forward={d.GetValueOrDefault("FWD")}");
                    mini.AppendLine($"  出网 HTTPS {d.GetValueOrDefault("HTTPS")}   出口 {d.GetValueOrDefault("EGRESS")}");
                }
            }
            else if (out2.Contains("NOINFO"))
            {
                mini.AppendLine($"  地址 {rc.Host}  （通）");
                mini.AppendLine("  但远端没有采集脚本");
                mini.AppendLine("  把包里的 bin/grayrouter-info.sh 拷过去即可");
            }
            else
            {
                var why = (err2 + " " + out2).Trim();
                if (why.Contains("Permission denied")) mini.AppendLine("  认证失败：去「连接」页改登录方式");
                else if (why.Contains("timed out") || why.Contains("Timeout")) mini.AppendLine($"  连不上 {rc.Host}（不在同一网络？）");
                else mini.AppendLine("  连不上" + (string.IsNullOrWhiteSpace(why) ? "" : "：" + why.Split('\n')[0]));
            }
        }

        snap.Local = local.ToString().TrimEnd();
        snap.Campus = campus.ToString().TrimEnd();
        snap.Mini = mini.ToString().TrimEnd();
        snap.Raw = $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\n{snap.Local}\n\n{snap.Campus}\n\n{snap.Mini}";
        return snap;
    }

    /// <summary>只取物理网卡（WLAN / 以太网），虚拟网卡和过滤器驱动不算</summary>
    public static async Task<List<AdapterInfo>> GetPhysicalAdaptersAsync()
    {
        var list = new List<AdapterInfo>();
        var (rc, outText) = await Proc.CmdAsync(
            "powershell -NoProfile -NonInteractive -Command \"Get-NetAdapter -Physical | Select-Object Name,InterfaceDescription,Status,LinkSpeed,MacAddress | ConvertTo-Json -Compress\"",
            30);
        if (rc != 0 || string.IsNullOrWhiteSpace(outText)) return list;

        var json = outText.Trim();
        if (json.StartsWith("{")) json = "[" + json + "]";
        int s = json.IndexOf('['), e = json.LastIndexOf(']');
        if (s < 0 || e < s) return list;
        json = json[s..(e + 1)];

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                list.Add(new AdapterInfo
                {
                    Name = el.TryGetProperty("Name", out var n) ? (n.GetString() ?? "") : "",
                    Description = el.TryGetProperty("InterfaceDescription", out var d) ? (d.GetString() ?? "") : "",
                    Status = el.TryGetProperty("Status", out var st) ? (st.GetString() ?? "") : "",
                    LinkSpeed = el.TryGetProperty("LinkSpeed", out var ls) ? (ls.GetString() ?? "") : "",
                    Mac = el.TryGetProperty("MacAddress", out var mc) ? (mc.GetString() ?? "") : "",
                });
            }
        }
        catch (Exception ex) { AppPaths.Log("解析网卡 JSON 失败: " + ex.Message + " | 原文: " + json); }

        // 回填 IP / 网关（NetworkInterface 按名称对应）
        var nets = NetworkInterface.GetAllNetworkInterfaces()
            .GroupBy(x => x.Name)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var a in list)
        {
            if (!nets.TryGetValue(a.Name, out var ni)) continue;
            var props = ni.GetIPProperties();
            var v4 = props.UnicastAddresses
                .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(x => x.Address.ToString())
                .ToList();
            a.Ip = v4.Count > 0 ? string.Join(", ", v4) : "无";
            a.Gateway = props.GatewayAddresses
                .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(g => g.Address.ToString())
                .FirstOrDefault() ?? "-";
        }

        return list;
    }

    public static async Task<List<WifiProfile>> GetWifiProfilesAsync()
    {
        var list = new List<WifiProfile>();
        var (rc, outText) = await Proc.CmdAsync(
            "powershell -NoProfile -NonInteractive -Command \"Get-NetConnectionProfile | Select-Object Name,InterfaceAlias | ConvertTo-Json -Compress\"",
            30);
        if (rc != 0 || string.IsNullOrWhiteSpace(outText)) return list;

        var json = outText.Trim();
        if (json.StartsWith("{")) json = "[" + json + "]";
        int s = json.IndexOf('['), e = json.LastIndexOf(']');
        if (s < 0 || e < s) return list;
        json = json[s..(e + 1)];

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                list.Add(new WifiProfile
                {
                    Name = el.TryGetProperty("Name", out var n) ? (n.GetString() ?? "") : "",
                    Alias = el.TryGetProperty("InterfaceAlias", out var a) ? (a.GetString() ?? "") : "",
                });
            }
        }
        catch (Exception ex) { AppPaths.Log("解析 WiFi 配置失败: " + ex.Message); }
        return list;
    }

    static Dictionary<string, string> ParseKv(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            var i = t.IndexOf('=');
            if (i <= 0) continue;
            d[t[..i].Trim()] = t[(i + 1)..].Trim();
        }
        return d;
    }
}

public sealed class AdapterInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public string LinkSpeed { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Gateway { get; set; } = "";

    /// <summary>管理员状态是 Disabled（而不是仅仅没插线）</summary>
    public bool IsDisabled => Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
}

public sealed class WifiProfile
{
    public string Name { get; set; } = "";
    public string Alias { get; set; } = "";
}
