using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;

namespace NetPanel.Services;

public sealed record SpeedSample(double Seconds, double Mbps, long TotalBytes);

public sealed class SpeedSource
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public override string ToString() => Name;
}

public static class SpeedTest
{
    /// <summary>实测可用测速源（2026-10 实测，按单连接速度排序）</summary>
    public static readonly SpeedSource[] Sources =
    {
        new() { Name = "清华 TUNA（默认，最稳）", Url = "https://mirrors.tuna.tsinghua.edu.cn/ubuntu/ls-lR.gz" },
        new() { Name = "腾讯云（实测最快）",      Url = "https://mirrors.cloud.tencent.com/ubuntu/ls-lR.gz" },
        new() { Name = "华为云",                  Url = "https://mirrors.huaweicloud.com/ubuntu/ls-lR.gz" },
        new() { Name = "南京大学",                Url = "https://mirror.nju.edu.cn/ubuntu/ls-lR.gz" },
        new() { Name = "上海交大",                Url = "https://mirror.sjtu.edu.cn/ubuntu/ls-lR.gz" },
        new() { Name = "阿里云（较慢）",          Url = "https://mirrors.aliyun.com/ubuntu/ls-lR.gz" },
        new() { Name = "Cloudflare（最后备选）",  Url = "https://speed.cloudflare.com/__down?bytes=50000000" },
    };

    /// <summary>
    /// 多连接并发测速：同时开 connections 条连接累加带宽，结果更接近真实值。
    /// 每条连接取文件的不同区间（服务器不支持 Range 时自动退化成整文件，照样能测）。
    /// </summary>
    public static async Task<SpeedResult> RunAsync(
        SpeedSource source,
        TimeSpan duration,
        int connections,
        IProgress<SpeedSample> progress,
        CancellationToken ct)
    {
        if (connections < 1) connections = 1;

        var result = new SpeedResult { Source = source.Name, Connections = connections };

        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 NetPanel/1.0");

        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long total = 0;
        int failures = 0;
        string lastError = "";

        var tasks = new Task[connections];
        for (int i = 0; i < connections; i++)
        {
            int idx = i;
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, source.Url);
                    // 每条连接取 50MB 窗口：0-49MB, 50-99MB, ...
                    req.Headers.Range = new RangeHeaderValue(idx * 50L * 1024 * 1024, null);
                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stopCts.Token);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref failures);
                        if (string.IsNullOrEmpty(lastError)) lastError = $"HTTP {(int)resp.StatusCode}";
                        return;
                    }
                    await using var stream = await resp.Content.ReadAsStreamAsync(stopCts.Token);
                    var buffer = new byte[128 * 1024];
                    while (!stopCts.IsCancellationRequested)
                    {
                        int n;
                        try { n = await stream.ReadAsync(buffer, stopCts.Token); }
                        catch (OperationCanceledException) { break; }
                        if (n <= 0) break;
                        Interlocked.Add(ref total, n);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failures);
                    if (string.IsNullOrEmpty(lastError)) lastError = ex.Message;
                }
            });
        }

        // 采样循环：每 200ms 取一次累计字节，算瞬时带宽
        var sw = Stopwatch.StartNew();
        var lastTick = Stopwatch.StartNew();
        long lastBytes = 0;
        double peak = 0;

        while (sw.Elapsed < duration && !ct.IsCancellationRequested)
        {
            await Task.Delay(150, CancellationToken.None);
            if (lastTick.Elapsed.TotalMilliseconds < 200) continue;

            long cur = Interlocked.Read(ref total);
            double dt = lastTick.Elapsed.TotalSeconds;
            double mbps = (cur - lastBytes) * 8.0 / dt / 1_000_000.0;
            if (mbps > peak) peak = mbps;
            progress.Report(new SpeedSample(sw.Elapsed.TotalSeconds, mbps, cur));
            lastBytes = cur;
            lastTick.Restart();

            if (failures >= connections && cur == 0) break;     // 全部连接都失败
            if (Array.TrueForAll(tasks, t => t.IsCompleted) && cur == 0) break;
        }

        stopCts.Cancel();
        try { await Task.WhenAll(tasks); } catch { }

        long finalBytes = Interlocked.Read(ref total);
        if (finalBytes == 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrEmpty(lastError)
                    ? "测速源没有返回数据，换一个源再试。"
                    : $"测速源不可用：{lastError}\n换个测速源再试。");
        }

        double seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.1);
        result.Bytes = finalBytes;
        result.Seconds = seconds;
        result.AverageMbps = finalBytes * 8.0 / seconds / 1_000_000.0;
        result.PeakMbps = peak;
        result.Cancelled = ct.IsCancellationRequested || sw.Elapsed < duration;
        return result;
    }

    /// <summary>简单延迟：连一次主机 443 端口的耗时</summary>
    public static async Task<int> PingAsync(string url)
    {
        try
        {
            var uri = new Uri(url);
            var sw = Stopwatch.StartNew();
            using var tcp = new System.Net.Sockets.TcpClient();
            var task = tcp.ConnectAsync(uri.Host, uri.Port);
            var done = await Task.WhenAny(task, Task.Delay(3000));
            if (done != task) return -1;
            await task;
            return (int)sw.Elapsed.TotalMilliseconds;
        }
        catch { return -1; }
    }
}

public sealed class SpeedResult
{
    public string Source { get; set; } = "";
    public int Connections { get; set; } = 1;
    public long Bytes { get; set; }
    public double Seconds { get; set; }
    public double AverageMbps { get; set; }
    public double PeakMbps { get; set; }
    public double TtfbMs { get; set; }
    public bool Cancelled { get; set; }
}
