using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetPanel.Services;

/// <summary>校园网账号信息（密码只在本机加密保存，不上传、不写日志）</summary>
public sealed class CampusCreds
{
    public string Username { get; set; } = "";
    public string PasswordEnc { get; set; } = "";     // DPAPI 密文（Base64）
    public string Interface { get; set; } = "eno1";
    public string UserType { get; set; } = "unicom";   // unicom / cmcc / telecom / 空（校园网）
}

/// <summary>校园网（srun）登录/注销：写远端 Config.yaml + 调 bitsrun / 门户接口</summary>
public static class CampusLogin
{
    private static string CredFile => Path.Combine(AppPaths.DataDir, "campus.json");

    // ---------------- 本机凭据 ----------------
    public static CampusCreds LoadCreds()
    {
        try
        {
            if (File.Exists(CredFile))
                return JsonSerializer.Deserialize<CampusCreds>(File.ReadAllText(CredFile)) ?? new CampusCreds();
        }
        catch { }
        return new CampusCreds();
    }

    public static void SaveCreds(string user, string password, string iface, string userType = "unicom")
    {
        var c = new CampusCreds
        {
            Username = user.Trim(),
            Interface = string.IsNullOrWhiteSpace(iface) ? "eno1" : iface,
            UserType = userType,
            PasswordEnc = string.IsNullOrEmpty(password) ? "" : Dpapi.Protect(password),
        };
        try { File.WriteAllText(CredFile, JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { AppPaths.Log("[校园网] 保存账号失败: " + ex.Message); }
    }

    public static string UnprotectPassword(CampusCreds c)
    {
        if (string.IsNullOrEmpty(c.PasswordEnc)) return "";
        try { return Dpapi.Unprotect(c.PasswordEnc); } catch { return ""; }
    }

    // ---------------- 远端 Config.yaml ----------------
    public static async Task<string> ReadRemoteConfigAsync(RemoteConfig rc)
    {
        var (code, outp, _) = await Remote.RunAsync("ssh.exe",
            $"-o ConnectTimeout=4 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o NumberOfPasswordPrompts=0 " +
            $"-p {rc.Port} {rc.Target} \"cat ~/Config.yaml 2>/dev/null\"", 15);
        return code == 0 ? outp : "";
    }

    /// <summary>在原有 YAML 上只替换 username / password，其余字段（域名、enc、acid）原样保留</summary>
    public static string PatchYaml(string yaml, string user, string password, string userType = null)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return yaml;

        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        bool gotUser = false, gotPass = false, gotType = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var m = Regex.Match(lines[i], @"^(\s*username\s*:\s*).*$");
            if (m.Success) { lines[i] = m.Groups[1].Value + "\"" + user + "\""; gotUser = true; continue; }

            var mp = Regex.Match(lines[i], @"^(\s*password\s*:\s*).*$");
            if (mp.Success) { lines[i] = mp.Groups[1].Value + "\"" + password + "\""; gotPass = true; continue; }

            if (userType != null)
            {
                var mt = Regex.Match(lines[i], @"^(\s*user_type\s*:\s*).*$");
                if (mt.Success) { lines[i] = mt.Groups[1].Value + (userType == "" ? "\"\"" : userType); gotType = true; }
            }
        }

        if (!gotUser) lines.Add($"    username: \"{user}\"");
        if (!gotPass) lines.Add($"    password: \"{password}\"");
        if (userType != null && !gotType) lines.Add($"    user_type: {(userType == "" ? "\"\"" : userType)}");
        return string.Join("\n", lines);
    }

    private static async Task<(bool Ok, string Msg)> WriteRemoteConfigAsync(RemoteConfig rc, string yaml)
    {
        var (code, _, err) = await Remote.RunWithInputAsync("ssh.exe",
            $"-o ConnectTimeout=4 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o NumberOfPasswordPrompts=0 " +
            $"-p {rc.Port} {rc.Target} \"cat > ~/Config.yaml && chmod 600 ~/Config.yaml\"", yaml, 20);

        if (code == 0) return (true, "已写入远端 Config.yaml");
        return (false, string.IsNullOrWhiteSpace(err) ? "写远端配置失败" : err.Trim().Split('\n')[0]);
    }

    // ---------------- 登录 ----------------

    /// <summary>校园网地址段判断：只有 10.149/16、10.150/16 才可能在做校园网认证。</summary>
    public static bool IsCampusIp(string ip)
        => !string.IsNullOrWhiteSpace(ip)
           && (ip.StartsWith("10.149.") || ip.StartsWith("10.150."));

    /// <summary>预检：这个网卡现在是不是校园网环境。只读，不发认证、不改配置。</summary>
    public static async Task<(bool IsCampus, string Ip, string Reason)> CheckCampusAsync(RemoteConfig rc, string iface)
    {
        if (string.IsNullOrWhiteSpace(iface)) iface = "eno1";
        await Remote.ResolveAsync(rc);
        var (code, outp, err) = await Remote.RunAsync("ssh.exe",
            $"-o ConnectTimeout=4 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o NumberOfPasswordPrompts=0 " +
            $"-p {rc.Port} {rc.Target} \"ip -4 -br addr show dev {iface} 2>/dev/null\"", 10);
        var line = (outp ?? "").Split('\n').FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";
        var ip = System.Text.RegularExpressions.Regex.Match(line, @"\b(\d+\.\d+\.\d+\.\d+)\b").Groups[1].Value;
        if (string.IsNullOrWhiteSpace(ip))
            // 连不上远端 = 预检本身没跑通，放行（后续登录会自己快速报错），不因为预检失败拦人
            if (code != 0)
                return (true, "", $"预检没跑通（{(err ?? "").Split('\n').FirstOrDefault()?.Trim()}），直接尝试");
            return (false, "", $"网卡 {iface} 没有 IPv4 地址（没插线 / 没连通）");
        if (IsCampusIp(ip))
            return (true, ip, "");
        return (false, ip, $"网卡 {iface} 的地址是 {ip}，不在校园网段（10.149 / 10.150）");
    }

    public static async Task<(bool Ok, string Msg)> LoginAsync(string user, string password, string iface, string userType = "unicom")
    {
        var rc = RemoteConfig.Load();
        if (!rc.IsConfigured) return (false, "还没配置远端，去「连接」页填地址");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password)) return (false, "账号或密码是空的");

        await Remote.ResolveAsync(rc);

        // 预检：不是校园网就直接返回，绝不写 Config.yaml
        var pre = await CheckCampusAsync(rc, iface);
        if (!pre.IsCampus)
        {
            AppPaths.Log($"[校园网] 预检跳过：{pre.Reason}");
            return (false, $"当前不是校园网，已跳过（配置未改动）：{pre.Reason}");
        }

        var old = await ReadRemoteConfigAsync(rc);
        if (string.IsNullOrWhiteSpace(old)) return (false, "远端读不到 ~/Config.yaml，先跑一次部署");

        var dm0 = Regex.Match(old, @"domain\s*:\s*([^\s""]+)").Groups[1].Value;

        var yaml = PatchYaml(old, user.Trim(), password, userType);
        var (wOk, wMsg) = await WriteRemoteConfigAsync(rc, yaml);
        if (!wOk) return (false, wMsg);

        // bitsrun 一次 = 检查并登录一次；失败就等 3 秒再试一次（刚注销完门户可能还没腾出会话）
        string text = "", firstLine = "";
        int code = 0;
        bool online = false;
        string wanIp = "";

        for (int attempt = 1; attempt <= 2 && !online; attempt++)
        {
            var (c, outp, err) = await Remote.RunAsync("ssh.exe",
                $"-o ConnectTimeout=5 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o NumberOfPasswordPrompts=0 " +
                $"-p {rc.Port} {rc.Target} \"~/bitsrun -config ~/Config.yaml -interface {iface} 2>&1\"", 45);

            code = c;
            text = (outp + "\n" + err).Trim();
            firstLine = text.Split('\n').FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";
            AppPaths.Log($"[校园网] bitsrun 第 {attempt} 次 退出码={code} 网卡={iface}\n{text}");

            // 不信文字，也不信被门户劫持的网页：问门户“这个 IP 上是不是我”
            (online, wanIp) = await QueryOnlineAsync(rc, dm0, user.Trim());
            AppPaths.Log($"[校园网] 门户查询：online={online} wanIp={wanIp}");

            if (!online && attempt == 1) await Task.Delay(3000);
        }

        if (online)
            return (true, string.IsNullOrWhiteSpace(firstLine)
                ? $"登录成功，门户确认在线（{wanIp}）"
                : $"门户确认在线（{wanIp}） · {firstLine}");

        var lower = text.ToLowerInvariant();
        if (lower.Contains("password") || lower.Contains("login_error") || lower.Contains("密码"))
            return (false, "认证被拒，门户原文：\n" + text);
        return (false, $"两次都没登上，门户查不到 {wanIp} 的会话。bitsrun 原文：\n" + text);
    }

    /// <summary>权威判定：问门户 rad_user_info，看这个 IP 上是不是本账号在线</summary>
    private static async Task<(bool Online, string Ip)> QueryOnlineAsync(RemoteConfig rc, string domain, string user)
    {
        var (c1, ipOut, _) = await Remote.RunAsync("ssh.exe",
            $"-o ConnectTimeout=4 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o NumberOfPasswordPrompts=0 " +
            $"-p {rc.Port} {rc.Target} \"ip -4 route get 1.1.1.1 2>/dev/null\"", 12);
        var ip = Regex.Match(ipOut, @"src\s+(\d+\.\d+\.\d+\.\d+)").Groups[1].Value;
        if (ip == "") return (false, "");

        if (string.IsNullOrWhiteSpace(domain)) return (false, ip);

        var (_, body, _) = await Remote.RunAsync("ssh.exe",
            $"-o ConnectTimeout=4 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o NumberOfPasswordPrompts=0 " +
            $"-p {rc.Port} {rc.Target} \"curl -s -m 6 'http://{domain}/cgi-bin/rad_user_info?ip={ip}'\"", 15);

        var t = body.Trim();
        // 在线会回 "账号,上线时间,...,IP,..."；不在线会回 login_error / not_online 之类
        var online = !t.StartsWith("login_error") && t.Split(',').Length > 3
                     && t.Split(',')[0].Trim() == user;
        return (online, ip);
    }

    // ---------------- 注销（门户同款签名：sha1(time+user+ip+1+time)） ----------------
    public static async Task<(bool Ok, string Msg)> LogoutAsync(string user)
    {
        var rc = RemoteConfig.Load();
        if (!rc.IsConfigured) return (false, "还没配置远端");

        await Remote.ResolveAsync(rc);

        var yaml = await ReadRemoteConfigAsync(rc);
        var domain = Regex.Match(yaml, @"domain\s*:\s*([^\s""]+)").Groups[1].Value;
        var userInCfg = Regex.Match(yaml, @"username\s*:\s*[""']?([^""'\s]+)").Groups[1].Value;
        if (string.IsNullOrWhiteSpace(domain)) return (false, "远端 Config.yaml 里没读到域名");

        var u = string.IsNullOrWhiteSpace(user) ? userInCfg : user.Trim();
        if (string.IsNullOrWhiteSpace(u)) return (false, "不知道账号，先在上面填账号");

        // 远端当前 WAN IP
        var (c2, ipOut, _) = await Remote.RunAsync("ssh.exe",
            $"-o ConnectTimeout=4 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o NumberOfPasswordPrompts=0 " +
            $"-p {rc.Port} {rc.Target} \"ip -4 route get 1.1.1.1 2>/dev/null\"", 12);
        var ip = Regex.Match(ipOut, @"src\s+(\d+\.\d+\.\d+\.\d+)").Groups[1].Value;
        if (string.IsNullOrEmpty(ip)) return (false, "读不到远端 WAN IP，可能没连上网");
        if (!IsCampusIp(ip))
        {
            AppPaths.Log($"[校园网] 注销跳过：当前出口 {ip} 不是校园网");
            return (false, $"当前出口不是校园网（{ip}），无需注销。");
        }

        // sign 在远端算（时间戳必须和请求一致，避免两台机器时差）
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sign = Sha1Hex($"{ts}{u}{ip}1{ts}");
        var url = $"http://{domain}/cgi-bin/rad_user_dm?ip={ip}&username={Uri.EscapeDataString(u)}" +
                  $"&time={ts}&unbind=1&sign={sign}&callback=cb";

        var (c3, body, err3) = await Remote.RunAsync("ssh.exe",
            $"-o ConnectTimeout=4 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o NumberOfPasswordPrompts=0 " +
            $"-p {rc.Port} {rc.Target} \"curl -s -m 10 '{url}'\"", 20);

        var text = (body + " " + err3).Trim();
        if (c3 != 0 && string.IsNullOrWhiteSpace(body))
            return (false, string.IsNullOrWhiteSpace(text) ? "注销请求发不出去" : text.Split('\n')[0]);

        var lower = text.ToLowerInvariant();
        if (lower.Contains("sign_error"))
            return (false, "门户拒绝签名（sign_error），这版的算法对不上");
        if (lower.Contains("\"ok\"") || lower.Contains("logout"))
            return (true, $"已注销（{ip}）");
        if (lower.Contains("not online"))
            return (true, $"本来就未在线（{ip}）");

        return (false, string.IsNullOrWhiteSpace(text) ? "门户没回内容" : text.Split('\n')[0]);
    }

    private static string Sha1Hex(string s)
    {
        var b = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(b).ToLowerInvariant();
    }

    // ---------------- 状态（只读，安全） ----------------
    public static async Task<string> StatusAsync()
    {
        var rc = RemoteConfig.Load();
        if (!rc.IsConfigured) return "远端未配置";

        await Remote.ResolveAsync(rc);
        var (code, outp, err) = await Remote.RunAsync("ssh.exe",
            $"-o ConnectTimeout=4 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o NumberOfPasswordPrompts=0 " +
            $"-p {rc.Port} {rc.Target} \"ip -4 -br addr show 2>/dev/null | head -6; " +
            "echo ---; curl -s -m 5 -o /dev/null -w '%{http_code}' http://connect.rom.miui.com/generate_204\"", 20);

        if (code != 0) return "远端连不上：" + (string.IsNullOrWhiteSpace(err) ? "超时" : err.Split('\n')[0].Trim());

        var parts = outp.Split("---");
        var addrs = parts[0].Trim().Replace("\n", "　");
        var http = parts.Length > 1 ? parts[1].Trim() : "?";
        var yml = await ReadRemoteConfigAsync(rc);
        var dm = Regex.Match(yml, @"domain\s*:\s*([^\s""]+)").Groups[1].Value;
        var un = Regex.Match(yml, @"username\s*:\s*[""']?([^""'\s]+)").Groups[1].Value;
        var (onl, wanIp) = await QueryOnlineAsync(rc, dm, un);
        if (!string.IsNullOrWhiteSpace(wanIp) && !IsCampusIp(wanIp))
            return $"当前出口不是校园网（{wanIp}），认证不适用\n{addrs}";
        var online = onl ? $"在线（门户确认，{wanIp}）" : $"未在线（门户查不到 {wanIp} 的会话）";
        return $"{online}\n{addrs}";
    }
}

/// <summary>Windows DPAPI：只允许当前用户解密，不写明文到磁盘</summary>
internal static class Dpapi
{
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetPanel.Campus.v1");

    public static string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var inBlob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        var entBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = Marshal.AllocHGlobal(Entropy.Length) };
        try
        {
            Marshal.Copy(data, 0, inBlob.pbData, data.Length);
            Marshal.Copy(Entropy, 0, entBlob.pbData, Entropy.Length);
            if (!CryptProtectData(ref inBlob, "NetPanel", ref entBlob, IntPtr.Zero, IntPtr.Zero, 0, out var outBlob))
                return "";
            try
            {
                var buf = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, buf, 0, outBlob.cbData);
                return Convert.ToBase64String(buf);
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            Marshal.FreeHGlobal(entBlob.pbData);
        }
    }

    public static string Unprotect(string b64)
    {
        var data = Convert.FromBase64String(b64);
        var inBlob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        var entBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = Marshal.AllocHGlobal(Entropy.Length) };
        try
        {
            Marshal.Copy(data, 0, inBlob.pbData, data.Length);
            Marshal.Copy(Entropy, 0, entBlob.pbData, Entropy.Length);
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, 0, out var outBlob))
                return "";
            try
            {
                var buf = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, buf, 0, outBlob.cbData);
                return Encoding.UTF8.GetString(buf);
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            Marshal.FreeHGlobal(entBlob.pbData);
        }
    }
}
