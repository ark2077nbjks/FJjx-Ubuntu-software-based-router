using System.IO;
using System.Text;

namespace NetPanel.Services;

/// <summary>
/// 软件数据目录：默认跟着 exe 走（便携），不再写死任何本机路径。
/// 规则：exe 所在目录的上一级如果有 data 目录就用它（本机 shuju\app → shuju\data），
///       否则用 exe 同目录下的 data。
/// </summary>
public static class AppPaths
{
    public static readonly string BaseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public static readonly string DataDir = ResolveDataDir();

    private static string ResolveDataDir()
    {
        try
        {
            var sibling = Path.GetFullPath(Path.Combine(BaseDir, "..", "data"));
            if (Directory.Exists(sibling)) return sibling;
        }
        catch { }
        return Path.Combine(BaseDir, "data");
    }

    public static string LogFile => Path.Combine(DataDir, "app.log");
    public static string SnapshotFile => Path.Combine(DataDir, "last-status.txt");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string RemoteFile => Path.Combine(DataDir, "remote.json");

    public static void Ensure()
    {
        try { Directory.CreateDirectory(DataDir); } catch { }
    }

    public static void Log(string msg)
    {
        try
        {
            Ensure();
            File.AppendAllText(LogFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}
