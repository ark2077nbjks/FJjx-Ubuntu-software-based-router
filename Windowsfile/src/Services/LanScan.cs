using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace NetPanel.Services;

/// <summary>局域网里发现的设备</summary>
public sealed class LanDevice
{
    public string Ip { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";   // 本机 / 软路由
    public string State { get; set; } = "";    // 在线 / 不在线
    public string Rtt { get; set; } = "";      // 12 ms / —
}

/// <summary>
/// 扫本网段有哪些设备。
/// 两条腿：① 本机 ping 扫一遍 + 读 ARP 表；② 远端软路由的邻居表 + DHCP 租约（配了才有）。
/// </summary>
public static class LanScan
{
    // 常见厂商前缀（够认人就行，认不出显示 —）
    private static readonly Dictionary<string, string> Oui = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00:1A:1E"] = "Aruba",        ["00:0C:29"] = "VMware",
        ["00:50:56"] = "VMware",       ["08:00:27"] = "VirtualBox",
        ["00:1B:21"] = "Intel",        ["00:1E:67"] = "Intel",
        ["3C:97:0E"] = "Intel",        ["8C:16:45"] = "Intel",
        ["A4:BB:6D"] = "Intel",        ["E4:A4:71"] = "Intel",
        ["00:0E:C6"] = "ASIX(USB网卡)", ["00:1C:42"] = "Parallels",
        ["B8:27:EB"] = "树莓派",        ["DC:A6:32"] = "树莓派",
        ["E4:5F:01"] = "树莓派",        ["2C:CF:67"] = "树莓派",
        ["00:11:32"] = "Synology",     ["00:1A:8C"] = "Synology",
        ["24:5E:BE"] = "QNAP",         ["00:08:9B"] = "ICP(工控)",
        ["00:1B:44"] = "SanDisk",      ["AC:84:C6"] = "TP-Link",
        ["50:C7:BF"] = "TP-Link",      ["B0:BE:76"] = "TP-Link",
        ["C0:06:C3"] = "TP-Link",      ["F4:F2:6D"] = "TP-Link",
        ["00:1D:0F"] = "TP-Link",      ["14:CC:20"] = "TP-Link",
        ["34:96:72"] = "TP-Link",      ["9C:A2:F4"] = "TP-Link",
        ["00:0C:43"] = "MediaTek",     ["00:0C:E7"] = "MediaTek",
        ["1C:BF:CE"] = "MediaTek",     ["44:00:10"] = "MediaTek",
        ["00:E0:4C"] = "Realtek",      ["52:54:00"] = "QEMU/KVM",
        ["00:26:5E"] = "Hon Hai",      ["00:1E:EC"] = "Hon Hai",
        ["3C:CD:5D"] = "Huawei",       ["48:DB:50"] = "Huawei",
        ["70:8B:CD"] = "Huawei",       ["8C:34:FD"] = "Huawei",
        ["A0:1B:29"] = "Huawei",       ["E0:24:7F"] = "Huawei",
        ["00:E0:FC"] = "Huawei",       ["CC:A2:23"] = "Huawei",
        ["00:9A:CD"] = "Huawei",       ["D8:9B:3B"] = "Huawei",
        ["F4:9F:F3"] = "Huawei",       ["24:69:8E"] = "Xiaomi",
        ["28:6C:07"] = "Xiaomi",       ["34:CE:00"] = "Xiaomi",
        ["38:A4:ED"] = "Xiaomi",       ["50:8F:4C"] = "Xiaomi",
        ["64:09:80"] = "Xiaomi",       ["78:02:F8"] = "Xiaomi",
        ["8C:BE:BE"] = "Xiaomi",       ["9C:99:A0"] = "Xiaomi",
        ["AC:C1:EE"] = "Xiaomi",       ["F0:B4:29"] = "Xiaomi",
        ["FC:64:BA"] = "Xiaomi",       ["00:25:9E"] = "Oppo",
        ["2C:5B:B8"] = "Oppo",         ["94:65:2D"] = "OnePlus",
        ["C0:EE:FB"] = "OnePlus",      ["00:16:3E"] = "阿里云",
        ["A8:5E:45"] = "vivo",         ["3C:BD:3E"] = "vivo",
        ["00:1E:C2"] = "Apple",        ["00:23:DF"] = "Apple",
        ["04:0C:CE"] = "Apple",        ["0C:4D:E9"] = "Apple",
        ["10:40:F3"] = "Apple",        ["28:37:37"] = "Apple",
        ["34:A3:95"] = "Apple",        ["40:6C:8F"] = "Apple",
        ["58:B0:35"] = "Apple",        ["6C:40:08"] = "Apple",
        ["7C:6D:62"] = "Apple",        ["84:38:35"] = "Apple",
        ["9C:FC:01"] = "Apple",        ["A4:83:E7"] = "Apple",
        ["AC:BC:32"] = "Apple",        ["C8:2A:14"] = "Apple",
        ["D0:81:7A"] = "Apple",        ["F0:18:98"] = "Apple",
        ["2C:54:91"] = "Microsoft",    ["7C:1E:52"] = "Microsoft",
        ["00:1C:10"] = "Cisco",        ["00:26:0B"] = "Cisco",
        ["00:1A:A1"] = "Cisco",        ["00:0F:34"] = "Cisco",
        ["00:11:92"] = "Cisco",        ["00:18:8B"] = "Cisco",
        ["00:26:AB"] = "Samsung",      ["34:23:BA"] = "Samsung",
        ["5C:0A:5B"] = "Samsung",      ["78:1F:DB"] = "Samsung",
        ["8C:71:F8"] = "Samsung",      ["C0:BD:D1"] = "Samsung",
        ["00:0D:93"] = "Apple",        ["00:1F:F3"] = "Apple",
        ["B4:0F:3B"] = "Tenda",        ["C8:3A:35"] = "Tenda",
        ["00:E0:4C:68"] = "Realtek",   ["1C:69:7A"] = "Elite",
        ["24:4B:FE"] = "ASUSTek",      ["04:D4:C4"] = "ASUSTek",
        ["2C:56:DC"] = "ASUSTek",      ["38:D5:47"] = "ASUSTek",
        ["00:E0:18"] = "ASUSTek",      ["AC:22:0B"] = "ASUSTek",
        ["B0:6E:BF"] = "ASUSTek",      ["F8:32:E4"] = "ASUSTek",
        ["00:1B:FC"] = "ASUSTek",      ["00:24:8C"] = "ASUSTek",
        ["D8:5D:4C"] = "TP-Link",      ["1C:61:B4"] = "TP-Link",
        ["00:0D:B9"] = "PC Engines",   ["00:90:0B"] = "Nokia",
        ["00:0C:42"] = "Routerboard",  ["4C:5E:0C"] = "Routerboard",
        ["64:D1:54"] = "Routerboard",  ["74:4D:28"] = "Routerboard",
        ["00:1C:DF"] = "Belkin",       ["94:10:3E"] = "Belkin",
    };

    public static string Vendor(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac) || mac.Length < 8) return "—";
        var key = mac[..8].Replace('-', ':').ToUpperInvariant();
        if (Oui.TryGetValue(key, out var v)) return v;
        // 本地管理地址（随机 MAC，手机常见）
        if (mac.Length >= 2)
        {
            var b = Convert.ToInt32(mac[..2].Replace("-", "").Replace(":", ""), 16);
            if ((b & 0x02) != 0) return "随机 MAC";
        }
        return "—";
    }

    /// <summary>猜本机所在网段，返回 192.168.50.0/24 这种</summary>
    public static async Task<string> GuessLocalCidrAsync()
    {
        var (code, output) = await Proc.RunAsync("powershell.exe",
            "-NoProfile -Command \"Get-NetIPAddress -AddressFamily IPv4 | " +
            "Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne '127.0.0.1' } | " +
            "Select-Object -First 1 -ExpandProperty IPAddress\"", 12);

        var ip = output.Split('\n').Select(x => x.Trim())
                       .FirstOrDefault(x => Regex.IsMatch(x, @"^\d+\.\d+\.\d+\.\d+$"));
        if (ip == null) return "";

        var parts = ip.Split('.');
        return $"{parts[0]}.{parts[1]}.{parts[2]}.0/24";
    }

    /// <summary>扫网段。progress 用来往界面上报进度。</summary>
    public static async Task<List<LanDevice>> ScanAsync(
        string cidr, RemoteConfig? rc, Action<string> progress, CancellationToken ct)
    {
        var devices = new Dictionary<string, LanDevice>(StringComparer.Ordinal);

        // ---------- ① 本机 ping 扫一遍（ARP 表才有东西） ----------
        var baseIp = cidr.Split('/')[0];
        var head = baseIp[..(baseIp.LastIndexOf('.') + 1)];      // "192.168.50."
        progress($"正在扫 {head}1 ~ {head}254 …");

        var selfIps = new HashSet<string>(StringComparer.Ordinal);
        var (_, ipOut) = await Proc.RunAsync("powershell.exe",
            "-NoProfile -Command \"Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress\"", 12);
        foreach (var t in ipOut.Split('\n'))
        {
            var v = t.Trim();
            if (Regex.IsMatch(v, @"^\d+\.\d+\.\d+\.\d+$")) selfIps.Add(v);
        }

        var alive = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        using var gate = new SemaphoreSlim(48);
        var tasks = new List<Task>();
        for (int i = 1; i <= 254; i++)
        {
            if (ct.IsCancellationRequested) break;
            var ip = head + i;
            await gate.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var ping = new Ping();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var reply = await ping.SendPingAsync(ip, 600);
                    if (reply.Status == IPStatus.Success)
                        alive[ip] = sw.ElapsedMilliseconds;
                }
                catch { }
                finally { gate.Release(); }
            }, ct));
        }
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }

        progress($"ping 通 {alive.Count} 台，正在读本机 ARP 表 …");

        // ---------- ② 读本机 ARP 表 ----------
        var (_, arp) = await Proc.RunAsync("arp.exe", "-a", 15);
        foreach (Match m in Regex.Matches(arp, @"(\d+\.\d+\.\d+\.\d+)\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})"))
        {
            var ip = m.Groups[1].Value;
            if (!ip.StartsWith(head)) continue;
            var mac = m.Groups[2].Value.Replace('-', ':').ToUpperInvariant();
            devices[ip] = new LanDevice
            {
                Ip = ip, Mac = mac, Vendor = Vendor(mac), Source = "本机",
            };
        }

        // ---------- ③ 远端软路由的邻居表 + 租约（配了才查） ----------
        if (rc is { IsConfigured: true })
        {
            progress("正在问软路由要设备表 …");
            var cmd = "ip -4 neigh show 2>/dev/null; echo '---LEASES---'; " +
                      "cat /var/lib/misc/dnsmasq.leases 2>/dev/null; " +
                      "cat /var/lib/NetworkManager/*.leases 2>/dev/null; " +
                      "cat /tmp/dhcp.leases 2>/dev/null; true";
            var (rc2, rem, err2) = await Remote.RunAsync("ssh.exe",
                $"-o ConnectTimeout=4 -o BatchMode=yes -o StrictHostKeyChecking=accept-new " +
                $"-o NumberOfPasswordPrompts=0 -p {rc.Port} {rc.Target} \"{cmd}\"", 20);

            var firstErr = err2.Split('\n').FirstOrDefault(x => x.Trim() != "") ?? "";
            AppPaths.Log($"[设备] 软路由表 rc={rc2} 长度={rem.Length} 错误={firstErr.Trim()}");

            if (rem.Length > 0 && (rc2 == 0 || rem.Contains("lladdr")))
            {
                var names = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var line in rem.Split('\n'))
                {
                    var t = line.Trim();

                    // ip neigh: 192.168.50.173 dev wlp2s0 lladdr 3c:cd:5d:xx:xx:xx REACHABLE
                    var mn = Regex.Match(t, @"^(\d+\.\d+\.\d+\.\d+)\s+dev\s+\S+\s+lladdr\s+([0-9a-fA-F:]{17})");
                    if (mn.Success)
                    {
                        var ip = mn.Groups[1].Value;
                        if (!ip.StartsWith(head)) continue;          // 只认扫描网段，跳过 docker0 / 上游网关
                        var mac = mn.Groups[2].Value.ToUpperInvariant();
                        if (!devices.ContainsKey(ip))
                            devices[ip] = new LanDevice { Ip = ip, Mac = mac, Vendor = Vendor(mac), Source = "软路由" };
                        else
                        {
                            devices[ip].Source = "本机+软路由";
                            if (string.IsNullOrEmpty(devices[ip].Mac)) devices[ip].Mac = mac;
                            if (devices[ip].Vendor == "—") devices[ip].Vendor = Vendor(mac);
                        }
                        continue;
                    }

                    // dnsmasq leases: 1730000000 3c:cd:5d:xx:xx:xx 192.168.50.173 my-phone *
                    var ml = Regex.Match(t, @"^\d{9,}\s+([0-9a-fA-F:]{17})\s+(\d+\.\d+\.\d+\.\d+)\s+(\S+)");
                    if (ml.Success)
                    {
                        var mac = ml.Groups[1].Value.ToUpperInvariant();
                        var ip = ml.Groups[2].Value;
                        var host = ml.Groups[3].Value.TrimEnd('*').Trim();
                        if (host != "" && host != "*") names[ip] = host;
                        continue;
                    }

                    // NetworkManager 租约（写法略有不同：expiry mac ip hostname clientid）
                    var mnm = Regex.Match(t, @"^\d{9,}\s+([0-9a-fA-F:]{17})\s+(\d+\.\d+\.\d+\.\d+)\s+(\S+)");
                    if (mnm.Success)
                    {
                        var host = mnm.Groups[3].Value.TrimEnd('*').Trim();
                        if (host != "" && host != "*") names[mnm.Groups[2].Value] = host;
                    }
                }

                foreach (var kv in names)
                    if (devices.TryGetValue(kv.Key, out var d)) d.Name = kv.Value;
            }
        }

        // ---------- ④ 组装 ----------
        foreach (var kv in devices)
        {
            var d = kv.Value;
            if (selfIps.Contains(d.Ip)) d.Name = d.Name == "" ? "这台电脑" : d.Name + "（这台电脑）";

            if (alive.TryGetValue(d.Ip, out var ms))
            {
                d.State = "在线";
                d.Rtt = ms + " ms";
            }
            else if (selfIps.Contains(d.Ip))
            {
                d.State = "在线";
                d.Rtt = "本机";
            }
            else if (d.Source.Contains("软路由"))
            {
                d.State = "在线";
                d.Rtt = "—";
            }
            else
            {
                d.State = "不在线";
                d.Rtt = "—";
            }
        }

        var list = devices.Values
            .OrderBy(d => IPAddress.TryParse(d.Ip, out var a) ? 0 : 1)
            .ThenBy(d => IPAddress.TryParse(d.Ip, out var a) ? a.GetAddressBytes()[3] : 0)
            .ToList();

        progress($"扫完：{list.Count} 台设备，{list.Count(x => x.State == "在线")} 台在线");
        return list;
    }
}
