using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace NetPanel.Services;

/// <summary>
/// 代理 / 加速器检测（实时）。
/// 本地层：系统代理、WinHTTP、环境变量、虚拟网卡、代理进程、本地代理端口 —— 零联网，可每 10 秒刷。
/// 联网层：三个源交叉查出口 IP + 归属 + 机房/代理标记，再量国内外延迟 —— 每分钟一次就够。
/// 全程只读，不改设置；不需要管理员。
/// </summary>
public sealed class ProxyLocal
{
    public bool Likely;
    public List<string> Findings = new();
    public string Text = "";
}

public sealed class ProxyReport
{
    public bool ProxyLikely;
    public string Verdict = "";
    public string LocalText = "";
    public string RemoteText = "";
    public string LatencyText = "";

    public string AllText => string.Join("\n\n",
        new[] { LocalText, RemoteText, LatencyText }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public static class ProxyCheck
{
    private static readonly int[] LocalPorts =
        { 7890, 7891, 7897, 1080, 1087, 10808, 10809, 2080, 20171, 8889 };

    private static readonly string[] VirtKeywords =
        { "tap", "tun", "wireguard", "wintun", "clash", "zerotier", "openvpn", "sstap", "netch", "v2ray", "sing-box", "proxy" };

    private static readonly string[] ProxyProcs =
    {
        "clash", "mihomo", "verge", "v2ray", "xray", "sing-box", "shadowsocks", "ss-local", "trojan",
        "netch", "sstap", "hiddify", "nekoray", "nekobox", "proxifier", "wireguard", "openvpn",
        "tailscale", "zerotier", "uu.exe", "uugame", "neteaseuu", "xunyou", "qiyou", "accel", "booster",
    };

    // ---------------- 本地层（零联网，可高频） ----------------

    public static async Task<ProxyLocal> LocalAsync(CancellationToken ct = default)
    {
        var res = new ProxyLocal();
        var sb = new StringBuilder();
        var hits = new List<string>();

        sb.AppendLine("【系统代理 · WinINET（浏览器/系统走这层）】");
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var enable = k?.GetValue("ProxyEnable")?.ToString() ?? "0";
            var server = k?.GetValue("ProxyServer")?.ToString() ?? "";
            var pac = k?.GetValue("AutoConfigURL")?.ToString() ?? "";
            if (enable == "1" && server != "") { sb.AppendLine($"  √ 已开启：{server}"); hits.Add("系统代理 " + server); }
            else sb.AppendLine("  · 未开启");
            if (!string.IsNullOrWhiteSpace(pac)) { sb.AppendLine($"  √ PAC 脚本：{pac}"); hits.Add("PAC 脚本"); }
        }
        catch (Exception ex) { sb.AppendLine("  ? 读不到：" + ex.Message); }

        sb.AppendLine();
        sb.AppendLine("【WinHTTP 代理】");
        try
        {
            var (rc, outp, _) = await Remote.RunAsync("netsh.exe", "winhttp show proxy", 4000);
            var lines = outp.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x != "").ToList();
            var line = lines.FirstOrDefault(x => x.Contains("直接访问") || x.Contains("Direct access"))
                    ?? lines.FirstOrDefault(x => System.Text.RegularExpressions.Regex.IsMatch(x, @"[\w.\-]+:\d{2,5}"))
                    ?? lines.FirstOrDefault(x => x.Contains("Proxy Server") || x.Contains("代理服务器"));
            if (rc == 0 && !string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine("  " + line);
                var direct = line.Contains("直接访问") || line.Contains("Direct access");
                var hasHostPort = System.Text.RegularExpressions.Regex.IsMatch(line, @"[\w.\-]+:\d{2,5}");
                if (!direct && hasHostPort) hits.Add("WinHTTP 代理 " + line);
            }
            else sb.AppendLine("  · 未设置");
        }
        catch { sb.AppendLine("  · 未设置"); }

        sb.AppendLine();
        sb.AppendLine("【环境变量】");
        var envHit = new List<string>();
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" })
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v)) envHit.Add($"{name}={v}");
        }
        sb.AppendLine(envHit.Count > 0 ? "  √ " + string.Join("  ", envHit) : "  · 无");
        if (envHit.Count > 0) hits.Add("环境变量代理");

        sb.AppendLine();
        sb.AppendLine("【虚拟网卡（TUN/TAP 类）】");
        var virt = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var txt = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (VirtKeywords.Any(x => txt.Contains(x))) virt.Add($"{ni.Name}（{ni.Description}）");
            }
        }
        catch { }
        sb.AppendLine(virt.Count > 0 ? string.Join("\n", virt.Select(v => "  √ " + v)) : "  · 没看到");
        if (virt.Count > 0) hits.Add("虚拟网卡 " + virt.Count + " 张");

        sb.AppendLine();
        sb.AppendLine("【代理 / 加速器进程】");
        var procs = await RunningProxyProcessesAsync();
        sb.AppendLine(procs.Count > 0 ? string.Join("\n", procs.Select(x => "  √ " + x)) : "  · 没看到");
        if (procs.Count > 0) hits.Add("进程 " + string.Join("/", procs));

        sb.AppendLine();
        sb.AppendLine("【本地代理端口（只探 127.0.0.1）】");
        var open = new List<int>();
        await Task.WhenAll(LocalPorts.Select(async p =>
        {
            if (await PortOpenAsync("127.0.0.1", p, 150, ct)) lock (open) open.Add(p);
        }));
        if (open.Count > 0)
        {
            sb.AppendLine("  √ 在听：" + string.Join("、", open.OrderBy(x => x)));
            hits.Add("端口 " + string.Join("/", open.OrderBy(x => x)));
        }
        else sb.AppendLine("  · 都没有在听");

        res.Likely = hits.Count > 0;
        res.Findings = hits;
        res.Text = sb.ToString().TrimEnd();
        return res;
    }

    // ---------------- 联网层（每分钟一次） ----------------

    public static async Task<(string Remote, string Latency)> RemoteAsync(Action<string>? log = null,
                                                                        CancellationToken ct = default)
    {
        var ips = new Dictionary<string, string>();
        string geo = "", isp = "", asn = "", flags = "", ipipText = "";

        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 NetPanel");

        async Task<string?> Get(string url)
        {
            try
            {
                var r = await http.GetAsync(url, ct);
                return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync(ct) : null;
            }
            catch { return null; }
        }

        var t1 = Get("http://ip-api.com/json/?lang=zh-CN&fields=status,country,regionName,city,isp,as,query,proxy,hosting,mobile");
        var t2 = Get("https://api.ip.sb/geoip");
        var t3 = Get("https://myip.ipip.net");
        await Task.WhenAll(t1, t2, t3);

        try
        {
            if (t1.Result != null)
            {
                using var d = JsonDocument.Parse(t1.Result);
                var r = d.RootElement;
                if (r.TryGetProperty("status", out var st) && st.GetString() == "success")
                {
                    var ip = r.GetProperty("query").GetString() ?? "";
                    if (ip != "") ips[ip] = "ip-api";
                    geo = string.Join(" ", new[] { Str(r, "country"), Str(r, "regionName"), Str(r, "city") }.Where(s => s != ""));
                    isp = Str(r, "isp");
                    asn = Str(r, "as");
                    var marks = new List<string>();
                    if (r.TryGetProperty("hosting", out var h) && h.ValueKind == JsonValueKind.True) marks.Add("机房 IP");
                    if (r.TryGetProperty("proxy", out var p) && p.ValueKind == JsonValueKind.True) marks.Add("被标为代理");
                    if (r.TryGetProperty("mobile", out var m) && m.ValueKind == JsonValueKind.True) marks.Add("移动网络");
                    flags = marks.Count > 0 ? string.Join(" · ", marks) : "无";
                }
            }
        }
        catch { }

        try
        {
            if (t2.Result != null)
            {
                using var d = JsonDocument.Parse(t2.Result);
                var r = d.RootElement;
                var ip = Str(r, "ip");
                if (ip != "") ips[ip] = "ip.sb";
                if (geo == "") geo = string.Join(" ", new[] { Str(r, "country"), Str(r, "region"), Str(r, "city") }.Where(s => s != ""));
                if (asn == "" && r.TryGetProperty("asn", out var a)) asn = "AS" + a.ToString();
            }
        }
        catch { }

        try
        {
            if (t3.Result != null)
            {
                var txt = t3.Result.Trim();
                var mm = System.Text.RegularExpressions.Regex.Match(txt, @"(\d{1,3}(?:\.\d{1,3}){3})");
                if (mm.Success) ips[mm.Groups[1].Value] = "ipip";
                if (geo == "") geo = txt;
                ipipText = txt;
            }
        }
        catch { }

        var sb = new StringBuilder();
        sb.AppendLine("【出口 IP（三个源交叉）】");
        if (ips.Count == 0) sb.AppendLine("  × 三个源都没查通（没网？被墙？）");
        else
        {
            foreach (var kv in ips) sb.AppendLine($"  {kv.Key}   ← {kv.Value}");
            sb.AppendLine(ips.Count > 1
                ? "  ! 多源 IP 不一致 —— 常见于运营商 NAT 池 / 多出口；若在使用代理，也可能是分流规则导致"
                : "  · 三源一致");
        }
        sb.AppendLine();
        sb.AppendLine("【归属与身份】");
        sb.AppendLine("  地区    " + (geo == "" ? "—" : geo));
        sb.AppendLine("  运营商  " + (isp == "" ? "—" : isp));
        sb.AppendLine("  ASN     " + (asn == "" ? "—" : asn));
        sb.AppendLine("  标记    " + (flags == "" ? "—" : flags));
        if (ipipText != "") sb.AppendLine("  ipip    " + ipipText);

        var lat = new StringBuilder();
        lat.AppendLine("【延迟基准（HTTP 头，各 1 次）】");
        async Task<(long Ms, string Note)> Head(string name, string url)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();
                return (sw.ElapsedMilliseconds, $"{name} {(int)r.StatusCode}");
            }
            catch (Exception ex) { sw.Stop(); return (-1, $"{name} 失败({ex.GetType().Name})"); }
        }
        var cn = await Head("百度", "https://www.baidu.com/");
        var os = await Head("Cloudflare", "https://www.cloudflare.com/cdn-cgi/trace");
        lat.AppendLine($"  国内  {cn.Note}   {(cn.Ms >= 0 ? cn.Ms + " ms" : "—")}");
        lat.AppendLine($"  境外  {os.Note}   {(os.Ms >= 0 ? os.Ms + " ms" : "—")}");
        if (cn.Ms >= 0 && os.Ms > 0)
        {
            string guess;
            if (os.Ms > cn.Ms * 3 && os.Ms > 300) guess = "境外明显更慢：像直连（或代理线路差）";
            else if (os.Ms < cn.Ms * 0.7) guess = "境外比国内快：可能走了加速器/代理";
            else guess = "境内外接近：看不出明显的代理特征";
            lat.AppendLine("  判断  " + guess);
        }

        return (sb.ToString().TrimEnd(), lat.ToString().TrimEnd());
    }

    /// <summary>按钮一次性跑完整检测</summary>
    public static async Task<ProxyReport> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        var rep = new ProxyReport();
        var local = await LocalAsync(ct);
        rep.LocalText = local.Text;
        rep.ProxyLikely = local.Likely;

        log?.Invoke("查出口 IP（三个源交叉）…");
        var (remote, lat) = await RemoteAsync(log, ct);
        rep.RemoteText = remote;
        rep.LatencyText = lat;

        var ipLine = remote.Split('\n').FirstOrDefault(x => x.Contains("← "))?.Trim() ?? "出口查不到";
        var head = local.Likely ? "! 检测到代理/加速器相关配置" : "✓ 没发现本地代理";
        var mid = local.Findings.Count > 0 ? "：" + string.Join("、", local.Findings) : "";
        rep.Verdict = $"{head}{mid}\n{ipLine}";
        return rep;
    }

    private static async Task<List<string>> RunningProxyProcessesAsync()
    {
        var hit = new List<string>();
        try
        {
            var (rc, outp, _) = await Remote.RunAsync("tasklist.exe", "/fo csv /nh", 4000);
            if (rc != 0 || string.IsNullOrWhiteSpace(outp)) return hit;
            foreach (var line in outp.Replace("\r", "").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var name = line.Split(',')[0].Trim('"');
                if (name == "") continue;
                var low = name.ToLowerInvariant();
                if (!ProxyProcs.Any(k => low.Contains(k))) continue;
                hit.Add(name);
            }
        }
        catch { }
        return hit.Distinct().Take(12).ToList();
    }

    private static string Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static async Task<bool> PortOpenAsync(string host, int port, int ms, CancellationToken ct)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync(host, port, ct).AsTask();
            var done = await Task.WhenAny(t, Task.Delay(ms, ct));
            return done == t && c.Connected;
        }
        catch { return false; }
    }
}
