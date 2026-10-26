using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NetPanel.Services;

/// <summary>部署参数。密码只在内存里，不落盘。</summary>
public sealed class DeployOptions
{
    public string Host = "";
    public int Port = 22;
    public string User = "";
    public string SshPassword = "";
    public string SudoPassword = "";
    public string Topology = "A";
    public string Ssid = "Gray-Router";
    public string WifiPass = "";
    public string LanIp = "192.168.50.1";
    public string Harden = "none";
    public bool DryRun;
    public string RemoteDir = "/tmp/grayrouter-deploy";

    public string Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)) return "没填地址";
        if (string.IsNullOrWhiteSpace(User)) return "没填用户名";
        if (string.IsNullOrWhiteSpace(SshPassword)) return "没填 SSH 密码";
        if (string.IsNullOrWhiteSpace(SudoPassword)) return "没填 sudo 密码";
        if (!DryRun && WifiPass.Length < 8) return "热点密码至少 8 位";
        if (string.IsNullOrWhiteSpace(Ssid)) return "没填热点名";
        return "";
    }
}

public sealed class CheckItem
{
    public string Name = "";
    public bool Ok;
    public string Detail = "";
}

public sealed class DeployResult
{
    public bool Ok;
    public string Message = "";
    public bool Dropped;                   // 安装过程中掉过线
    public List<CheckItem> Checks = new();
}

/// <summary>
/// 一键部署：SFTP 上传内置包 → 远端解压 → sudo 免交互安装 → 自检。
/// 掉线（netplan/NetworkManager 被重启）视为正常，重连后继续。
/// </summary>
public sealed class SshDeploy
{
    private readonly Action<string> _log;
    private readonly Action<int, int, string>? _step;

    public SshDeploy(Action<string> log, Action<int, int, string>? step = null)
    {
        _log = log;
        _step = step;
    }

    private static ConnectionInfo Conn(DeployOptions o)
    {
        var pw = new PasswordAuthenticationMethod(o.User, o.SshPassword);
        var ki = new KeyboardInteractiveAuthenticationMethod(o.User);
        ki.AuthenticationPrompt += (_, e) =>
        {
            foreach (var p in e.Prompts) p.Response = o.SshPassword;
        };
        var ci = new ConnectionInfo(o.Host, o.Port, o.User, pw, ki)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        return ci;
    }

    private SshClient Connect(DeployOptions o)
    {
        var c = new SshClient(Conn(o));
        c.Connect();
        return c;
    }

    // ---------------------------------------------------------------- 主流程

    public async Task<DeployResult> RunAsync(DeployOptions o, CancellationToken ct)
    {
        var res = new DeployResult();
        var bad = o.Validate();
        if (bad != "") { res.Message = bad; return res; }

        _step?.Invoke(1, 7, "连接远端");
        SshClient? cli = null;
        try
        {
            cli = await Task.Run(() => Connect(o), ct);
            _log($"√ 已连接 {o.User}@{o.Host}:{o.Port}");
        }
        catch (SshAuthenticationException)
        {
            res.Message = "SSH 认证失败：用户名或密码不对";
            return res;
        }
        catch (Exception ex)
        {
            res.Message = "连不上远端：" + ex.Message;
            return res;
        }

        try
        {
            // ---- 2. 上传内置部署包
            _step?.Invoke(2, 7, "上传部署包");
            var pkgLen = await UploadAsync(cli, o, ct);
            _log($"√ 已上传 {pkgLen / 1024 / 1024.0:F1} MB 到 {o.RemoteDir}/pkg.zip");

            // ---- 3. 解压
            _step?.Invoke(3, 7, "解压");
            var unzip = await ExecAsync(cli, BuildUnzipCmd(o));
            if (unzip.code != 0)
            {
                res.Message = "解压失败：" + Tail(unzip.output, 3);
                return res;
            }
            _log("√ 解压完成");

            // ---- 4. 写安装参数（单独脚本，命令行里不出现密码）
            _step?.Invoke(4, 7, "准备安装参数");
            WriteRunScript(cli, o);
            _log("√ 参数已写入远端临时脚本（600 权限，装完删除）");

            // ---- 5. 执行安装（sudo 密码经 stdin 喂，不进命令行）
            _step?.Invoke(5, 7, "执行安装");
            var (code, output) = await RunSudoAsync(cli, o, $"bash {o.RemoteDir}/run.sh", ct, 1200);

            var dropped = code == int.MinValue;
            res.Dropped = dropped;
            if (dropped)
            {
                _log("! 安装过程中连接断开（多半是网络在切换），等它回来 …");
                cli = await WaitReconnectAsync(o, ct, 180);
                if (cli == null)
                {
                    res.Message = "安装后等不到远端回来（超过 3 分钟）。可能是网卡没起来，看部署窗口里的日志。";
                    return res;
                }
                _log("√ 远端已回来，继续自检");
            }
            else if (code == -3)
            {
                res.Message = "本机侧连接受损（看日志里 ! 开头那行）";
                return res;
            }
            else if (code == -2)
            {
                res.Message = "安装卡住或 sudo 密码不对（看日志里 ! 开头那行）";
                return res;
            }
            else if (code != 0)
            {
                res.Message = $"安装脚本退出码 {code}" + (Tail(output, 4) is { Length: > 0 } t ? "：" + t : "");
                return res;
            }
            else
            {
                _log("√ 安装脚本跑完（退出码 0）");
            }

            // ---- 6. 自检
            _step?.Invoke(6, 7, "自检");
            var chk = await RunSudoAsync(cli, o, $"bash {o.RemoteDir}/run.sh --verify", ct, 300, quiet: true);
            res.Checks = ParseChecks(chk.output);

            // ---- 7. 收尾：删临时文件
            _step?.Invoke(7, 7, "收尾");
            try
            {
                await ExecAsync(cli, $"rm -rf {o.RemoteDir}");
                _log("√ 已清理远端临时文件");
            }
            catch { /* 清理失败不影响结果 */ }

            var fails = res.Checks.Count(c => !c.Ok);
            res.Ok = res.Checks.Count > 0 && fails == 0;
            res.Message = res.Checks.Count == 0
                ? "部署跑完了，但自检没返回结果（看日志里 ! 那行）"
                : res.Ok
                    ? "部署完成，自检全部通过"
                    : $"部署跑完了，但自检有 {fails} 项没过（上面标 × 的）";
            return res;
        }
        catch (OperationCanceledException)
        {
            res.Message = "已取消";
            return res;
        }
        catch (Exception ex)
        {
            res.Message = "部署中断：" + ex.Message;
            return res;
        }
        finally
        {
            try { cli?.Dispose(); } catch { }
        }
    }

    // ---------------------------------------------------------------- 各步骤

    private async Task<long> UploadAsync(SshClient cli, DeployOptions o, CancellationToken ct)
    {
        using var sftp = new SftpClient(Conn(o));
        sftp.Connect();

        try { sftp.CreateDirectory(o.RemoteDir); } catch { /* 已存在 */ }

        await using var fs = Assembly.GetExecutingAssembly().GetManifestResourceStream("deploy.zip")
                             ?? throw new InvalidOperationException("程序里没有内置部署包");
        var total = fs.Length;
        var last = -1;

        var path = o.RemoteDir + "/pkg.zip";
        try { sftp.DeleteFile(path); } catch { }

        sftp.UploadFile(fs, path, true, ul =>
        {
            ct.ThrowIfCancellationRequested();
            var pct = (int)((double)ul * 100.0 / total);
            if (pct >= last + 5)  // 每 5% 报一次，别刷屏
            {
                last = pct;
                _log($"  上传 {pct,3}%  ({ul / 1024 / 1024.0:F1}/{total / 1024 / 1024.0:F1} MB)");
            }
        });
        await Task.CompletedTask;
        return total;
    }

    private static string BuildUnzipCmd(DeployOptions o) =>
        $"rm -rf {o.RemoteDir}/pkg && mkdir -p {o.RemoteDir}/pkg && " +
        $"(cd {o.RemoteDir}/pkg && python3 -m zipfile -e ../pkg.zip . 2>/dev/null || " +
        $"(cd {o.RemoteDir}/pkg && unzip -q ../pkg.zip)) && " +
        $"find {o.RemoteDir}/pkg -maxdepth 2 -name install.sh";

    /// <summary>把安装参数写成远端脚本：sudo 命令行里看不到密码。</summary>
    private void WriteRunScript(SshClient cli, DeployOptions o)
    {
        using var sftp = new SftpClient(Conn(o));
        sftp.Connect();

        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -o pipefail");
        sb.AppendLine("# 包里有一层顶级目录（GrayRouter-x.y.z/），自动找 install.sh 的真实位置");
        sb.AppendLine("ROOT=$(dirname \"$(find " + o.RemoteDir + "/pkg -maxdepth 3 -name install.sh | head -1)\")");
        sb.AppendLine("if [ -z \"$ROOT\" ] || [ ! -f \"$ROOT/install.sh\" ]; then echo \"找不到 install.sh\"; exit 2; fi");
        sb.AppendLine("chmod +x \"$ROOT/install.sh\" \"$ROOT\"/bin/*.sh \"$ROOT\"/bin/ua3f \"$ROOT\"/*.sh 2>/dev/null");
        sb.AppendLine("if [ \"$1\" = \"--verify\" ]; then exec bash \"$ROOT/install.sh\" --verify; fi");
        sb.Append("exec bash \"$ROOT/install.sh\" --yes --noninteractive");
        sb.Append(" --topology=").Append(Q(o.Topology));
        sb.Append(" --ssid=").Append(Q(o.Ssid));
        sb.Append(" --password=").Append(Q(o.WifiPass));
        sb.Append(" --lan-ip=").Append(Q(o.LanIp));
        sb.Append(" --harden=").Append(Q(o.Harden));
        if (o.DryRun) sb.Append(" --dry-run");
        sb.AppendLine();

        var path = o.RemoteDir + "/run.sh";
        try { sftp.DeleteFile(path); } catch { }
        // Linux 上必须是 LF：Windows 的 AppendLine 会写 CRLF，bash 会报 "invalid option"
        var script = sb.ToString().Replace("\r\n", "\n");
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(script)))
            sftp.UploadFile(ms, path, true);
        try { ExecAsync(cli, $"chmod 600 {path}").GetAwaiter().GetResult(); } catch { }
    }

    private static string Q(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

    /// <summary>执行一条命令（不需要 sudo 的）。</summary>
    private Task<(int code, string output)> ExecAsync(SshClient cli, string cmd) => Task.Run(() =>
    {
        using var c = cli.CreateCommand(cmd);
        c.CommandTimeout = TimeSpan.FromSeconds(60);
        var outp = c.Execute();
        return (c.ExitStatus ?? -1, outp + c.Error);
    });

    /// <summary>
    /// 跑需要 sudo 的命令：密码写进远端 600 临时文件，用 `sudo -S < 文件` 喂进去，
    /// 跑完立刻删。不用交互终端（回显/时序都不可靠），命令行里也没有明文密码。
    /// 掉线返回 int.MinValue；本机侧异常返回 -3；卡住/密码错返回 -2。
    /// </summary>
    private async Task<(int code, string output)> RunSudoAsync(
        SshClient cli, DeployOptions o, string cmd, CancellationToken ct, int timeoutSec, bool quiet = false)
    {
        var pwPath = o.RemoteDir + "/.pw";
        try
        {
            using (var sftp = new SftpClient(Conn(o)))
            {
                sftp.Connect();
                try { sftp.DeleteFile(pwPath); } catch { }
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(o.SudoPassword + "\n"));
                sftp.UploadFile(ms, pwPath, true);
                try { ExecAsync(cli, $"chmod 600 {pwPath}").GetAwaiter().GetResult(); } catch { }
            }
        }
        catch (Exception ex)
        {
            _log("  ! 写临时密码文件失败：" + ex.Message);
            return (-3, "");
        }

        var sb = new StringBuilder();
        try
        {
            using var c = cli.CreateCommand($"sudo -S -p '' {cmd} < {pwPath} 2>&1; echo __GRC=$?");
            c.CommandTimeout = TimeSpan.FromSeconds(timeoutSec);
            _log("  命令已下发（密码走 stdin）");

            var task = Task.Run(() =>
            {
                using var reader = new StringReader(c.Execute());
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    ct.ThrowIfCancellationRequested();
                    var t = Clean(line);
                    if (t.Length == 0 || t == o.SudoPassword) continue;
                    sb.AppendLine(t);
                    if (!quiet) _log("  " + t);
                    OnLine(t);
                    if (t.Contains("Sorry, try again") || t.Contains("Authentication failed")
                        || t.Contains("Authentication required") || t.Contains("sudo: a password is required"))
                        throw new SudoBadPassword();
                }
                return c.ExitStatus ?? -1;
            }, ct);

            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSec), ct));
            if (done != task)
            {
                try { c.CancelAsync(); } catch { }
                _log($"  ! 超过 {timeoutSec} 秒没跑完");
                return (-2, sb.ToString());
            }
            return (await task, sb.ToString());
        }
        catch (SudoBadPassword)
        {
            _log("  ! sudo 密码不对");
            return (-2, sb.ToString());
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshConnectionException
                                   || ex is System.Net.Sockets.SocketException
                                   || ex is ObjectDisposedException)
        {
            _log("  ! 连接断了：" + ex.Message);
            return (int.MinValue, sb.ToString());
        }
        catch (Exception ex)
        {
            _log("  ! 执行出错：" + ex.GetType().Name + " " + ex.Message);
            return (-3, sb.ToString());
        }
        finally
        {
            try
            {
                using var sftp = new SftpClient(Conn(o));
                sftp.Connect();
                sftp.DeleteFile(pwPath);
            }
            catch { /* 删不掉也没关系，下次覆盖 */ }
        }
    }

    private sealed class SudoBadPassword : Exception { }

    /// <summary>从输出里认出【3/7 解压】这类步骤行，转成进度。</summary>
    private void OnLine(string s)
    {
        var m = Regex.Match(s, @"【(\d+)/(\d+)\s*([^】]*)】");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var i) && int.TryParse(m.Groups[2].Value, out var n))
            _step?.Invoke(Math.Min(n, 6), 7, m.Groups[3].Value.Trim());
    }

    private static string Clean(string s)
    {
        s = Regex.Replace(s, "\u001b\\[[0-9;]*[A-Za-z]", "");   // ANSI
        s = Regex.Replace(s, "\\[[0-9;]*m", "");
        return s.TrimEnd();
    }

    private static string Tail(string s, int n)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" / ", lines.TakeLast(n).Select(x => x.Trim()));
    }

    private static List<CheckItem> ParseChecks(string output)
    {
        var list = new List<CheckItem>();
        foreach (var raw in output.Split('\n'))
        {
            var s = Clean(raw);
            if (!s.StartsWith("CHECK|", StringComparison.Ordinal)) continue;
            var p = s.Split('|');
            if (p.Length < 4) continue;
            list.Add(new CheckItem { Name = p[1], Ok = p[2] == "ok", Detail = p[3] });
        }
        return list;
    }

    /// <summary>等远端回来（重连），最多 waitSec 秒。</summary>
    private async Task<SshClient?> WaitReconnectAsync(DeployOptions o, CancellationToken ct, int waitSec)
    {
        var until = DateTime.UtcNow.AddSeconds(waitSec);
        var n = 0;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var c = await Task.Run(() => Connect(o), ct);
                return c;
            }
            catch
            {
                n++;
                if (n % 3 == 0) _log($"  还在等（{n * 4}s）…");
                await Task.Delay(4000, ct);
            }
        }
        return null;
    }
}
