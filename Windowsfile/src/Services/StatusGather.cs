using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace NetPanel.Services;

public sealed class StatusRow
{
    public string Label = "";
    public string Value = "";
    public bool Sub;           // 附属说明行（型号、提示），灰色小字、不占标签列
    public string Icon = "";   // 复用图标库里的 png 名（不含 /icons/ 前缀）
    public List<StatusRow>? Children;   // 非空 = 可折叠的组（默认收起）
}

public sealed class RemoteNic
{
    public string Name = "";
    public string State = "";
    public string Speed = "";
    public string Driver = "";
    public string Bus = "";
    public string Model = "";
    public string Ip = "";
    public string Role = "";
}

public sealed class RemoteVnic
{
    public string Name = "", State = "", Ip = "";
}

public sealed class StatusSection
{
    public string Title = "";
    public List<StatusRow> Rows = new();
}

public sealed class Snapshot
{
    public string Local = "";
    public string Campus = "";
    public string Mini = "";
    public string Raw = "";
    public bool MiniOnline;
    public bool CampusOk;      // 204 探测通过 = 已认证能上网

    public List<StatusSection> LocalSections = new();
    public List<StatusSection> CampusSections = new();
    public List<StatusSection> MiniSections = new();
    public List<RemoteNic> RemoteNics = new();
    public int RemoteVirtualNics;
    public List<RemoteVnic> RemoteVnics = new();
}

public static class StatusGather
{
    /// <summary>JSON 里可能是字符串、数字，甚至空对象（PowerShell 枚举），统一安全取值</summary>
    private static string SafeStr(System.Text.Json.JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => v.GetString() ?? "",
            System.Text.Json.JsonValueKind.Number => v.ToString(),
            System.Text.Json.JsonValueKind.True => "Up",
            System.Text.Json.JsonValueKind.False => "Disabled",
            System.Text.Json.JsonValueKind.Null => "",
            _ => v.ToString() is "{}" ? "" : v.ToString(),
        };
    }


    /// <summary>uptime 英文（2 days, 21 hours）→ 中文</summary>
    /// <summary>看门狗状态：observe=只看不动，act=会自动重启 AP</summary>
    /// <summary>远端看门狗当前模式：observe / act（面板开关用）</summary>
    public static string LastWatchMode = "observe";

    /// <summary>远端当前模式：router / hotspot（空 = 还没读到）</summary>
    public static string LastMode = "";

    private static string WatchText(Dictionary<string, string> d)
    {
        var st = d.GetValueOrDefault("WATCH_STATE", "");
        if (string.IsNullOrWhiteSpace(st) || st == "none")
            return "— 没装看门狗";

        var mode = d.GetValueOrDefault("WATCH_MODE", "");
        LastWatchMode = string.IsNullOrWhiteSpace(mode) ? "observe" : mode;
        var ts = d.GetValueOrDefault("WATCH_TS", "");
        var hm = ts.Length >= 16 ? ts.Substring(11, 5) : ts;
        var obs = mode == "observe";

        if (d.GetValueOrDefault("WATCH_ACTED", "false") == "true")
            return $"√ {hm} 已自动重启 AP";
        if (st == "suspect")
            return obs ? $"⚠ {hm} 检测到假在线（观察模式，没动手）" : $"⚠ {hm} 假在线";
        return obs ? $"√ {hm} 正常（观察模式）" : $"√ {hm} 正常";
    }

    private static string CnUptime(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        var t = Regex.Replace(s, @"(\d+)\s*days?", "$1 天");
        t = Regex.Replace(t, @"(\d+)\s*hours?", "$1 小时");
        t = Regex.Replace(t, @"(\d+)\s*minutes?", "$1 分钟");
        t = t.Replace(",", " ").Replace("  ", " ").Trim();
        return t;
    }

    /// <summary>netsh 输出的英文键 → 界面中文标签</summary>
    private static string WifiLabel(string k) => k switch
    {
        "SSID" => "SSID",
        "BSSID" => "BSSID",
        "Signal" or "信号" => "信号",
        "Receive rate" or "接收速率" => "接收速率",
        "Transmit rate" or "传输速率" => "传输速率",
        "State" or "状态" => "状态",
        _ => k,
    };

    public static async Task<Snapshot> CollectAsync()
    {
        var snap = new Snapshot();
        var local = new StringBuilder();
        var campus = new StringBuilder();
        var mini = new StringBuilder();

        // ---------------- 本机网卡（只列物理网卡，虚拟/过滤器不算） ----------------
        local.AppendLine("【网卡】状态 / 速率 / IP / 网关");
        var secNic = new StatusSection { Title = "网卡" };
        var adapters = await GetPhysicalAdaptersAsync();
        var nicByName = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .GroupBy(n => n.Name)
            .ToDictionary(g => g.Key, g => g.First());

        if (adapters.Count == 0)
        {
            local.AppendLine("  （读不到物理网卡，可能需要管理员权限）");
            secNic.Rows.Add(new StatusRow { Label = "网卡", Value = "读不到，可能需要管理员权限" });
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

            local.AppendLine($"  {a.Name}   [{LocalStatus(a.Status, a.IsDisabled)}]   {a.LinkSpeed}");
            local.AppendLine($"    IP {(v4.Count > 0 ? string.Join(", ", v4) : "无")}   网关 {gw}");
            local.AppendLine($"    {a.Description}");

            if (secNic.Rows.Count > 0) secNic.Rows.Add(new StatusRow { Label = "", Value = "" });
            secNic.Rows.Add(new StatusRow { Label = a.Name, Value = $"[{LocalStatus(a.Status, a.IsDisabled)}] {a.LinkSpeed}" });
            secNic.Rows.Add(new StatusRow { Label = "IP", Value = v4.Count > 0 ? string.Join(", ", v4) : "无" });
            secNic.Rows.Add(new StatusRow { Label = "网关", Value = gw });
            secNic.Rows.Add(new StatusRow { Label = "型号", Value = a.Description, Sub = true });
        }

        // ---- 虚拟网卡 / 系统虚拟口（Hyper-V、VMware、WSL、Docker、Wi-Fi Direct、WAN Miniport…） ----
        var physNames = new HashSet<string>(adapters.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        var virt = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && !physNames.Contains(n.Name))
            .Where(n => !new[] { "filter", "scheduler", "qos", "packet" }.Any(k => n.Name.ToLowerInvariant().Contains(k)))
            .Select(n =>
            {
                var v4 = n.GetIPProperties().UnicastAddresses
                    .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(x => x.Address.ToString()).ToList();
                return new { n.Name, n.OperationalStatus, n.NetworkInterfaceType, Ip = v4.FirstOrDefault() ?? "" };
            })
            .OrderByDescending(v => v.Ip != "")
            .ThenByDescending(v => v.OperationalStatus == OperationalStatus.Up)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (virt.Count > 0)
        {
            var up = virt.Count(v => v.OperationalStatus == OperationalStatus.Up);
            local.AppendLine();
            local.AppendLine($"【虚拟网卡】{virt.Count} 个（在线 {up}）");
            var kids = new List<StatusRow>();
            foreach (var v in virt)
            {
                var st = v.OperationalStatus == OperationalStatus.Up ? "已连接" : "未连接";
                var ip = string.IsNullOrEmpty(v.Ip) ? "无 IP" : v.Ip;
                var line = $"{v.Name} · {st} · {ip}";
                local.AppendLine("  " + line);
                kids.Add(new StatusRow { Label = "", Value = line });
            }
            secNic.Rows.Add(new StatusRow { Label = "", Value = "" });
            secNic.Rows.Add(new StatusRow { Label = "虚拟网卡", Value = $"{virt.Count} 个（在线 {up}）", Children = kids });
        }

        snap.LocalSections.Add(secNic);

        // ---------------- 无线 ----------------
        local.AppendLine();
        local.AppendLine("【无线连接】");
        var secWifi = new StatusSection { Title = "无线连接" };
        var profiles = await GetWifiProfilesAsync();
        if (profiles.Count == 0)
        {
            local.AppendLine("  （当前没有已连接的无线网络）");
            secWifi.Rows.Add(new StatusRow { Label = "状态", Value = "当前没有已连接的无线网络" });
        }
        foreach (var pf in profiles)
        {
            local.AppendLine($"  SSID {pf.Name}    接口 {pf.Alias}");
            secWifi.Rows.Add(new StatusRow { Label = "SSID", Value = pf.Name });
            secWifi.Rows.Add(new StatusRow { Label = "接口", Value = pf.Alias });
        }

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
            if (wifiKeys.Any(x => k == x))
            {
                local.AppendLine($"  {k,-13}: {v}");
                secWifi.Rows.Add(new StatusRow { Label = WifiLabel(k), Value = v });
                got = true;
            }
        }
        if (!got)
        {
            local.AppendLine("  信号强度读不到：需要「管理员权限 + 系统位置服务开启」");
            secWifi.Rows.Add(new StatusRow { Label = "信号", Value = "读不到（需要管理员权限 + 位置服务）", Sub = true });
        }
        snap.LocalSections.Add(secWifi);


        // ---------------- 校园网 ----------------
        campus.AppendLine("【出口测试】");
        var secOut = new StatusSection { Title = "出口测试" };
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
        snap.CampusOk = code == "204";
        secOut.Rows.Add(new StatusRow { Label = "连通测试", Value = code == "204" ? $"{code} √ 能上网" : $"{code} × 不通" });
        campus.AppendLine($"  公网出口 IP  : {egress}");
        secOut.Rows.Add(new StatusRow { Label = "公网出口", Value = egress });
        snap.CampusSections.Add(secOut);

        campus.AppendLine();
        campus.AppendLine("【判定】");
        campus.AppendLine("  204 = 已认证可上网；超时/失败 = 没认证或网断了");
        campus.AppendLine("  （认证可到左侧「校园网」页填账号密码，由 minipc 执行）");
        snap.CampusSections.Add(new StatusSection
        {
            Title = "判定",
            Rows =
            {
                new StatusRow { Label = "", Value = "204 = 已认证可上网；超时/失败 = 没认证或网断了", Sub = true },
                new StatusRow { Label = "", Value = "认证去左侧「校园网」页：填账号密码 → minipc 自动登录 / 注销", Sub = true },
            }
        });

        // ---------------- 远端软路由 ----------------
        var rc = RemoteConfig.Load();
        mini.AppendLine("【远端软路由】");
        var secMini = new StatusSection { Title = "" };
        if (!rc.IsConfigured)
        {
            mini.AppendLine("  还没配置");
            mini.AppendLine("  去「连接」页填地址和用户名");
            secMini.Rows.Add(new StatusRow { Label = "状态", Value = "还没配置" });
            secMini.Rows.Add(new StatusRow { Label = "", Value = "去「连接」页填地址和用户名", Sub = true });
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
                "|| bash ~/netpanel-info.sh 2>/dev/null || echo NOINFO; " +
                "echo ---NICS---; (bash ~/netpanel-nics.sh 2>/dev/null || bash /opt/grayrouter/bin/netpanel-nics.sh 2>/dev/null)\"",
                25);

            var nics = new List<RemoteNic>();
            if (out2.Contains("---NICS---"))
            {
                var parts = out2.Split("---NICS---", 2);
                out2 = parts[0];
                foreach (var line in parts[1].Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("NIC|"))
                    {
                        var f = t.Split('|');
                        if (f.Length >= 9)
                            nics.Add(new RemoteNic
                            {
                                Name = f[1], State = f[2], Speed = f[3], Driver = f[4],
                                Bus = f[5], Model = f[6], Ip = f[7], Role = f[8],
                            });
                    }
                    else if (t.StartsWith("VIRTUAL|") && int.TryParse(t.Split('|')[1], out var vc))
                        snap.RemoteVirtualNics = vc;
                    else if (t.StartsWith("VNIC|"))
                    {
                        var vf = t.Split('|');
                        if (vf.Length >= 4)
                            snap.RemoteVnics.Add(new RemoteVnic { Name = vf[1], State = vf[2], Ip = vf[3] });
                    }
                }
            }
            snap.RemoteNics = nics;

            var d = ParseKv(out2);
            bool anyKey = d.ContainsKey("TOPOLOGY") || d.ContainsKey("AP_TYPE");

            if (rc2 == 0 && anyKey)
            {
                snap.MiniOnline = true;
                mini.AppendLine($"  地址 {rc.Host}  （通）");
                secMini.Rows.Add(new StatusRow { Label = "地址", Value = $"{rc.Host}（通）" });

                if (d.ContainsKey("TOPOLOGY"))
                {
                    // 通用版 grayroutor-info.sh
                    var topo = d.GetValueOrDefault("TOPOLOGY", "?");
                    var ssid = d.GetValueOrDefault("SSID", "");
                    mini.AppendLine($"  拓扑 {topo}   出口 {d.GetValueOrDefault("GATEWAY")}");
                    mini.AppendLine($"  WAN {d.GetValueOrDefault("WAN_IF")} {d.GetValueOrDefault("WAN_IP")}");
                    mini.AppendLine($"  LAN {d.GetValueOrDefault("LAN_IF")} {d.GetValueOrDefault("LAN_IP")}"
                                    + (string.IsNullOrWhiteSpace(ssid) ? "" : $"  热点 {ssid}"));
                    mini.AppendLine($"  {(d.GetValueOrDefault("LAN_ACTIVE") == "1" ? "√ 转发服务已启动" : "× 转发服务没起来")}"
                                    + $"   客户端 {d.GetValueOrDefault("CLIENTS")}");
                    mini.AppendLine($"  ip_forward={d.GetValueOrDefault("IP_FORWARD")}   出网 HTTP {d.GetValueOrDefault("OUT_HTTP")}");
                    mini.AppendLine($"  采集时间 {d.GetValueOrDefault("TS")}");

                    secMini.Rows.Add(new StatusRow { Label = "拓扑", Value = $"{topo} · 出口 {d.GetValueOrDefault("GATEWAY")}" });
                    secMini.Rows.Add(new StatusRow { Label = "WAN", Value = $"{d.GetValueOrDefault("WAN_IF")} {d.GetValueOrDefault("WAN_IP")}" });
                    secMini.Rows.Add(new StatusRow { Label = "LAN", Value = $"{d.GetValueOrDefault("LAN_IF")} {d.GetValueOrDefault("LAN_IP")}"
                                                                    + (string.IsNullOrWhiteSpace(ssid) ? "" : $" · 热点 {ssid}") });
                    secMini.Rows.Add(new StatusRow { Label = "转发", Value = (d.GetValueOrDefault("LAN_ACTIVE") == "1" ? "√ 已启动" : "× 没起来")
                                                                    + $" · 客户端 {d.GetValueOrDefault("CLIENTS")}" });
                    secMini.Rows.Add(new StatusRow { Label = "出网", Value = $"HTTP {d.GetValueOrDefault("OUT_HTTP")} · ip_forward={d.GetValueOrDefault("IP_FORWARD")}" });

                    // 伪装层（UA3F：IPID 重写；不做 MitM）
                    var ua3fCn = !d.ContainsKey("UA3F")
                        ? "— 远端没报（旧版采集脚本）"
                        : (d.GetValueOrDefault("UA3F") == "active"
                            ? (d.GetValueOrDefault("UA3F_LISTEN", "0") == "1" ? "√ 运行中 · 1080 在听" : "√ 运行中 · 1080 没监听")
                            : "× 未运行");
                    mini.AppendLine($"  伪装 {ua3fCn}");
                    secMini.Rows.Add(new StatusRow { Label = "伪装", Value = ua3fCn });
                    var watchCn = WatchText(d);
                    mini.AppendLine($"  守护 {watchCn}");
                    secMini.Rows.Add(new StatusRow { Label = "守护", Value = watchCn });
                    secMini.Rows.Add(new StatusRow { Label = "采集", Value = d.GetValueOrDefault("TS"), Sub = true });
                }
                else
                {
                    // 兼容旧版 netpanel-info.sh
                    bool isAp = d.GetValueOrDefault("AP_TYPE") == "AP";
                    mini.AppendLine($"  模式 {(isAp ? "软路由（AP）" : "热点模式（客户端）")}");
                    mini.AppendLine($"  已运行 {CnUptime(d.GetValueOrDefault("UPTIME"))}");
                    mini.AppendLine($"  有线 {d.GetValueOrDefault("WAN_IP")}  {(d.GetValueOrDefault("CARRIER") == "1" ? "已插线" : "没插线")}  {d.GetValueOrDefault("SPEED")} Mb/s");
                    mini.AppendLine($"  无线 {d.GetValueOrDefault("AP_TYPE")} / {d.GetValueOrDefault("AP_SSID")}  {d.GetValueOrDefault("AP_IP")}");
                    mini.AppendLine($"  客户端 {d.GetValueOrDefault("CLIENTS")}   转发 ip_forward={d.GetValueOrDefault("FWD")}");
                    mini.AppendLine($"  出网 HTTPS {d.GetValueOrDefault("HTTPS")}   出口 {d.GetValueOrDefault("EGRESS")}");

                    LastMode = isAp ? "router" : "hotspot";
                    secMini.Rows.Add(new StatusRow { Label = "模式", Value = isAp ? "软路由（AP）" : "热点模式（客户端）" });
                    secMini.Rows.Add(new StatusRow { Label = "已运行", Value = CnUptime(d.GetValueOrDefault("UPTIME")) });
                    secMini.Rows.Add(new StatusRow { Label = "有线", Value = $"{d.GetValueOrDefault("WAN_IP")}  "
                        + $"{(d.GetValueOrDefault("CARRIER") == "1" ? "已插线" : "没插线")}  {d.GetValueOrDefault("SPEED")} Mb/s" });
                    secMini.Rows.Add(new StatusRow { Label = "无线", Value = $"{d.GetValueOrDefault("AP_TYPE")} / {d.GetValueOrDefault("AP_SSID")}  {d.GetValueOrDefault("AP_IP")}" });
                    secMini.Rows.Add(new StatusRow { Label = "客户端", Value = $"{d.GetValueOrDefault("CLIENTS")} · 转发 ip_forward={d.GetValueOrDefault("FWD")}" });
                    secMini.Rows.Add(new StatusRow { Label = "出网", Value = $"HTTPS {d.GetValueOrDefault("HTTPS")} · 出口 {d.GetValueOrDefault("EGRESS")}" });

                    // 伪装层（旧版采集脚本不带这项时显示“—”，不误报未运行）
                    var ua3fCn2 = !d.ContainsKey("UA3F")
                        ? "— 远端没报（旧版采集脚本）"
                        : (d.GetValueOrDefault("UA3F") == "1" || d.GetValueOrDefault("UA3F") == "active" ? "√ 运行中" : "× 未运行");
                    mini.AppendLine($"  伪装 {ua3fCn2}");
                    secMini.Rows.Add(new StatusRow { Label = "伪装", Value = ua3fCn2 });
                    var watchCn2 = WatchText(d);
                    mini.AppendLine($"  守护 {watchCn2}");
                    secMini.Rows.Add(new StatusRow { Label = "守护", Value = watchCn2 });
                }
            }
            else if (out2.Contains("NOINFO"))
            {
                mini.AppendLine($"  地址 {rc.Host}  （通）");
                mini.AppendLine("  但远端没有采集脚本");
                mini.AppendLine("  把包里的 bin/grayrouter-info.sh 拷过去即可");
                secMini.Rows.Add(new StatusRow { Label = "地址", Value = $"{rc.Host}（通）" });
                secMini.Rows.Add(new StatusRow { Label = "采集", Value = "远端没有采集脚本", Sub = true });
                secMini.Rows.Add(new StatusRow { Label = "", Value = "把包里的 bin/grayrouter-info.sh 拷过去即可", Sub = true });
            }
            else
            {
                var why = (err2 + " " + out2).Trim();
                string msg;
                if (why.Contains("Permission denied")) msg = "认证失败：去「连接」页改登录方式";
                else if (why.Contains("timed out") || why.Contains("Timeout")) msg = $"连不上 {rc.Host}（不在同一网络？）";
                else msg = "连不上" + (string.IsNullOrWhiteSpace(why) ? "" : "：" + why.Split('\n')[0]);
                mini.AppendLine("  " + msg);
                secMini.Rows.Add(new StatusRow { Label = "状态", Value = msg, Sub = true });
            }
        }

        snap.MiniSections.Add(secMini);

        // ---------------- 远端网卡清单（含 USB 网卡，热插拔后刷新即可见） ----------------
        if (snap.RemoteNics.Count > 0)
        {
            var secRnic = new StatusSection { Title = "网卡" };
            foreach (var nic in snap.RemoteNics)
            {
                var isWifi = nic.Name.StartsWith("wl") || nic.Bus == "usb" && nic.Model.Contains("Wireless");
                var icon = isWifi ? "wifi.png" : "lan.png";
                var speed = string.IsNullOrEmpty(nic.Speed) ? "—" : nic.Speed + " Mb/s";
                var role = string.IsNullOrWhiteSpace(nic.Role) ? "备用" : nic.Role;
                var st = nic.State switch { "up" => "已连接", "down" => "未连接", "unknown" => "状态未知", _ => nic.State };
                if (nic.Bus == "usb") st += "（USB）";

                var ipTxt = string.IsNullOrWhiteSpace(nic.Ip) || nic.Ip == "-" ? "" : $" · {nic.Ip}";
                secRnic.Rows.Add(new StatusRow
                {
                    Icon = icon,
                    Label = nic.Name,
                    Value = $"{st} · {speed} · {role}{ipTxt}",
                });

                // 第二行只放型号/驱动；总线只有 USB 才写，免得换行难看
                var meta = string.Join(" · ", new[] { nic.Model, nic.Driver,
                        nic.Bus == "usb" ? "USB" : "" }
                    .Where(x => !string.IsNullOrWhiteSpace(x) && x != "-"));
                if (!string.IsNullOrWhiteSpace(meta))
                    secRnic.Rows.Add(new StatusRow { Label = "", Value = meta, Sub = true });
            }
            // 远端虚拟网卡：集中成一个可折叠组，默认收起
            if (snap.RemoteVnics.Count > 0)
            {
                var kids = snap.RemoteVnics
                    .OrderByDescending(v => v.Ip != "-" && v.Ip != "")
                    .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(v => new StatusRow
                    {
                        Label = "",
                        Value = $"{v.Name} · {(v.State == "up" ? "已连接" : "未连接")} · {(v.Ip == "-" || v.Ip == "" ? "无 IP" : v.Ip)}",
                    })
                    .ToList();
                var upN = snap.RemoteVnics.Count(v => v.State == "up");
                secRnic.Rows.Add(new StatusRow { Label = "虚拟网卡", Value = $"{snap.RemoteVnics.Count} 个（在线 {upN}）", Children = kids });
            }
            else if (snap.RemoteVirtualNics > 0)
                secRnic.Rows.Add(new StatusRow { Label = "", Value = $"另有 {snap.RemoteVirtualNics} 个虚拟网卡（docker / veth，未列出）", Sub = true });

            snap.MiniSections.Add(secRnic);
        }


        snap.Local = local.ToString().TrimEnd();
        snap.Campus = campus.ToString().TrimEnd();
        snap.Mini = mini.ToString().TrimEnd();
        snap.Raw = $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\n{snap.Local}\n\n{snap.Campus}\n\n{snap.Mini}";
        return snap;
    }


    /// <summary>只取远端网口名字（一行），给热插拔检测用 —— 一次 ssh，几百字节</summary>
    public static async Task<string> RemoteNicNamesAsync(RemoteConfig rc)
    {
        if (!rc.IsConfigured) return "";
        try
        {
            await Remote.ResolveAsync(rc);
            var (code, outp, _) = await Remote.RunAsync("ssh.exe",
                $"-o ConnectTimeout=4 -o BatchMode=yes -o StrictHostKeyChecking=accept-new " +
                $"-o NumberOfPasswordPrompts=0 -p {rc.Port} {rc.Target} " +
                "\"ls /sys/class/net 2>/dev/null | sort | tr '\\n' ' '\"", 10);
            return code == 0 ? outp.Trim() : "";
        }
        catch { return ""; }
    }

    /// <summary>只取物理网卡（WLAN / 以太网），虚拟网卡和过滤器驱动不算</summary>

    /// <summary>Windows 状态词 → 中文（Not Present 在设备被禁用时也会出现，别直接翻成“不存在”）</summary>
    public static string LocalStatus(string status, bool isDisabled)
    {
        if (isDisabled) return "已禁用";
        return status.Trim() switch
        {
            "Up" => "已连接",
            "Disconnected" => "未连接",
            "Disabled" => "已禁用",
            "Not Present" => "未就绪",
            _ => string.IsNullOrWhiteSpace(status) ? "未知" : status,
        };
    }

    public static async Task<List<AdapterInfo>> GetPhysicalAdaptersAsync()
    {
        var list = new List<AdapterInfo>();

        // 一趟 PowerShell 同时拿「物理网卡清单」和「设备管理器里被禁用的网卡」：
        // 被禁用的设备 Get-NetAdapter 只报 Not Present，真信号是 PnP 的 Status=Error(ProblemCode 22)
        const string ps =
            "$a=@(Get-NetAdapter -Physical | Select-Object Name,InterfaceDescription," +
            "@{n='Status';e={[string]$_.Status}},LinkSpeed,MacAddress); " +
            "$d=@(Get-PnpDevice -Class Net -ErrorAction SilentlyContinue | Where-Object {$_.Status -eq 'Error'} | " +
            "Select-Object -ExpandProperty FriendlyName); " +
            "[pscustomobject]@{adapters=$a;disabled=$d} | ConvertTo-Json -Depth 4 -Compress";

        var (rc, outText) = await Proc.CmdAsync("powershell -NoProfile -NonInteractive -Command \"" + ps + "\"", 30);
        if (rc != 0 || string.IsNullOrWhiteSpace(outText))
        {
            var first = (outText ?? "").Split('\n').FirstOrDefault(x => x.Trim() != "") ?? "";
            AppPaths.Log($"[网卡] 查询失败 rc={rc} 输出长度={(outText ?? "").Length} 首行={first.Trim()}");
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(outText.Trim());
            var root = doc.RootElement;

            if (root.TryGetProperty("adapters", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    list.Add(new AdapterInfo
                    {
                        Name = SafeStr(el, "Name"),
                        Description = SafeStr(el, "InterfaceDescription"),
                        Status = SafeStr(el, "Status"),
                        LinkSpeed = SafeStr(el, "LinkSpeed"),
                        Mac = SafeStr(el, "MacAddress"),
                    });
                }
            }

            if (root.TryGetProperty("disabled", out var dis) && dis.ValueKind == JsonValueKind.Array)
            {
                var errNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var el in dis.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) continue;
                    var n = el.GetString();
                    if (!string.IsNullOrWhiteSpace(n)) errNames.Add(n);
                }
                foreach (var a in list)
                    if (errNames.Contains(a.Description) || errNames.Contains(a.Name))
                        a.IsDisabled = true;
            }
        }
        catch (Exception ex) { AppPaths.Log("解析网卡 JSON 失败: " + ex.Message + " | 原文: " + outText); }

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
        if (rc != 0 || string.IsNullOrWhiteSpace(outText))
        {
            var first = (outText ?? "").Split('\n').FirstOrDefault(x => x.Trim() != "") ?? "";
            AppPaths.Log($"[WiFi] 取连接配置失败 rc={rc} 输出长度={(outText ?? "").Length} 首行={first.Trim()}");
            return list;
        }

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

    /// <summary>网卡处于禁用状态：Status=Disabled，或被设备管理器禁用（PnP ProblemCode=22，Get-NetAdapter 只报 Not Present）</summary>
    public bool IsDisabled { get; set; } = false;
}

public sealed class WifiProfile
{
    public string Name { get; set; } = "";
    public string Alias { get; set; } = "";
}
