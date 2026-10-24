using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NetPanel.Services;

/// <summary>远端软路由的连接配置（默认全空，由用户填；不预置任何个人信息）</summary>
public sealed class RemoteConfig
{
    public string Host { get; set; } = "";
    /// <summary>多个候选地址，逗号/空格分隔；Host 自动取第一个通的</summary>
    public string Hosts { get; set; } = "";
    public string User { get; set; } = "";
    public int Port { get; set; } = 22;
    /// <summary>auto / key / password</summary>
    public string Auth { get; set; } = "auto";
    public string LastPackage { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(User);
    [System.Text.Json.Serialization.JsonIgnore]
    public string Target => $"{User}@{Host}";

    /// <summary>所有候选地址（去重、去空）</summary>
    public List<string> HostList()
    {
        var raw = new List<string>();
        foreach (var part in (Hosts + "," + Host).Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var h = part.Trim();
            if (h != "" && !raw.Contains(h)) raw.Add(h);
        }
        return raw;
    }

    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static RemoteConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.RemoteFile))
                return JsonSerializer.Deserialize<RemoteConfig>(File.ReadAllText(AppPaths.RemoteFile)) ?? new();
        }
        catch (Exception ex) { AppPaths.Log("读取远程配置失败: " + ex.Message); }
        return new RemoteConfig();
    }

    public void Save()
    {
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(AppPaths.RemoteFile, JsonSerializer.Serialize(this, Opt), new UTF8Encoding(false));
        }
        catch (Exception ex) { AppPaths.Log("保存远程配置失败: " + ex.Message); }
    }
}

public static class Remote
{
    /// <summary>ssh 公共参数</summary>
    private static string BaseArgs(RemoteConfig cfg, bool batchMode)
    {
        var sb = new StringBuilder();
        sb.Append($"-o ConnectTimeout=4 -o StrictHostKeyChecking=accept-new -p {cfg.Port} ");
        if (batchMode) sb.Append("-o BatchMode=yes -o NumberOfPasswordPrompts=0 ");
        return sb.ToString();
    }

    /// <summary>不带认证地探一下 22 端口开不开（快、也不会触发密码框）</summary>
    public static async Task<bool> ProbeTcpAsync(string host, int port, int timeoutMs = 1200)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var task = tcp.ConnectAsync(host, port);
            var done = await Task.WhenAny(task, Task.Delay(timeoutMs));
            return done == task && tcp.Connected;
        }
        catch { return false; }
    }

    /// <summary>多地址：按顺序探，第一个通的设为当前地址。返回 null 表示都不通。</summary>
    public static async Task<string?> ResolveAsync(RemoteConfig cfg, Action<string>? log = null)
    {
        var hosts = cfg.HostList();
        if (hosts.Count == 0) { log?.Invoke("× 没填地址"); return null; }

        foreach (var h in hosts)
        {
            var ok = await ProbeTcpAsync(h, cfg.Port);
            log?.Invoke((ok ? "√ " : "× ") + $"{h}:{cfg.Port}" + (ok ? "  通" : "  连不上"));
            if (ok)
            {
                if (cfg.Host != h) { cfg.Host = h; cfg.Save(); }
                return h;
            }
        }
        return null;
    }

    /// <summary>弹一个真正的 SSH 交互终端（能敲命令）</summary>
    public static void OpenTerminal(RemoteConfig cfg, string title) =>
        StartConsole($"""
@echo off
title {title}
ssh.exe -t -o ConnectTimeout=8 -o StrictHostKeyChecking=accept-new -p {cfg.Port} {cfg.Target}
echo.
echo ==== 会话结束，按任意键关闭 ====
pause >nul
""");

    /// <summary>
    /// 把脚本写成临时 bat，再用 start 开一个新控制台跑它。
    /// 不这么绕的话，嵌套引号在 Windows 命令行里几乎必定被吃掉。
    /// </summary>
    private static void StartConsole(string script)
    {
        Directory.CreateDirectory(AppPaths.TmpDir);
        var bat = Path.Combine(AppPaths.TmpDir, $"netpanel-{Guid.NewGuid():N}.bat");
        // bat 必须 CRLF：LF 换行会让 cmd.exe 把整行拆碎（'192.168.50.1' 不是内部或外部命令）
        var crlf = script.Replace("\r\n", "\n").Replace("\n", "\r\n");
        File.WriteAllText(bat, crlf, Encoding.GetEncoding(936));
        try
        {
            foreach (var old in Directory.GetFiles(AppPaths.TmpDir, "netpanel-*.bat"))
                if (DateTime.Now - File.GetLastWriteTime(old) > TimeSpan.FromDays(1))
                    File.Delete(old);
        }
        catch { }

        var psi = new ProcessStartInfo("cmd.exe", $"/c start \"\" \"{bat}\"")
        {
            UseShellExecute = true,
            WorkingDirectory = AppPaths.BaseDir,
        };
        Process.Start(psi);
    }

    /// <summary>测试连接：只跑一条 echo，返回 (成功, 说明)</summary>
    public static async Task<(bool Ok, string Detail)> TestAsync(RemoteConfig cfg)
    {
        if (!cfg.IsConfigured) return (false, "还没填地址和用户名");

        // auto：先试密钥（不弹密码），失败就说明需要密码
        var (rc, outp, err) = await RunAsync("ssh.exe",
            $"{BaseArgs(cfg, true)}{cfg.Target} \"echo NETPANEL_OK\"");

        if (rc == 0 && outp.Contains("NETPANEL_OK"))
            return (true, "密钥登录可用");

        if (cfg.Auth == "password")
            return (true, "已设为密码登录：连接时会弹终端让你输密码");

        var msg = (err + " " + outp).Trim();
        if (msg.Contains("Permission denied") || msg.Contains("no such identity"))
            return (false, "连上了但没通过认证  →  可切到「密码登录」，或先 ssh-copy-id 配好密钥");
        if (msg.Contains("Connection refused"))
            return (false, "端口没开（SSH 没装或没启动？）");
        if (msg.Contains("timed out") || msg.Contains("Timeout"))
            return (false, "连不上：地址不对，或不在同一网络");
        return (false, string.IsNullOrWhiteSpace(msg) ? $"ssh 返回码 {rc}" : msg);
    }

    /// <summary>把本地目录上传到远端 /tmp/grayrouter-upload（存在则先清空）</summary>
    public static async Task<(bool Ok, string Msg)> UploadAsync(RemoteConfig cfg, string localDir)
    {
        if (!Directory.Exists(localDir)) return (false, "本地目录不存在：" + localDir);

        var remoteDir = "/tmp/grayrouter-upload";
        var (rc1, _, err1) = await RunAsync("ssh.exe",
            $"{BaseArgs(cfg, cfg.Auth != "password")}{cfg.Target} \"rm -rf {remoteDir} && mkdir -p {remoteDir}\"");
        if (rc1 != 0) return (false, "远端建目录失败：" + err1.Trim());

        var (rc2, _, err2) = await RunAsync("scp.exe",
            $"-r -o ConnectTimeout=4 -o StrictHostKeyChecking=accept-new -P {cfg.Port} " +
            $"\"{localDir}\" {cfg.Target}:{remoteDir}/");
        if (rc2 != 0) return (false, "上传失败：" + err2.Trim());
        return (true, remoteDir);
    }

    /// <summary>
    /// 在独立的控制台窗口里执行命令（sudo 要交互密码，必须有真终端）
    /// 程序不接触密码。
    /// </summary>
    public static void RunInConsoleWindow(RemoteConfig cfg, string remoteCommand, string title) =>
        StartConsole($"""
@echo off
title {title}
ssh.exe -t -o ConnectTimeout=8 -o StrictHostKeyChecking=accept-new -p {cfg.Port} {cfg.Target} "{remoteCommand}"
echo.
echo ==== 已完成，按任意键关闭 ====
pause >nul
""");

    public static async Task<(int Rc, string Out, string Err)> RunAsync(string exe, string args, int timeoutSec = 30)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            var waitTask = p.WaitForExitAsync();                 // ← 必须存下来，不能重复调用后比对象
            var done = await Task.WhenAny(waitTask, Task.Delay(timeoutSec * 1000));
            if (done != waitTask)
            {
                try { p.Kill(true); } catch { }
                return (-1, "", "超时");
            }
            return (p.ExitCode, await outTask, await errTask);
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }
}
