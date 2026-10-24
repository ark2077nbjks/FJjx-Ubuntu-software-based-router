using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace NetPanel.Services;

public sealed class SiteResult
{
    public string Input { get; set; } = "";
    public string Host { get; set; } = "";
    public string Ip { get; set; } = "";
    public bool Ok { get; set; }
    public int Status { get; set; }
    public string Error { get; set; } = "";
    public double DnsMs { get; set; } = -1;
    public double TcpMs { get; set; } = -1;
    public double TlsMs { get; set; } = -1;
    public double TtfbMs { get; set; } = -1;
    public double TotalMs { get; set; }

    public string StageDetail()
    {
        var parts = new List<string>();
        if (DnsMs >= 0) parts.Add($"DNS {DnsMs:F0}");
        else parts.Add("DNS 失败");
        if (TcpMs >= 0) parts.Add($"连接 {TcpMs:F0}"); 
        if (TlsMs >= 0) parts.Add($"TLS {TlsMs:F0}");
        if (TtfbMs >= 0) parts.Add($"首字节 {TtfbMs:F0}");
        parts.Add($"共 {TotalMs:F0}ms");
        return string.Join(" / ", parts);
    }
}

public static class SiteCheck
{
    /// <summary>检查一个网址：DNS → TCP → TLS → HTTP，逐段计时</summary>
    public static async Task<SiteResult> CheckAsync(string input, int timeoutMs = 6000, CancellationToken ct = default)
    {
        var r = new SiteResult { Input = input };
        var swTotal = Stopwatch.StartNew();

        try
        {
            string raw = input.Trim();
            if (raw.Length == 0) { r.Error = "空地址"; return r; }
            if (!raw.Contains("://")) raw = "https://" + raw;

            var uri = new Uri(raw);
            string host = uri.Host;
            int port = uri.Port > 0 ? uri.Port : (uri.Scheme == "http" ? 80 : 443);
            bool tls = uri.Scheme == "https";
            r.Host = host;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            // ---- 1. DNS ----
            var sw = Stopwatch.StartNew();
            IPAddress? ip = null;
            try
            {
                if (IPAddress.TryParse(host, out var direct)) ip = direct;
                else
                {
                    var addrs = await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
                    ip = addrs.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault();
                }
            }
            catch (Exception) { }
            r.DnsMs = sw.Elapsed.TotalMilliseconds;

            if (ip == null) { r.Error = "DNS 解析失败（域名查不到，可能被墙或网络断了）"; return r; }
            r.Ip = ip.ToString();

            // ---- 2. TCP ----
            using var tcp = new TcpClient();
            sw.Restart();
            try
            {
                await tcp.ConnectAsync(ip, port, cts.Token);
            }
            catch (OperationCanceledException) { r.TcpMs = sw.Elapsed.TotalMilliseconds; r.Error = $"连接超时（{port} 端口无响应）"; return r; }
            catch (Exception ex) { r.TcpMs = sw.Elapsed.TotalMilliseconds; r.Error = "连接被拒绝：" + ex.Message; return r; }
            r.TcpMs = sw.Elapsed.TotalMilliseconds;

            Stream stream = tcp.GetStream();

            // ---- 3. TLS ----
            if (tls)
            {
                sw.Restart();
                var ssl = new SslStream(stream, false, (_, _, _, _) => true);
                try
                {
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
                    r.TlsMs = sw.Elapsed.TotalMilliseconds;
                    stream = ssl;
                }
                catch (OperationCanceledException) { r.Error = "TLS 握手超时"; return r; }
                catch (Exception ex)
                {
                    r.TlsMs = sw.Elapsed.TotalMilliseconds;
                    var m = ex.Message;
                    bool reset = m.Contains("forced") || m.Contains("reset", StringComparison.OrdinalIgnoreCase)
                                 || m.Contains("EOF") || m.Contains("0 bytes")
                                 || m.Contains("Unable to read data") || m.Contains("强迫关闭")
                                 || m.Contains("连接被关闭") || m.Contains("已中止");
                    r.Error = reset ? "TLS 连接被重置（多半是被墙/被拦截）" : "TLS 握手失败：" + m;
                    return r;
                }
            }

            // ---- 4. HTTP ----
            string path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            async Task<string?> Request(string verb)
            {
                var sb = new StringBuilder();
                sb.Append($"{verb} {path} HTTP/1.1\r\n");
                sb.Append($"Host: {host}\r\n");
                sb.Append("User-Agent: NetPanel/0.5 (Windows)\r\n");
                sb.Append("Accept: */*\r\n");
                sb.Append("Connection: close\r\n\r\n");
                var bytes = Encoding.ASCII.GetBytes(sb.ToString());
                sw.Restart();
                await stream.WriteAsync(bytes, cts.Token);
                await stream.FlushAsync(cts.Token);

                var buf = new byte[2048];
                var acc = new StringBuilder();
                while (acc.Length < 8192)
                {
                    int n = await stream.ReadAsync(buf, cts.Token);
                    if (n <= 0) break;
                    acc.Append(Encoding.ASCII.GetString(buf, 0, n));
                    if (acc.ToString().Contains("\r\n\r\n")) break;
                }
                r.TtfbMs = sw.Elapsed.TotalMilliseconds;
                return acc.Length == 0 ? null : acc.ToString();
            }

            string? resp;
            try { resp = await Request("HEAD"); }
            catch (OperationCanceledException) { r.Error = "HTTP 请求超时（连接通了但服务器不回）"; return r; }
            catch (Exception ex) { r.Error = "HTTP 失败：" + ex.Message; return r; }

            if (resp == null) { r.Error = "服务器没有返回任何内容"; return r; }

            int code = ParseStatus(resp);
            // 有些服务器不认 HEAD，重试 GET
            if (code == 405 || code == 501 || code == 0)
            {
                try
                {
                    using var tcp2 = new TcpClient();
                    await tcp2.ConnectAsync(ip, port, cts.Token);
                    Stream s2 = tcp2.GetStream();
                    if (tls)
                    {
                        var ssl2 = new SslStream(s2, false, (_, _, _, _) => true);
                        await ssl2.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
                        s2 = ssl2;
                    }
                    stream = s2;
                    var resp2 = await Request("GET");
                    if (resp2 != null) code = ParseStatus(resp2);
                }
                catch { }
            }

            r.Status = code;
            r.Ok = code > 0;
            if (code == 0) r.Error = "返回内容里没有 HTTP 状态行";
            return r;
        }
        catch (Exception ex)
        {
            r.Error = ex.Message;
            return r;
        }
        finally
        {
            r.TotalMs = swTotal.Elapsed.TotalMilliseconds;
        }
    }

    private static int ParseStatus(string resp)
    {
        // HTTP/1.1 200 OK
        var line = resp.Split('\n')[0].Trim();
        var parts = line.Split(' ');
        if (parts.Length >= 2 && int.TryParse(parts[1], out int code)) return code;
        return 0;
    }

    /// <summary>状态码 → 人话</summary>
    public static string Describe(SiteResult r)
    {
        if (!string.IsNullOrEmpty(r.Error)) return r.Error;
        return r.Status switch
        {
            >= 200 and < 300 => "正常",
            >= 300 and < 400 => "重定向（能连）",
            401 => "需要登录（能连）",
            403 => "拒绝访问（能连，可能屏蔽了非浏览器）",
            404 => "页面不存在（但服务器是通的）",
            >= 500 => "服务器报错",
            _ => $"HTTP {r.Status}"
        };
    }
}
