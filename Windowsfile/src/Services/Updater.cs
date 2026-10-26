using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NetPanel.Services;

/// <summary>面板自更新：查 GitHub Release → 下载 → 换文件 → 重启</summary>
public static class Updater
{
    private const string Owner = "ark2077nbjks";
    private const string Repo = "FJjx-Ubuntu-software-based-router";

    public sealed record ReleaseInfo(string Tag, string AssetUrl, long Size);

    public static string CurrentVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>查最新 Release（超时 15 秒；不弹认证框）</summary>
    public static async Task<(ReleaseInfo? Info, string Error)> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetPanel-Updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            if (!root.TryGetProperty("assets", out var assets))
                return (null, "Release 里没有附件");

            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                if (!name.Equals("NetPanel.exe", StringComparison.OrdinalIgnoreCase)) continue;
                var size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                var dl = a.TryGetProperty("browser_download_url", out var d) ? (d.GetString() ?? "") : "";
                return (new ReleaseInfo(tag, dl, size), "");
            }
            return (null, "Release 里没找到 NetPanel.exe");
        }
        catch (Exception ex)
        {
            return (null, "检查失败：" + ex.Message);
        }
    }

    /// <summary>tag（v1.33.2）是否比当前版本新</summary>
    public static bool IsNewer(string tag)
    {
        var clean = (tag ?? "").TrimStart('v', 'V');
        if (!Version.TryParse(clean, out var remote)) return false;
        var cur = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        return remote > new Version(cur.Major, cur.Minor, Math.Max(cur.Build, 0));
    }

    /// <summary>下载到 data\tmp\netpanel-new.exe，返回路径</summary>
    public static async Task<(bool Ok, string Msg, string Path)> DownloadAsync(ReleaseInfo info, Action<long, long>? progress = null)
    {
        var path = System.IO.Path.Combine(AppPaths.TmpDir, "netpanel-new.exe");
        try
        {
            Directory.CreateDirectory(AppPaths.TmpDir);
            if (File.Exists(path)) File.Delete(path);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetPanel-Updater");
            using var resp = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? info.Size;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(path))
            {
                var buf = new byte[81920];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    got += n;
                    progress?.Invoke(got, total);
                }
            }

            var len = new FileInfo(path).Length;
            if (info.Size > 0 && len != info.Size)
                return (false, $"下载大小不对（{len} / {info.Size}）", path);
            if (len < 200_000)
                return (false, $"文件太小（{len} 字节），不像 exe", path);

            return (true, $"下载完成 {len / 1024.0 / 1024.0:F1} MB", path);
        }
        catch (Exception ex)
        {
            return (false, "下载失败：" + ex.Message, path);
        }
    }

    /// <summary>起一个隐藏的 PowerShell：等本进程退出 → 复制 → 重启</summary>
    public static bool LaunchUpdater(string newExe, string targetExe, out string error)
    {
        error = "";
        try
        {
            var ps1 = System.IO.Path.Combine(AppPaths.TmpDir, "netpanel-update.ps1");
            var script = """
param([string]$src,[string]$dst,[int]$procId,[string]$log)
$ErrorActionPreference = 'SilentlyContinue'
function Note($m) { Add-Content -LiteralPath $log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $m) }
Note ("等待面板退出 pid=" + $procId)
for ($i = 0; $i -lt 120; $i++) {
    if (-not (Get-Process -Id $procId)) { break }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Milliseconds 700
for ($k = 1; $k -le 30; $k++) {
    try {
        Copy-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop
        Note ("第 " + $k + " 次替换成功，启动新版")
        Start-Process -FilePath $dst
        exit 0
    } catch {
        Note ("第 " + $k + " 次失败：" + $_.Exception.Message)
        Start-Sleep -Milliseconds 700
    }
}
Note '替换彻底失败，尝试拉起旧版'
Start-Process -FilePath $dst
exit 1
""";

            File.WriteAllText(ps1, script.Replace("\r\n", "\n"), new UTF8Encoding(false));

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\" " +
                $"-src \"{newExe}\" -dst \"{targetExe}\" -procId {Environment.ProcessId} " +
                $"-log \"{System.IO.Path.Combine(AppPaths.LogDir, "update.log")}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>当前 exe 的完整路径（单文件发布也准）</summary>
    public static string CurrentExePath()
    {
        var p = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return p;
        return System.IO.Path.Combine(AppContext.BaseDirectory, "NetPanel.exe");
    }
}
