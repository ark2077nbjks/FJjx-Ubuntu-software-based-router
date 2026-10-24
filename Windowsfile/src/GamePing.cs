using System.Diagnostics;
using System.Net.NetworkInformation;

namespace NetPanel.Services;

public sealed class PingTarget
{
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public string Note { get; init; } = "";
    public override string ToString() => Name;
}

public sealed class PingStats
{
    public string Host { get; set; } = "";
    public int Sent { get; set; }
    public int Received { get; set; }
    public double Min { get; set; }
    public double Avg { get; set; }
    public double Max { get; set; }
    public double Jitter { get; set; }
    public double LossPct { get; set; }
    public string Grade { get; set; } = "";
    public string Verdict { get; set; } = "";
}

public static class GamePing
{
    /// <summary>预置目标（都是实测能解析、能回 ICMP 的）</summary>
    public static readonly PingTarget[] Targets =
    {
        new() { Name = "Valve 香港（CS2 亚服走这里）", Host = "103.28.54.136", Note = "实测 26ms，打 CS2 首选" },
        new() { Name = "Valve 新加坡",                 Host = "103.10.124.84", Note = "实测 315ms，路由绕远" },
        new() { Name = "Steam 商店 / CDN",             Host = "store.steampowered.com", Note = "网页与下载用" },
        new() { Name = "国内对照（阿里 DNS）",          Host = "223.5.5.5", Note = "衡量本地基础延迟" },
    };

    /// <summary>连续发 count 个 ICMP，边发边报</summary>
    public static async Task<PingStats> RunAsync(
        string host, int count, int intervalMs,
        IProgress<(int Done, int Rtt, int Total)> progress,
        CancellationToken ct)
    {
        var stats = new PingStats { Host = host };
        var rtts = new List<double>();

        using var ping = new Ping();

        for (int i = 0; i < count; i++)
        {
            if (ct.IsCancellationRequested) break;

            int rtt = -1;
            try
            {
                var reply = await ping.SendPingAsync(host, 2000);
                if (reply.Status == IPStatus.Success) rtt = (int)reply.RoundtripTime;
            }
            catch { rtt = -1; }

            stats.Sent++;
            if (rtt >= 0)
            {
                stats.Received++;
                rtts.Add(rtt);
            }
            progress.Report((stats.Sent, rtt, count));

            if (i < count - 1)
            {
                try { await Task.Delay(intervalMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        Calculate(stats, rtts);
        return stats;
    }

    private static void Calculate(PingStats s, List<double> rtts)
    {
        if (rtts.Count == 0)
        {
            s.Min = s.Avg = s.Max = s.Jitter = -1;
            s.LossPct = 100;
            s.Grade = "不可用";
            s.Verdict = "一个包都没回来：目标屏蔽了 ICMP，或者网络断了";
            return;
        }

        s.Min = rtts.Min();
        s.Max = rtts.Max();
        s.Avg = rtts.Average();
        s.LossPct = s.Sent == 0 ? 0 : (s.Sent - s.Received) * 100.0 / s.Sent;

        double jitter = 0;
        for (int i = 1; i < rtts.Count; i++) jitter += Math.Abs(rtts[i] - rtts[i - 1]);
        s.Jitter = rtts.Count > 1 ? jitter / (rtts.Count - 1) : 0;

        (s.Grade, s.Verdict) = Judge(s.Avg, s.Jitter, s.LossPct);
    }

    /// <summary>按 CS2 这种对枪向 FPS 的标准判级</summary>
    public static (string Grade, string Verdict) Judge(double avg, double jitter, double loss)
    {
        if (loss >= 3)
            return ("不适合", $"丢包 {loss:F0}%，游戏会瞬移、技能放不出——先查线路或换节点");
        if (avg <= 30 && jitter <= 5 && loss < 1)
            return ("极佳", "这个延迟打 CS2 对枪完全没问题");
        if (avg <= 50 && jitter <= 10 && loss < 2)
            return ("良好", "可以正常打，偶尔有轻微延迟感");
        if (avg <= 80 && jitter <= 20 && loss < 3)
            return ("勉强", "能玩，但抢枪时会慢半拍");
        return ("不适合", $"延迟 {avg:F0}ms / 抖动 {jitter:F0}ms 偏高，对枪吃亏——换个更近的服务器");
    }
}
