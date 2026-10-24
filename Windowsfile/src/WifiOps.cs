using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NetPanel.Services;

public sealed class WifiNetwork
{
    public string Ssid { get; set; } = "";
    public int Signal { get; set; }
    public string Auth { get; set; } = "";
    public string Encrypt { get; set; } = "";
    public bool Saved { get; set; }

    public string SignalText => new string('▮', Math.Clamp(Signal / 20, 0, 5))
                                + new string('▯', 5 - Math.Clamp(Signal / 20, 0, 5));

    public string Bars => $"{SignalText}  {Signal}%";
}

/// <summary>WiFi 扫描 / 连接（Windows 11 要求：管理员 + 位置服务开启，否则拿不到列表）</summary>
public static class WifiOps
{
    public static async Task<bool> LocationOnAsync()
    {
        var (rc, outText) = await Proc.CmdAsync(
            "powershell -NoProfile -NonInteractive -Command \"(Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\location' -ErrorAction SilentlyContinue).Value\"",
            20);
        return rc == 0 && outText.Contains("Allow", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<HashSet<string>> SavedProfilesAsync()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var (_, outText) = await Proc.CmdAsync("netsh wlan show profiles", 25);
        foreach (var line in outText.Split('\n'))
        {
            var t = line.Trim();
            var i = t.IndexOf(':');
            if (i <= 0) continue;
            var key = t[..i].Trim();
            if (!key.Contains("配置文件") && !key.Contains("Profile")) continue;
            var name = t[(i + 1)..].Trim();
            if (name.Length > 0) set.Add(name);
        }
        if (set.Count == 0)
        {
            // 兜底：所有 ": xxx" 后面那截都收进来（英文/中文开关都兼容）
            foreach (var line in outText.Split('\n'))
            {
                var t = line.Trim();
                if (!t.Contains(':')) continue;
                var name = t.Split(':', 2)[1].Trim();
                if (name.Length > 0 && !name.StartsWith(' ') && !t.StartsWith("<")) set.Add(name);
            }
        }
        return set;
    }

    public static async Task<(List<WifiNetwork> List, string Message)> ScanAsync()
    {
        var list = new List<WifiNetwork>();
        var saved = await SavedProfilesAsync();

        var (rc, outText) = await Proc.CmdAsync("netsh wlan show networks mode=bssid", 30);

        if (outText.Contains("location", StringComparison.OrdinalIgnoreCase)
            || outText.Contains("位置", StringComparison.Ordinal)
            || outText.Contains("elevation", StringComparison.OrdinalIgnoreCase))
        {
            return (list, "读不到 WiFi 列表：需要「管理员权限 + 位置服务开启」");
        }

        WifiNetwork? cur = null;
        int idx = 0;
        foreach (var raw in outText.Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;

            // SSID 行：SSID 1 : 名字
            var mSsid = Regex.Match(t, @"^SSID\s+\d+\s*:\s*(.*)$", RegexOptions.IgnoreCase);
            if (mSsid.Success)
            {
                var name = mSsid.Groups[1].Value.Trim();
                cur = new WifiNetwork { Ssid = name, Saved = saved.Contains(name) };
                list.Add(cur);
                idx = 0;
                continue;
            }
            if (cur == null) continue;

            var m = Regex.Match(t, @"^(.*?)\s*:\s*(.*)$");
            if (!m.Success) continue;
            var key = m.Groups[1].Value.Trim();
            var val = m.Groups[2].Value.Trim();

            if (key.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
            {
                idx++;
                var mSig = Regex.Match(val, @"\((\d+)%\)");
                if (mSig.Success && int.TryParse(mSig.Groups[1].Value, out var sig) && sig > cur.Signal)
                    cur.Signal = sig;
                continue;
            }
            if (key.Contains("信号") || key.Equals("Signal", StringComparison.OrdinalIgnoreCase))
            {
                var mSig = Regex.Match(val, @"(\d+)%");
                if (mSig.Success && int.TryParse(mSig.Groups[1].Value, out var sig) && sig > cur.Signal)
                    cur.Signal = sig;
            }
            else if (key.Contains("身份验证") || key.Equals("Authentication", StringComparison.OrdinalIgnoreCase))
                cur.Auth = val;
            else if (key.Contains("加密") || key.Equals("Encryption", StringComparison.OrdinalIgnoreCase))
                cur.Encrypt = val;
        }

        list.Sort((a, b) => b.Signal.CompareTo(a.Signal));
        return (list, list.Count == 0 ? "没有扫到网络（位置服务关着的时候也会是空的）" : $"扫到 {list.Count} 个");
    }


    /// <summary>只列已保存的网络：不需要位置服务、不需要管理员（实测可连接）</summary>
    public static async Task<List<WifiNetwork>> SavedNetworksAsync()
    {
        var list = new List<WifiNetwork>();
        await Task.CompletedTask;
        var set = await SavedProfilesAsync();
        foreach (var name in set.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(name) || name == "<None>") continue;
            list.Add(new WifiNetwork { Ssid = name, Saved = true, Signal = 0, Auth = "已保存", Encrypt = "" });
        }
        return list;
    }

    public static async Task<(bool Ok, string Message)> DisconnectAsync()
    {
        var (rc, outText) = await Proc.CmdAsync("netsh wlan disconnect", 20);
        AppPaths.Log($"断开 WiFi rc={rc}: {outText.Trim()}");
        return (rc == 0, outText.Trim());
    }

    public static async Task<(bool Ok, string Message)> ConnectAsync(WifiNetwork net, string? password)
    {
        if (net.Saved || string.IsNullOrEmpty(password))
        {
            var (rc1, o1) = await Proc.CmdAsync($"netsh wlan connect name=\"{net.Ssid}\"", 30);
            AppPaths.Log($"连接 {net.Ssid}（已有配置）rc={rc1}: {o1.Trim()}");
            return (rc1 == 0, o1.Trim());
        }

        // 新网络：先建配置再连
        var xml = $"""
<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
  <name>{XmlEscape(net.Ssid)}</name>
  <SSIDConfig><SSID><name>{XmlEscape(net.Ssid)}</name></SSID></SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>auto</connectionMode>
  <MSM>
    <security>
      <authEncryption>
        <authentication>WPA2PSK</authentication>
        <encryption>AES</encryption>
        <useOneX>false</useOneX>
      </authEncryption>
      <sharedKey>
        <keyType>passPhrase</keyType>
        <protected>false</protected>
        <keyMaterial>{XmlEscape(password)}</keyMaterial>
      </sharedKey>
    </security>
  </MSM>
</WLANProfile>
""";
        AppPaths.Ensure();
        var file = Path.Combine(AppPaths.DataDir, "wifi-profile.xml");
        File.WriteAllText(file, xml, new UTF8Encoding(false));

        var (rcA, oA) = await Proc.CmdAsync($"netsh wlan add profile filename=\"{file}\" user=current", 30);
        if (rcA != 0)
        {
            AppPaths.Log($"添加配置失败 {net.Ssid}: {oA}");
            return (false, oA.Trim());
        }

        var (rc2, o2) = await Proc.CmdAsync($"netsh wlan connect name=\"{net.Ssid}\"", 30);
        AppPaths.Log($"连接 {net.Ssid}（新建配置）rc={rc2}: {o2.Trim()}");
        try { File.Delete(file); } catch { }
        return (rc2 == 0, o2.Trim());
    }

    static string XmlEscape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");
}
