using System.Diagnostics;
using System.Text;

namespace NetPanel.Services;

public static class Proc
{
    /// <summary>执行外部命令，统一按 UTF-8 收输出（配合 chcp 65001）</summary>
    public static async Task<(int Code, string Output)> RunAsync(
        string file, string args, int timeoutSec = 25)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            using var p = new Process { StartInfo = psi };
            var sb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };

            if (!p.Start()) return (-1, "无法启动进程：" + file);

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                lock (sb) sb.AppendLine($"[超时 {timeoutSec}s]");
                return (-1, sb.ToString());
            }

            return (p.ExitCode, sb.ToString());
        }
        catch (Exception ex)
        {
            return (-1, "执行失败：" + ex.Message);
        }
    }

    /// <summary>跑一条 cmd 命令（先切 UTF-8 代码页，中文才不乱码）</summary>
    public static Task<(int Code, string Output)> CmdAsync(string command, int timeoutSec = 25)
        => RunAsync("cmd.exe", "/c chcp 65001>nul && " + command, timeoutSec);
}
