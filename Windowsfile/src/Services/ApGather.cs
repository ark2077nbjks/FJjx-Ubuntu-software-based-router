using System.Text.RegularExpressions;

namespace NetPanel.Services;

public sealed class ApSta
{
    public string Mac = "", Signal = "", Tx = "", Rx = "", Inactive = "";
}

/// <summary>软路由 AP 的实时射频状态（只读，来自 iw）</summary>
public sealed class ApInfo
{
    public bool Ok;
    public string Err = "";
    public string Iface = "", Ssid = "", IfType = "";
    public string Freq = "", Ch = "", Width = "", Power = "";
    public int StaCount;
    public List<ApSta> Stas = new();

    public string Band => Freq.Length == 0 ? ""
        : (int.TryParse(Freq, out var f) && f < 3000 ? "2.4G" : "5G");

    public string BandText => Band switch { "2.4G" => "2.4 GHz", "5G" => "5 GHz", _ => "—" };
    public string ChText => Ch.Length == 0 ? "—" : (Freq.Length == 0 ? Ch : $"{Ch}（{Freq} MHz）");
    public string WidthText => Width.Length == 0 ? "—" : Width + " MHz";
    public string PowerText => Power.Length == 0 ? "—" : Power + " dBm";
    public string SsidText => Ssid.Length == 0 ? "—" : Ssid;
}

public static class ApGather
{
    /// <summary>读一次：iw dev info + station dump（一次 ssh，只读）</summary>
    public static async Task<ApInfo> ReadAsync()
    {
        var info = new ApInfo();
        try
        {
            var cfg = RemoteConfig.Load();
            var host = await Remote.ResolveAsync(cfg);
            if (host == null) { info.Err = "远端连不上"; return info; }

            var remote =
                "\"IF=$(for i in $(iw dev 2>/dev/null | sed -n 's/^Interface //p'); do " +
                "[ \\\"$(iw dev $i info 2>/dev/null | awk '/type/{print $2}')\\\" = AP ] && { echo $i; break; }; done); " +
                "[ -z \\\"$IF\\\" ] && IF=$(iw dev 2>/dev/null | sed -n 's/^Interface //p' | head -1); " +
                "[ -z \\\"$IF\\\" ] && IF=wlp2s0; " +
                "iw dev $IF info 2>/dev/null; echo ===STA===; iw dev $IF station dump 2>/dev/null\"";
            var args = Remote.BaseArgs(cfg, true) + cfg.Target + " " + remote;

            var run = await Remote.RunAsync("ssh.exe", args, 12);
            var outText = run.Out ?? "";
            if (outText.Contains("NOINFO") || string.IsNullOrWhiteSpace(outText))
            {
                info.Err = "远端没返回数据";
                return info;
            }

            var parts = outText.Split("===STA===", 2);
            var head = parts[0];
            info.Stas = ParseStations(parts.Length > 1 ? parts[1] : "");

            info.Iface = Rx(head, @"Interface (\S+)") ?? "wlp2s0";
            info.Ssid = Rx(head, @"\n\s*ssid (.+)")?.Trim() ?? "";
            info.IfType = Rx(head, @"\n\s*type (\w+)") ?? "";

            var chm = Regex.Match(head, @"channel (\d+) \((\d+) MHz\)");
            if (chm.Success) { info.Ch = chm.Groups[1].Value; info.Freq = chm.Groups[2].Value; }

            var wm = Regex.Match(head, @"width: (\d+) MHz");
            if (wm.Success) info.Width = wm.Groups[1].Value;

            var pm = Regex.Match(head, @"txpower ([\d.]+)");
            if (pm.Success) info.Power = pm.Groups[1].Value;

            info.StaCount = info.Stas.Count;
            info.Ok = info.IfType.Equals("AP", StringComparison.OrdinalIgnoreCase);
            if (!info.Ok) info.Err = "这块网卡当前不是 AP 模式";
        }
        catch (Exception ex)
        {
            info.Err = ex.Message;
        }
        return info;
    }

    private static string? Rx(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<ApSta> ParseStations(string raw)
    {
        var list = new List<ApSta>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        ApSta? cur = null;
        foreach (var line in raw.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.StartsWith("Station "))
            {
                if (cur != null) list.Add(cur);
                var mac = t.Substring(8).Trim();
                var cut = mac.IndexOf(" (on ", StringComparison.Ordinal);
                if (cut > 0) mac = mac.Substring(0, cut);
                cur = new ApSta { Mac = mac };
                continue;
            }
            if (cur == null) continue;

            var m1 = Regex.Match(t, @"^\s*signal:\s+(-?\d+)");
            if (m1.Success) { cur.Signal = m1.Groups[1].Value + " dBm"; continue; }

            var m2 = Regex.Match(t, @"^\s*tx bitrate:\s+([\d.]+)");
            if (m2.Success) { cur.Tx = m2.Groups[1].Value; continue; }

            var m2b = Regex.Match(t, @"^\s*rx bitrate:\s+([\d.]+)");
            if (m2b.Success) { cur.Rx = m2b.Groups[1].Value; continue; }

            var m3 = Regex.Match(t, @"^\s*inactive time:\s+(\d+)");
            if (m3.Success) { cur.Inactive = m3.Groups[1].Value; continue; }
        }
        if (cur != null) list.Add(cur);

        // 信号强的排前面
        list.Sort((a, b) =>
        {
            int pa = int.TryParse(a.Signal.Replace(" dBm", ""), out var x) ? x : -999;
            int pb = int.TryParse(b.Signal.Replace(" dBm", ""), out var y) ? y : -999;
            return pb.CompareTo(pa);
        });
        return list;
    }
}
