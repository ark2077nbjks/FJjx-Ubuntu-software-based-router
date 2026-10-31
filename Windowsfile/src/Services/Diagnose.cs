using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetPanel.Services;

/// <summary>网络诊断的一行结果</summary>
public sealed class DiagItem
{
    public string Layer { get; set; } = "";      // 第几层 / 分类
    public string Name { get; set; } = "";       // 检查项
    public string State { get; set; } = "wait";  // ok / fail / warn / skip / wait
    public string Detail { get; set; } = "";
    public long Ms { get; set; }
    public string Glyph => State switch { "ok" => "√", "fail" => "×", "warn" => "!", "skip" => "○", _ => "…" };
}

/// <summary>一键网络诊断：只读探测，不改任何配置。全部参数都从系统/配置取，不写死地址。</summary>
public static class Diagnose
{
    private static readonly string[] Probe204 =
    {
        "http://connect.rom.miui.com/generate_204",
        "http://www.gstatic.com/generate_204",
        "http://connectivitycheck.platform.hicloud.com/generate_204",
    };

    private static readonly string[] IpEcho =
    {
        "http://ip.3322.net",
        "https://api.ipify.org",
    };

    public sealed class Result
    {
        public List<DiagItem> Items { get; } = new();
        public string Verdict { get; set; } = "";
        /// <summary>可能原因 + 怎么解决（多行文本）</summary>
        public string Advice { get; set; } = "";
        public string Egress { get; set; } = "";
    }

    public static async Task<Result> RunAsync(RemoteConfig? rc, Action<DiagItem> onItem, CancellationToken ct)
    {
        var res = new Result();
        void Add(DiagItem it) { res.Items.Add(it); onItem(it); }

        // ---------- 1 本机网卡 ----------
        var sw = Stopwatch.StartNew();
        var (ip, gw, nicName) = LocalIPv4AndGateway();
        Add(new DiagItem
        {
            Layer = "本机", Name = "网卡与地址",
            State = string.IsNullOrEmpty(ip) ? "fail" : "ok",
            Detail = string.IsNullOrEmpty(ip)
                ? "没有拿到 IPv4 地址（网线/WiFi 断了？）"
                : $"{nicName}  {ip}" + (string.IsNullOrEmpty(gw) ? "  · 没有默认网关" : $"  · 网关 {gw}"),
            Ms = sw.ElapsedMilliseconds,
        });
        if (string.IsNullOrEmpty(ip))
        {
            res.Verdict = "断在第 1 层：本机没有 IPv4 地址 —— 先看网线 / WiFi 连上没有";
            res.Advice = "可能原因：\n" +
                "① WiFi/网线其实没连上，或连上了但没拿到地址（DHCP 没响应）\n" +
                "② 网卡被禁用，或驱动异常\n" +
                "③ 热点/路由器已经挂了\n\n" +
                "怎么解决：\n" +
                "① 看右下角 WiFi 图标：是不是真的「已连接」\n" +
                "② 去「网卡」页看 WLAN 是否已连接、有没有 IP\n" +
                "③ 换手机热点试一次：能上 → 原来的网络/路由器的问题\n" +
                "④ 还不行：网卡页「禁用」再「启用」，或重启电脑";
            return res;
        }

        // ---------- 2 网关连通 ----------
        if (!string.IsNullOrEmpty(gw))
        {
            sw.Restart();
            var (ok, rtt, loss) = await PingCountAsync(gw, 6, 600, ct);
            Add(new DiagItem
            {
                Layer = "本机", Name = "网关连通",
                State = ok ? "ok" : "fail",
                Detail = ok ? $"{gw} 可达，平均 {rtt} ms，丢包 {loss}%" : $"{gw} ping 不通（丢包 {loss}%）",
                Ms = sw.ElapsedMilliseconds,
            });
            if (!ok)
            {
                res.Verdict = $"断在第 2 层：连网关 {gw} 都不通 —— 问题在局域网这一侧（网线/WiFi/上级设备）";
                res.Advice = "可能原因（按概率）：\n" +
                    "① 软路由无线「假在线」：SSID 还在、DHCP 也给地址，但数据不通（最常见）\n" +
                    "② 热点/路由器死机或过载（被扫描、设备反复重连都会压垮它）\n" +
                    "③ IP 冲突：同网段有别的设备占用了网关地址\n" +
                    "④ WiFi 只是显示已连接，实际关联已经断了\n\n" +
                    "怎么解决：\n" +
                    "① 回面板点「重启 AP」（3~5 秒恢复，需先装看门狗）\n" +
                    "② 看「状态」页的「守护」行，若写着「检测到假在线」，就是原因①\n" +
                    "③ 换连手机热点：能上 → 问题在这台路由器\n" +
                    "④ 都不行：去 minipc 接显示器重启一次";
                return res;
            }
        }
        else
        {
            Add(new DiagItem { Layer = "本机", Name = "网关连通", State = "skip", Detail = "没有默认网关，跳过" });
        }

        // ---------- 3 DNS ----------
        sw.Restart();
        var dnsName = "www.baidu.com";
        IPAddress[]? dnsAddrs = null;
        try
        {
            var t = Dns.GetHostAddressesAsync(dnsName, ct);
            if (await Task.WhenAny(t, Task.Delay(4000, ct)) == t) dnsAddrs = await t;
        }
        catch { }
        var dnsOk = dnsAddrs is { Length: > 0 };
        Add(new DiagItem
        {
            Layer = "解析", Name = "DNS 解析",
            State = dnsOk ? "ok" : "fail",
            Detail = dnsOk ? $"{dnsName} → {dnsAddrs![0]}" : $"{dnsName} 解析失败（可能是 DNS 不可用，或认证掉线被劫持）",
            Ms = sw.ElapsedMilliseconds,
        });

        // ---------- 4 HTTP 204 ----------
        sw.Restart();
        var (code, probe, bodyLen) = await Try204Async(ct);
        var httpOk = code == 204;
        Add(new DiagItem
        {
            Layer = "出口", Name = "HTTP 204 探测",
            State = httpOk ? "ok" : (code > 0 ? "warn" : "fail"),
            Detail = code > 0 ? $"{probe} 返回 {code}" : "所有探测点都连不上（完全出不去）",
            Ms = sw.ElapsedMilliseconds,
        });

        // ---------- 5 出口 IP ----------
        sw.Restart();
        var egress = await TryEgressAsync(ct);
        res.Egress = egress;
        Add(new DiagItem
        {
            Layer = "出口", Name = "公网出口 IP",
            State = string.IsNullOrEmpty(egress) ? "fail" : "ok",
            Detail = string.IsNullOrEmpty(egress) ? "取不到（出口被封或只能访问内网）" : egress,
            Ms = sw.ElapsedMilliseconds,
        });

        // ---------- 6 强制门户 ----------
        if (code > 0 && code != 204)
        {
            Add(new DiagItem
            {
                Layer = "出口", Name = "强制门户 / 认证",
                State = "warn",
                Detail = $"204 探测返回 {code}（不是 204）—— 典型的「需要网页认证」或「被劫持」特征",
            });
        }
        else if (httpOk)
        {
            Add(new DiagItem { Layer = "出口", Name = "强制门户 / 认证", State = "ok", Detail = "返回 204，没有强制门户" });
        }

        // ---------- 7 远端软路由 ----------
        if (rc is { IsConfigured: true })
        {
            sw.Restart();
            string host = "";
            try { host = await Remote.ResolveAsync(rc, null) ?? ""; } catch { }
            if (string.IsNullOrEmpty(host))
            {
                Add(new DiagItem { Layer = "远端", Name = "软路由可达", State = "fail", Detail = "配置的地址都连不上（不在同一网段？没开 SSH？）", Ms = sw.ElapsedMilliseconds });
            }
            else
            {
                Add(new DiagItem { Layer = "远端", Name = "软路由可达", State = "ok", Detail = host + ":22 通", Ms = sw.ElapsedMilliseconds });

                sw.Restart();
                var cmd = "ip route get 1.1.1.1 2>/dev/null | head -1; echo '---'; curl -s -m 6 http://ip.3322.net; echo; echo '---'; uptime -p 2>/dev/null";
                var (_, outText, _) = await Remote.RunAsync("ssh.exe",
                    $"-o ConnectTimeout=6 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o NumberOfPasswordPrompts=0 " +
                    $"-p {rc.Port} {rc.Target} \"{cmd}\"", 15);
                var parts = outText.Split("---");
                var route = parts.Length > 0 ? parts[0].Split('\n').FirstOrDefault(x => x.Trim() != "")?.Trim() ?? "" : "";
                route = System.Text.RegularExpressions.Regex.Replace(route, @"\s+uid \d+", "");   // 去掉 uid 1000 这种噪音
                var rip = parts.Length > 1 ? parts[1].Trim() : "";
                var up = parts.Length > 2 ? parts[2].Trim() : "";
                Add(new DiagItem
                {
                    Layer = "远端", Name = "远端出口",
                    State = string.IsNullOrEmpty(rip) ? "fail" : "ok",
                    Detail = (string.IsNullOrEmpty(rip) ? "远端也出不去" : "远端出口 " + rip) +
                             (string.IsNullOrEmpty(route) ? "" : "  ·  " + route) +
                             (string.IsNullOrEmpty(up) ? "" : "  ·  " + up),
                    Ms = sw.ElapsedMilliseconds,
                });
            }
        }
        else
        {
            Add(new DiagItem { Layer = "远端", Name = "软路由可达", State = "skip", Detail = "「连接」页没配地址，跳过" });
        }

        // ---------- 8 延迟基准 ----------
        sw.Restart();
        var targets = new List<(string host, string label)>();
        if (!string.IsNullOrEmpty(gw)) targets.Add((gw, "网关"));
        targets.Add(("223.5.5.5", "阿里 DNS"));
        var lat = new List<string>();
        foreach (var (h, label) in targets)
        {
            var (ok, rtt, loss) = await PingCountAsync(h, 3, 900, ct);
            lat.Add(ok ? $"{label} {rtt}ms" : $"{label} 不通");
        }
        Add(new DiagItem { Layer = "延迟", Name = "延迟基准", State = "ok", Detail = string.Join("  ·  ", lat), Ms = sw.ElapsedMilliseconds });

        // ---------- 结论 ----------
        if (string.IsNullOrEmpty(res.Verdict))
        {
            var firstFail = res.Items.FirstOrDefault(x => x.State == "fail");
            if (firstFail != null)
                res.Verdict = $"卡在「{firstFail.Layer} · {firstFail.Name}」：{firstFail.Detail}";
            else if (res.Items.Any(x => x.State == "warn"))
                res.Verdict = "能上网，但有异常：" + res.Items.First(x => x.State == "warn").Detail;
            else
                res.Verdict = "一切正常：地址、网关、DNS、出网、延迟都通";

            if (firstFail != null)
                res.Advice = AdviceForName(firstFail.Name);
            else if (res.Items.Any(x => x.State == "warn"))
                res.Advice = "可能原因：\n" +
                    "① 需要网页认证（强制门户）：数据被劫持到认证页\n" +
                    "② 系统代理 / VPN / 加速器在拦流量\n" +
                    "③ 出口被限速或封禁\n\n" +
                    "怎么解决：\n" +
                    "① 打开浏览器随便访问一个网站，完成认证\n" +
                    "② 关掉系统代理、VPN、游戏加速器再测一次\n" +
                    "③ 去「校园网」页看在线状态，掉线就重新登录";
            else
                res.Advice = "没有发现问题。\n\n" +
                    "如果仍然感觉「慢」或「卡」：\n" +
                    "① 「测速」页看带宽是否达标\n" +
                    "② 「游戏延迟」页看抖动和丢包（延迟高但带宽正常 = 线路问题）\n" +
                    "③ 「状态」页的「守护」行看有没有历史「假在线」记录";
        }
        return res;
    }

    /// <summary>按失败项给出「可能原因 + 怎么解决」</summary>
    private static string AdviceForName(string name) => name switch
    {
        "DNS 解析" =>
            "可能原因：\n" +
            "① DNS 服务器不可用（校园网 DNS 挂了 / 被改错）\n" +
            "② 认证掉线，DNS 请求被门户劫持\n" +
            "③ 系统里配了失效的 DNS\n\n" +
            "怎么解决：\n" +
            "① 去「校园网」页看在线状态，掉线就重新登录\n" +
            "② 浏览器随便打开一个网站，看是否跳到认证页\n" +
            "③ 「网址检测」页看各站点解析是否正常，全红就是 DNS 的问题",

        "HTTP 204 探测" =>
            "可能原因：\n" +
            "① 需要网页认证（强制门户把请求劫持了）\n" +
            "② 系统代理 / VPN / 加速器在拦流量\n" +
            "③ 出口被限速或封禁\n\n" +
            "怎么解决：\n" +
            "① 打开浏览器完成认证\n" +
            "② 关掉系统代理、VPN、加速器\n" +
            "③ 「校园网」页重新登录一次",

        "公网出口 IP" =>
            "可能原因：\n" +
            "① 只能访问内网（没认证 / 被限制）\n" +
            "② 出口 IP 查询服务本身不可达\n\n" +
            "怎么解决：\n" +
            "① 先看 204 探测是否正常，不正常先修那一层\n" +
            "② 「网址检测」页测几个外网站点\n" +
            "③ 「校园网」页确认在线",

        "软路由可达" =>
            "可能原因：\n" +
            "① 你换了网络，已经不在软路由的网段里\n" +
            "② 远端没开机，或 SSH 没起来\n" +
            "③ 「连接」页里配的地址不对\n\n" +
            "怎么解决：\n" +
            "① 连回软路由的热点（Gray-Router）\n" +
            "② 「连接」页把地址改成当前网关，点「测试连接」\n" +
            "③ 确认 minipc 是开机状态",

        "远端出口" =>
            "可能原因：\n" +
            "① 软路由自己没认证（bitsrun 掉线）\n" +
            "② 软路由上游网线没插 / 没拿到地址\n\n" +
            "怎么解决：\n" +
            "① 「校园网」页点「查状态」，掉线就「登录」\n" +
            "② 看「状态」页的 WAN 行有没有地址\n" +
            "③ 检查 minipc 的网线",

        _ => "可能原因：这一层没通，通常是上一层连带的（先修上面第一个红色的）\n\n" +
             "怎么解决：① 按上面顺序逐层排查；② 修完点「重新检查」"
    };

    // ---------------- 工具 ----------------

    public static (string ip, string gw, string nic) LocalIPv4AndGateway()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var p = ni.GetIPProperties();
            var ip = p.UnicastAddresses
                .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork &&
                                     !IPAddress.IsLoopback(u.Address) &&
                                     !u.Address.ToString().StartsWith("169.254."))?.Address.ToString();
            if (ip == null) continue;
            var gw = p.GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                     !g.Address.Equals(IPAddress.Any))?.Address.ToString() ?? "";
            return (ip, gw, ni.Name);
        }
        return ("", "", "");
    }

    private static async Task<(bool ok, long rtt, int loss)> PingCountAsync(string host, int count, int timeout, CancellationToken ct)
    {
        long total = 0; int okCount = 0;
        try
        {
            using var ping = new Ping();
            for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                try
                {
                    var r = await ping.SendPingAsync(host, timeout);
                    if (r.Status == IPStatus.Success) { okCount++; total += r.RoundtripTime; }
                }
                catch { }
            }
        }
        catch { }
        var loss = (int)Math.Round((count - okCount) * 100.0 / Math.Max(1, count));
        return (okCount > 0, okCount > 0 ? total / okCount : 0, loss);
    }

    private static async Task<(int code, string probe, long len)> Try204Async(CancellationToken ct)
    {
        // 三个探测点并发，取第一个 204；全失败则返回任意有响应的那个
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        var tasks = Probe204.Select(async url =>
        {
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                return ((int)resp.StatusCode, new Uri(url).Host, (long)body.Length);
            }
            catch { return (0, "", 0L); }
        }).ToArray();
        var results = await Task.WhenAll(tasks);
        foreach (var r in results) if (r.Item1 == 204) return r;
        foreach (var r in results) if (r.Item1 > 0) return r;
        return (0, "", 0);
    }

    private static async Task<string> TryEgressAsync(CancellationToken ct)
    {
        // 两个回显点并发，谁先返回合法 IP 用谁
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        var tasks = IpEcho.Select(async url =>
        {
            try
            {
                var s = (await http.GetStringAsync(url, ct)).Trim();
                return IPAddress.TryParse(s, out _) ? s : "";
            }
            catch { return ""; }
        }).ToArray();
        var arr = await Task.WhenAll(tasks);
        return arr.FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
    }
}
