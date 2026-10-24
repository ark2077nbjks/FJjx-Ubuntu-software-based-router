using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;

namespace NetPanel.Services;

/// <summary>一次采样的结果：某张网卡在这 15 秒里收/发了多少 KB</summary>
public sealed class TrafficSample
{
    public DateTime Time { get; set; }
    public string Adapter { get; set; } = "";
    public double DownKb { get; set; }
    public double UpKb { get; set; }
}

/// <summary>流量统计：只用本机网卡计数器，不发包、不联网，纯读内存数字</summary>
public static class Traffic
{
    private static readonly Dictionary<string, (long rx, long tx)> Last = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastAt = DateTime.MinValue;

    public static int IntervalSeconds { get; } = 15;

    public static string FileFor(DateTime day) =>
        Path.Combine(AppPaths.DataDir, $"traffic-{day:yyyyMMdd}.csv");

    public static string TodayFile => FileFor(DateTime.Today);

    /// <summary>读一次网卡计数器，算出这段时间的增量。第一次调用只记基线，返回空。</summary>
    public static List<TrafficSample> Sample()
    {
        var rows = new List<TrafficSample>();
        var now = DateTime.Now;

        // 只认真网卡：物理类型 + 有 MAC，再按 MAC 去重
        // （Windows 会给同一块网卡挂 5~6 个"过滤器伪网卡"，计数器完全一样，不去重就会算 6 遍）
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                                            or NetworkInterfaceType.GigabitEthernet
                                            or NetworkInterfaceType.Wireless80211
                                            or NetworkInterfaceType.FastEthernetT
                                            or NetworkInterfaceType.FastEthernetFx)
            .Where(n =>
            {
                try { return n.GetPhysicalAddress().GetAddressBytes().Length == 6; }
                catch { return false; }
            })
            .GroupBy(n => n.GetPhysicalAddress().ToString())     // 按 MAC 去重
            .Select(g => g.OrderBy(n => n.Name.Length).First())  // 留名字最短的那个（WLAN / 以太网）
            .ToList();

        double seconds = _lastAt == DateTime.MinValue
            ? IntervalSeconds
            : Math.Max(1, (now - _lastAt).TotalSeconds);

        foreach (var ni in nics)
        {
            long rx, tx;
            try
            {
                var st = ni.GetIPStatistics();
                rx = st.BytesReceived;
                tx = st.BytesSent;
            }
            catch { continue; }

            if (!Last.TryGetValue(ni.Name, out var prev))
            {
                Last[ni.Name] = (rx, tx);          // 第一次：只记基线
                continue;
            }

            var dRx = rx - prev.rx;
            var dTx = tx - prev.tx;
            Last[ni.Name] = (rx, tx);

            if (dRx < 0 || dTx < 0) continue;      // 计数器回绕/重置，跳过

            var kb = (dRx + dTx) / 1024.0;
            if (kb < 1) continue;                  // 没流量就不落盘，省空间

            rows.Add(new TrafficSample
            {
                Time = now,
                Adapter = ni.Name,
                DownKb = dRx / 1024.0,
                UpKb = dTx / 1024.0,
            });
        }

        _lastAt = now;
        return rows;
    }

    /// <summary>追加写当天 CSV：时间,网卡,下行KB,上行KB</summary>
    public static void Append(IEnumerable<TrafficSample> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var path = TodayFile;
            var needHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            using var sw = new StreamWriter(path, true, System.Text.Encoding.UTF8);
            if (needHeader) sw.WriteLine("时间,网卡,下行KB,上行KB");
            foreach (var r in list)
                sw.WriteLine($"{r.Time:yyyy-MM-dd HH:mm:ss},{r.Adapter},{r.DownKb:F1},{r.UpKb:F1}");
        }
        catch (Exception ex) { AppPaths.Log("[流量] 写 CSV 失败: " + ex.Message); }
    }

    public sealed class Row
    {
        public DateTime Time;
        public string Adapter = "";
        public double DownKb, UpKb;
    }

    public static List<Row> ReadDay(DateTime day)
    {
        var rows = new List<Row>();
        var path = FileFor(day);
        if (!File.Exists(path)) return rows;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("时间")) continue;
                var p = t.Split(',');
                if (p.Length < 4) continue;
                if (!DateTime.TryParse(p[0], out var when)) continue;
                if (!double.TryParse(p[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) continue;
                if (!double.TryParse(p[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var u)) continue;
                rows.Add(new Row { Time = when, Adapter = p[1], DownKb = d, UpKb = u });
            }
        }
        catch (Exception ex) { AppPaths.Log("[流量] 读 CSV 失败: " + ex.Message); }
        return rows;
    }

    /// <summary>当天 24 小时分桶，返回 [小时][0=下行MB, 1=上行MB]</summary>
    public static double[][] HourlyBuckets(DateTime day)
    {
        var buckets = new double[24][];
        for (int i = 0; i < 24; i++) buckets[i] = new double[2];
        foreach (var r in ReadDay(day))
        {
            var h = r.Time.Hour;
            buckets[h][0] += r.DownKb / 1024.0;
            buckets[h][1] += r.UpKb / 1024.0;
        }
        return buckets;
    }

    public static (double downMb, double upMb) Total(DateTime day)
    {
        double d = 0, u = 0;
        foreach (var r in ReadDay(day)) { d += r.DownKb; u += r.UpKb; }
        return (d / 1024.0, u / 1024.0);
    }

    /// <summary>最近 N 天（含今天）每天的合计，用来显示近 7 天</summary>
    public static (double downMb, double upMb) TotalDays(int days)
    {
        double d = 0, u = 0;
        for (int i = 0; i < days; i++)
        {
            var t = Total(DateTime.Today.AddDays(-i));
            d += t.downMb; u += t.upMb;
        }
        return (d, u);
    }

    /// <summary>当天按网卡汇总</summary>
    public static List<(string adapter, double downMb, double upMb)> PerAdapter(DateTime day)
    {
        var map = new Dictionary<string, (double d, double u)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ReadDay(day))
        {
            map.TryGetValue(r.Adapter, out var cur);
            map[r.Adapter] = (cur.d + r.DownKb / 1024.0, cur.u + r.UpKb / 1024.0);
        }
        return map.Select(kv => (kv.Key, kv.Value.d, kv.Value.u))
                  .OrderByDescending(x => x.Item2 + x.Item3)
                  .ToList();
    }

    /// <summary>删掉 N 天前的日 CSV</summary>
    public static int Prune(int keepDays = 30)
    {
        int removed = 0;
        try
        {
            var cutoff = DateTime.Today.AddDays(-keepDays);
            foreach (var f in Directory.GetFiles(AppPaths.DataDir, "traffic-*.csv"))
            {
                var name = Path.GetFileNameWithoutExtension(f).Replace("traffic-", "");
                if (!DateTime.TryParseExact(name, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var d)) continue;
                if (d < cutoff) { try { File.Delete(f); removed++; } catch { } }
            }
        }
        catch { }
        return removed;
    }

    public static string SizeText(double mb) =>
        mb >= 1024 ? $"{mb / 1024.0:F2} GB" : mb >= 1 ? $"{mb:F1} MB" : $"{mb * 1024:F0} KB";
}
