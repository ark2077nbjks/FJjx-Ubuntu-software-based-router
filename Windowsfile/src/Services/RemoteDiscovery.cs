using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NetPanel.Services;

/// <summary>
/// 「连接」页的地址自动探测。
/// 原则：只问系统已经知道的东西（网关 / ARP 邻居表），再加极少量兜底地址；
/// 绝不扫全网段、不发广播。所有探测都是异步 + 硬超时，界面不会被卡住。
/// </summary>
public static class RemoteDiscovery
{
    public sealed record Hit(string Host, bool Verified, string Detail);

    /// <summary>本机所有 IPv4 地址（用来排除自己的地址、推导 /24 前缀）</summary>
    private static List<(string Ip, string Mask)> LocalV4()
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    var mask = ua.IPv4Mask?.ToString() ?? "255.255.255.0";
                    list.Add((ip, mask));
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>默认网关（Windows 侧），通常就是软路由</summary>
    private static List<string> Gateways()
    {
        var res = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var g in ni.GetIPProperties().GatewayAddresses)
                {
                    if (g?.Address == null) continue;
                    if (g.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = g.Address.ToString();
                    if (s != "0.0.0.0" && !res.Contains(s)) res.Add(s);
                }
            }
        }
        catch { }
        return res;
    }

    /// <summary>ARP 邻居表里的 IP（零发包，系统本来就知道）</summary>
    private static async Task<List<string>> NeighborIPsAsync()
    {
        var res = new List<string>();
        try
        {
            var (rc, outp, _) = await Remote.RunAsync("arp.exe", "-a", 2500);
            if (rc != 0 || string.IsNullOrWhiteSpace(outp)) return res;
            foreach (Match m in Regex.Matches(outp, @"(\d{1,3}(?:\.\d{1,3}){3})\s+(?:[0-9a-fA-F]{2}[-:]){5}[0-9a-fA-F]{2}"))
            {
                var ip = m.Groups[1].Value;
                if (ip.EndsWith(".255") || ip.EndsWith(".0")) continue;
                if (ip.StartsWith("224.") || ip.StartsWith("239.") || ip.StartsWith("255.")) continue;
                if (ip == "127.0.0.1") continue;
                if (!res.Contains(ip)) res.Add(ip);
            }
        }
        catch { }
        return res;
    }

    /// <summary>拼候选列表：上次地址 → 网关 → ARP → (前缀.1 + 少量兜底)</summary>
    public static async Task<List<string>> CandidatesAsync(RemoteConfig cfg)
    {
        var mine = LocalV4();
        var own = mine.Select(x => x.Ip).ToHashSet();
        var res = new List<string>();

        void Add(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return;
            ip = ip.Trim();
            if (ip == "" || own.Contains(ip)) return;
            if (res.Contains(ip)) return;
            if (res.Count >= 12) return;   // 硬上限：最多探 12 个
            res.Add(ip);
        }

        // ① 上次成功 / 用户手填的
        foreach (var h in cfg.HostList()) Add(h);

        // ② 默认网关
        foreach (var g in Gateways()) Add(g);

        // ③ ARP 邻居表
        foreach (var ip in await NeighborIPsAsync()) Add(ip);

        // ④ 同网段的 .1（软路由最常见的地址）
        foreach (var (ip, mask) in mine)
        {
            var parts = ip.Split('.');
            if (parts.Length != 4) continue;
            Add($"{parts[0]}.{parts[1]}.{parts[2]}.1");
        }

        return res;
    }

    /// <summary>
    /// 探测：先看 22 端口通不通（400ms），通的再尝试用密钥跑一条 hostname 验证。
    /// 返回第一个「验证通过」的；没有验证通过的就返回第一个「端口通」的。
    /// </summary>
    public static async Task<Hit?> DiscoverAsync(RemoteConfig cfg, int port, Action<string>? log = null,
                                                 CancellationToken ct = default)
    {
        var cands = await CandidatesAsync(cfg);
        if (cands.Count == 0) { log?.Invoke("× 没找到可探测的地址"); return null; }
        log?.Invoke($"候选 {cands.Count} 个：{string.Join("、", cands)}");

        using var gate = new SemaphoreSlim(4);           // 并发 4，绝不打满
        var open = new List<string>();

        var tasks = cands.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await TcpOpenAsync(ip, port, 400, ct)) lock (open) open.Add(ip);
            }
            catch { }
            finally { gate.Release(); }
        }).ToArray();

        try { await Task.WhenAll(tasks); } catch { }
        ct.ThrowIfCancellationRequested();

        if (open.Count == 0) { log?.Invoke("× 22 端口都不通（机器没开？不在同一网络？）"); return null; }
        log?.Invoke($"端口 22 通的：{string.Join("、", open)}");

        // 密码模式没法在后台验证，直接把第一个端口通的当结果
        if (cfg.Auth == "password")
            return new Hit(open[0], false, "端口 22 通（密码模式没法自动验证登录）");

        foreach (var ip in open)
        {
            if (ct.IsCancellationRequested) break;
            var (rc, outp, _) = await Remote.RunAsync("ssh.exe",
                $"-o ConnectTimeout=3 -o StrictHostKeyChecking=accept-new -o BatchMode=yes " +
                $"-o NumberOfPasswordPrompts=0 -p {port} {cfg.User}@{ip} \"hostname\"", 5000);
            if (rc == 0 && !string.IsNullOrWhiteSpace(outp))
                return new Hit(ip, true, "已用密钥验证登录：" + outp.Trim().Split('\n')[0].Trim());
        }

        return new Hit(open[0], false, "端口 22 通，但密钥没验过（可能要密码，请点「打开终端」试）");
    }

    private static async Task<bool> TcpOpenAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync(host, port, ct).AsTask();       // ValueTask → Task
            var done = await Task.WhenAny(t, Task.Delay(timeoutMs, ct));
            return done == t && c.Connected;
        }
        catch { return false; }
    }
}
