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
    public string Source { get; set; } = "";   // 本机 / 软路由（内部逻辑用，不再显示）
    public string Conn { get; set; } = "";     // 显示用：本机 / 无线 2.4G -49dBm / 有线 / 同网段
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

    /// <summary>猜本机所在网段，返回 192.168.1.0/24 这种（纯 .NET，不起 PowerShell，快）</summary>
    public static Task<string> GuessLocalCidrAsync()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var p = ni.GetIPProperties();
            var hasGw = p.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                                    && !g.Address.Equals(IPAddress.Any));
            var u = p.UnicastAddresses.FirstOrDefault(a =>
                a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(a.Address) &&
                !a.Address.ToString().StartsWith("169.254."));
            if (u == null) continue;
            var mask = u.IPv4Mask;
            if (mask == null) continue;
            var ipb = u.Address.GetAddressBytes();
            var mb = mask.GetAddressBytes();
            var net = new byte[4];
            int ones = 0;
            for (int i = 0; i < 4; i++) { net[i] = (byte)(ipb[i] & mb[i]); ones += System.Numerics.BitOperations.PopCount(mb[i]); }
            // 优先带默认网关的那张（就是真正上网用的）
            var cidr = $"{net[0]}.{net[1]}.{net[2]}.{net[3]}/{ones}";
            if (hasGw) return Task.FromResult(cidr);
        }
        // 兜底：找一张 up 的
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            var u = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(a =>
                a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254."));
            if (u == null) continue;
            var b = u.Address.GetAddressBytes();
            return Task.FromResult($"{b[0]}.{b[1]}.{b[2]}.0/24");
        }
        return Task.FromResult("");
    }

    /// <summary>扫网段。progress 用来往界面上报进度。</summary>
    public static async Task<List<LanDevice>> ScanAsync(
        string cidr, RemoteConfig? rc, bool deep, Action<string> progress, CancellationToken ct)
    {
        var devices = new Dictionary<string, LanDevice>(StringComparer.Ordinal);

        // ---------- ① 只有「深度扫描」才发包；默认一个包都不发 ----------
        var baseIp = cidr.Split('/')[0];
        var head = baseIp[..(baseIp.LastIndexOf('.') + 1)];      // "192.168.50."

        var selfIps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var a in ni.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    selfIps.Add(a.Address.ToString());
        }

        var alive = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();

        if (deep)
        {
            // 限速：16 路并发、每个包错开 30ms、单包超时 1 秒 —— 别再把 2.4G 空口打满
            progress($"深度扫描 {head}1 ~ {head}254（16 并发，限速中）…");
            using var gate = new SemaphoreSlim(16);
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
                        var reply = await ping.SendPingAsync(ip, 1000);
                        if (reply.Status == IPStatus.Success) alive[ip] = sw.ElapsedMilliseconds;
                    }
                    catch { }
                    finally { gate.Release(); }
                }, ct));
                try { await Task.Delay(30, ct); } catch (OperationCanceledException) { break; }
            }
            try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
            progress($"ping 通 {alive.Count} 台，正在读 ARP 表 …");
        }
        else
        {
            progress("快速模式：先读邻居表，再补轻量查询 …");
        }

        // ---------- ② 读本机 ARP 表（不发包，秒出）----------
        var (_, arp) = await Proc.RunAsync("powershell.exe",
            "-NoProfile -Command \"Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue | " +
            "Where-Object {$_.State -ne 'Unreachable'} | ForEach-Object { $_.IPAddress + ' ' + $_.LinkLayerAddress + ' ' + $_.State }\"", 15);
        foreach (var line in arp.Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^(\d+\.\d+\.\d+\.\d+)\s+([0-9a-fA-F]{2}(?:[:-][0-9a-fA-F]{2}){5})\s+(\w+)$");
            if (!m.Success) continue;
            var ip = m.Groups[1].Value;
            if (!ip.StartsWith(head)) continue;
            if (ip.EndsWith(".255") || ip.EndsWith(".0")) continue;      // 广播 / 网络地址不是设备
            var mac = m.Groups[2].Value.Replace('-', ':').ToUpperInvariant();
            var st = m.Groups[3].Value;
            devices[ip] = new LanDevice
            {
                Ip = ip, Mac = mac, Vendor = Vendor(mac), Source = "本机",
                // 邻居表状态：Reachable/Permanent = 在线；Stale = 见过但可能走了
                State = st is "Reachable" or "Permanent" ? "在线" : "可能在线",
            };
        }

        // ---------- ③ 远端软路由的邻居表 + 租约（配了才查） ----------
        if (rc is { IsConfigured: true })
        {
            progress("正在问软路由要设备表 …");
            // 一条 ssh 拿全：邻居表 + 无线接口 + 每个接口的站点信息 + 租约
            // 每条 iw 都套 timeout 3：驱动卡住时最多等 3 秒就放弃，不会把整个扫描拖死
            var cmd = "ip -4 neigh show 2>/dev/null; " +
                      "echo '---IW---'; timeout 3 iw dev 2>/dev/null; " +
                      "echo '---STATION---'; for d in $(timeout 3 iw dev 2>/dev/null | awk '/Interface/{print $2}'); " +
                      "do timeout 3 iw dev $d station dump 2>/dev/null; done; " +
                      "echo '---ROUTE---'; ip route get 1.1.1.1 2>/dev/null | head -1; " +
                      "echo '---LEASES---'; " +
                      "cat /var/lib/misc/dnsmasq.leases 2>/dev/null; " +
                      "cat /var/lib/NetworkManager/*.leases 2>/dev/null; true";
            var (rc2, rem, err2) = await Remote.RunAsync("ssh.exe",
                $"-o ConnectTimeout=4 -o BatchMode=yes -o StrictHostKeyChecking=accept-new " +
                $"-o NumberOfPasswordPrompts=0 -p {rc.Port} {rc.Target} \"{cmd}\"", 20);

            var firstErr = err2.Split('\n').FirstOrDefault(x => x.Trim() != "") ?? "";
            AppPaths.Log($"[设备] 软路由表 rc={rc2} 长度={rem.Length} 错误={firstErr.Trim()}");

            if (rem.Length > 0 && (rc2 == 0 || rem.Contains("lladdr")))
            {
                var names = new Dictionary<string, string>(StringComparer.Ordinal);
                var ifaceOf = new Dictionary<string, string>(StringComparer.Ordinal);       // ip → 接口名
                var signalOf = new Dictionary<string, string>(StringComparer.Ordinal);     // MAC → "-49dBm"
                var stationOn = new Dictionary<string, string>(StringComparer.Ordinal);    // MAC → 接口名
                var ifaceType = new Dictionary<string, string>(StringComparer.Ordinal);    // 接口 → AP/station/...
                var ifaceBand = new Dictionary<string, string>(StringComparer.Ordinal);    // 接口 → 2.4G/5G

                var seg = "NEIGH";
                string? lastIface = null;
                string? lastStation = null;
                var wanIface = "";
                foreach (var raw in rem.Split('\n'))
                {
                    var t = raw.Trim();
                    if (t == "---IW---") { seg = "IW"; lastIface = null; continue; }
                    if (t == "---STATION---") { seg = "STATION"; lastIface = null; lastStation = null; continue; }
                    if (t == "---ROUTE---") { seg = "ROUTE"; continue; }
                    if (t == "---LEASES---") { seg = "LEASES"; continue; }
                    if (t == "") continue;

                    if (seg == "IW")
                    {
                        // Interface wlp2s0 / type AP / channel 6 (2437 MHz), width: 20 MHz
                        var mi = Regex.Match(t, @"^Interface\s+(\S+)$");
                        if (mi.Success) { lastIface = mi.Groups[1].Value; continue; }
                        if (lastIface == null) continue;
                        var mt = Regex.Match(t, @"^type\s+(\S+)$");
                        if (mt.Success) { ifaceType[lastIface] = mt.Groups[1].Value; continue; }
                        var mc = Regex.Match(t, @"^channel\s+(\d+)");
                        if (mc.Success && int.TryParse(mc.Groups[1].Value, out var ch))
                            ifaceBand[lastIface] = ch <= 14 ? "2.4G" : "5G";
                        continue;
                    }

                    if (seg == "STATION")
                    {
                        var ms = Regex.Match(t, @"^Station\s+([0-9a-fA-F:]{17})\s+\(on\s+(\S+)\)");
                        if (ms.Success)
                        {
                            var mac2 = ms.Groups[1].Value.ToUpperInvariant();
                            stationOn[mac2] = ms.Groups[2].Value;
                            lastIface = ms.Groups[2].Value;
                            lastStation = mac2;
                            continue;
                        }
                        var mg = Regex.Match(t, @"^signal:\s+(-?\d+)\s");
                        if (mg.Success && lastStation != null)
                            signalOf[lastStation] = mg.Groups[1].Value + "dBm";
                        continue;
                    }

                    if (seg == "ROUTE")
                    {
                        var mr = Regex.Match(t, @"\bdev\s+(\S+)");
                        if (mr.Success) wanIface = mr.Groups[1].Value;
                        continue;
                    }

                    if (seg == "NEIGH")
                    {
                        // ip neigh: 192.168.1.23 dev wlan0 lladdr 3c:cd:5d:xx:xx:xx REACHABLE
                        var mn = Regex.Match(t, @"^(\d+\.\d+\.\d+\.\d+)\s+dev\s+(\S+)\s+lladdr\s+([0-9a-fA-F:]{17})");
                        if (mn.Success)
                        {
                            var ip = mn.Groups[1].Value;
                            if (!ip.StartsWith(head)) continue;          // 只认扫描网段，跳过 docker0 / 上游网关
                            var iface = mn.Groups[2].Value;
                            var mac = mn.Groups[3].Value.ToUpperInvariant();
                            ifaceOf[ip] = iface;
                            if (!devices.ContainsKey(ip))
                                devices[ip] = new LanDevice { Ip = ip, Mac = mac, Vendor = Vendor(mac), Source = "软路由" };
                            else
                            {
                                devices[ip].Source = "本机+软路由";
                                if (string.IsNullOrEmpty(devices[ip].Mac)) devices[ip].Mac = mac;
                                if (devices[ip].Vendor == "—") devices[ip].Vendor = Vendor(mac);
                            }
                        }
                        continue;
                    }

                    // LEASES 段：dnsmasq / NetworkManager 写法都在这里
                    var ml = Regex.Match(t, @"^\d{9,}\s+([0-9a-fA-F:]{17})\s+(\d+\.\d+\.\d+\.\d+)\s+(\S+)");
                    if (ml.Success)
                    {
                        var host = ml.Groups[3].Value.TrimEnd('*').Trim();
                        if (host != "" && host != "*") names[ml.Groups[2].Value] = host;
                    }
                }

                // ---- 把"连在哪"算出来（不额外发包，全部来自上面这一条 ssh）----
                foreach (var d in devices.Values)
                {
                    if (selfIps.Contains(d.Ip)) { d.Conn = "本机"; continue; }
                    var macKey = d.Mac.ToUpperInvariant();
                    if (stationOn.TryGetValue(macKey, out var apIface))
                    {
                        var band = ifaceBand.TryGetValue(apIface, out var bd) ? bd : "";
                        var sig = signalOf.TryGetValue(macKey, out var sg) ? sg : "";
                        d.Conn = ("无线 " + band + " " + sig).Trim();
                        continue;
                    }
                    if (ifaceOf.TryGetValue(d.Ip, out var ifc))
                    {
                        if (!string.IsNullOrEmpty(wanIface) && ifc == wanIface) d.Conn = "有线（外网侧）";
                        else if (ifaceType.TryGetValue(ifc, out var ty) && ty == "AP") d.Conn = "无线 " + (ifaceBand.TryGetValue(ifc, out var b2) ? b2 : "");
                        else d.Conn = ifc;
                        continue;
                    }
                    // 留着空，交给后面统一兜底（网关 / 同网段 / 本机）
                }

                AppPaths.Log($"[设备] 解析：接口={ifaceOf.Count} 站点={stationOn.Count} 信号={signalOf.Count} 类型={ifaceType.Count} 频段={ifaceBand.Count} wan={wanIface} 名字={names.Count}");

                foreach (var kv in names)
                    if (devices.TryGetValue(kv.Key, out var d)) d.Name = kv.Value;
            }
        }

        // 没走远端查询的设备（只在本机 ARP 里看到）补个默认值
        var localGw = DefaultGateway();
        foreach (var d in devices.Values)
            if (string.IsNullOrEmpty(d.Conn))
                d.Conn = selfIps.Contains(d.Ip) ? "本机"
                        : (!string.IsNullOrEmpty(localGw) && d.Ip == localGw ? "网关（本机直连）" : "同网段");

        // 自己这台也列出来（ARP 表里没有自己的条目）
        foreach (var sip in selfIps)
        {
            if (!sip.StartsWith(head)) continue;
            if (!devices.ContainsKey(sip))
                devices[sip] = new LanDevice { Ip = sip, Mac = "", Vendor = "", Source = "本机", Name = "这台电脑" };
        }

        // ---------- ③.5 名称：问软路由的 dnsmasq 要反向 DNS ----------
        // 只对"已经找到的设备"查，绝不扫网段；查不到就留空。
        await FillNamesAsync(devices, head, selfIps, progress, ct);

        // ---------- ③.8 延迟：快速模式给已知设备补 1 个轻 ping ----------
        // 深度模式下面的 alive 已经是 3 包真实值，这里只补快速模式。
        if (!deep) await LightPingAsync(devices, alive, selfIps, progress, ct);

        // ---------- ④ 组装 ----------
        foreach (var kv in devices)
        {
            var d = kv.Value;
            if (selfIps.Contains(d.Ip) && !d.Name.Contains("这台电脑"))
                d.Name = d.Name == "" ? "这台电脑" : d.Name + "（这台电脑）";

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
            else if (d.State == "" || d.State == null)
            {
                d.State = "不在线";
                d.Rtt = "—";
            }
            else
            {
                d.Rtt = "—";                                  // 邻居表给的状态（在线/可能在线），保留
            }
        }

        var list = devices.Values
            .OrderBy(d => IPAddress.TryParse(d.Ip, out var a) ? 0 : 1)
            .ThenBy(d => IPAddress.TryParse(d.Ip, out var a) ? a.GetAddressBytes()[3] : 0)
            .ToList();

        progress($"扫完：{list.Count} 台设备，{list.Count(x => x.State == "在线")} 台在线");
        return list;
    }

    // ================= 名称：反向 DNS（PTR） =================

    /// <summary>
    /// 向软路由的 dnsmasq 查每台设备的 PTR 记录。
    /// 名字只有 DHCP 服务器知道，所以这一步必须在"知道路由器地址"时才做。
    /// 上限 40 台；每台 2 秒超时；任何异常都只是"这个名字查不到"，不影响扫描结果。
    /// </summary>
    private static async Task FillNamesAsync(
        Dictionary<string, LanDevice> devices, string head, HashSet<string> selfIps,
        Action<string> progress, CancellationToken ct)
    {
        var gwForFilter = DefaultGateway();
        var need = devices.Values
            .Where(d => string.IsNullOrWhiteSpace(d.Name) && d.Ip.StartsWith(head)
                        && !selfIps.Contains(d.Ip) && d.Ip != gwForFilter)   // 路由器自己不用查，白等 1 秒
            .Select(d => d.Ip).Take(41).ToList();
        if (need.Count == 0) return;

        if (need.Count > 40)
        {
            progress("设备太多（>40 台），跳过名字查询");
            return;
        }

        // DNS 服务器 = 默认网关（shared 模式下 dnsmasq 就挂在网关地址上）
        var gw = gwForFilter;
        if (string.IsNullOrEmpty(gw))
        {
            progress("没有默认网关，跳过名字查询");
            return;
        }

        progress($"正在问 {gw} 要设备名（{need.Count} 个）…");
        var got = 0;
        // 并发 8、单次 1200ms 超时：既快又不会把路由器问懵
        using var gate = new SemaphoreSlim(8);
        var tasks = need.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var name = await PtrLookupAsync(gw, ip, 1200, ct);
                if (IsUsableName(name, ip) && devices.TryGetValue(ip, out var d) && string.IsNullOrWhiteSpace(d.Name))
                {
                    d.Name = name;
                    Interlocked.Increment(ref got);
                }
            }
            catch { }
            finally { gate.Release(); }
        }).ToList();
        try { await Task.WhenAll(tasks); } catch { }
        progress(got > 0 ? $"名字查到 {got} 个" : "没查到名字（路由器没有这些设备的反向记录）");
    }

    /// <summary>dnsmasq 对查不到的地址会回 bogon / localhost 这类占位名，不能当设备名用</summary>
    private static bool IsUsableName(string name, string ip)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();
        if (n.Length < 2 || n.Length > 64) return false;
        if (n == ip) return false;
        foreach (var bad in new[] { "bogon", "localhost", "local", "unknown", "none" })
            if (n.Equals(bad, StringComparison.OrdinalIgnoreCase) || n.StartsWith(bad + ".", StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private static string DefaultGateway()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var g = ni.GetIPProperties().GatewayAddresses
                    .FirstOrDefault(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                         !x.Address.Equals(IPAddress.Any));
                if (g != null) return g.Address.ToString();
            }
        }
        catch { }
        return "";
    }

    /// <summary>手搓一个 PTR 查询，不依赖系统 DNS 设置（系统 DNS 是校园网的，不知道局域网名字）</summary>
    private static async Task<string> PtrLookupAsync(string server, string ip, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var id = (ushort)Random.Shared.Next(1, 65535);
            var q = new List<byte>
            {
                (byte)(id >> 8), (byte)(id & 0xFF),   // ID
                0x01, 0x00,                           // 标准查询，期望递归
                0x00, 0x01,                           // QDCOUNT = 1
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00,   // AN/NS/AR = 0
            };
            var octets = ip.Split('.').Reverse();
            foreach (var o in octets) { q.Add((byte)o.Length); foreach (var c in o) q.Add((byte)c); }
            foreach (var lbl in new[] { "in-addr", "arpa" })
            {
                q.Add((byte)lbl.Length);
                foreach (var c in lbl) q.Add((byte)c);
            }
            q.Add(0x00);                      // 名称结束
            q.Add(0x00); q.Add(0x0C);         // QTYPE = PTR
            q.Add(0x00); q.Add(0x01);         // QCLASS = IN

            using var udp = new System.Net.Sockets.UdpClient();
            var payload = q.ToArray();
            await udp.SendAsync(payload, payload.Length, server, 53);

            var recv = udp.ReceiveAsync(ct).AsTask();
            var done = await Task.WhenAny(recv, Task.Delay(timeoutMs, ct));
            if (done != recv) return "";
            var resp = await recv;
            return ParseFirstPtr(resp.Buffer);
        }
        catch { return ""; }
    }

    /// <summary>从 DNS 应答里取第一条 PTR 的名字</summary>
    private static string ParseFirstPtr(byte[] b)
    {
        if (b.Length < 12) return "";
        int qd = (b[4] << 8) | b[5];
        int an = (b[6] << 8) | b[7];
        if (an == 0) return "";
        int p = 12;
        for (int i = 0; i < qd; i++)                 // 跳过问题区
        {
            p = SkipName(b, p);
            p += 4;
            if (p >= b.Length) return "";
        }
        for (int i = 0; i < an && p + 10 < b.Length; i++)
        {
            p = SkipName(b, p);                       // 记录名（可能是压缩指针）
            if (p + 10 > b.Length) return "";
            int type = (b[p] << 8) | b[p + 1];
            int rdlen = (b[p + 8] << 8) | b[p + 9];
            int rd = p + 10;
            if (type == 12 && rdlen > 0) return ReadName(b, rd).TrimEnd('.').Trim();
            p = rd + rdlen;
        }
        return "";
    }

    private static int SkipName(byte[] b, int p)
    {
        while (p < b.Length)
        {
            int len = b[p];
            if (len == 0) return p + 1;
            if ((len & 0xC0) == 0xC0) return p + 2;   // 压缩指针，占 2 字节
            p += 1 + len;
        }
        return p;
    }

    private static string ReadName(byte[] b, int p)
    {
        var sb = new System.Text.StringBuilder();
        int guard = 0;
        while (p < b.Length && guard++ < 32)
        {
            int len = b[p];
            if (len == 0) break;
            if ((len & 0xC0) == 0xC0)                 // 指针
            {
                if (p + 1 >= b.Length) break;
                p = ((len & 0x3F) << 8) | b[p + 1];
                continue;
            }
            p++;
            if (p + len > b.Length) break;
            sb.Append(System.Text.Encoding.ASCII.GetString(b, p, len)).Append('.');
            p += len;
        }
        return sb.ToString();
    }

    // ================= 延迟：轻量 ping =================

    /// <summary>
    /// 快速模式下给"已经有名字/在邻居表里"的设备各发 1 个 ping。
    /// 1 包/台、400ms 超时、40ms 间隔、最多 40 台 —— 和"254 地址 × 48 并发"不是一个量级。
    /// </summary>
    private static async Task LightPingAsync(
        Dictionary<string, LanDevice> devices,
        System.Collections.Concurrent.ConcurrentDictionary<string, long> alive,
        HashSet<string> selfIps, Action<string> progress, CancellationToken ct)
    {
        var targets = devices.Values
            .Where(d => !selfIps.Contains(d.Ip) && d.Ip.Contains('.'))
            .Select(d => d.Ip).Take(41).ToList();
        if (targets.Count == 0) return;
        if (targets.Count > 40)
        {
            progress("设备太多（>40 台），跳过延迟探测");
            return;
        }

        progress($"正在测延迟（{targets.Count} 台，每台 1 个包）…");
        foreach (var ip in targets)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                using var ping = new Ping();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var reply = await ping.SendPingAsync(ip, 400);
                if (reply.Status == IPStatus.Success) alive[ip] = sw.ElapsedMilliseconds;
            }
            catch { }
            try { await Task.Delay(40, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
