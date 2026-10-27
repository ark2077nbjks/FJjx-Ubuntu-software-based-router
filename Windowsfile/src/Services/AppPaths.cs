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

    /// <summary>自定义日志目录；null/空 = 用 DataDir（由 AppSettings 载入时设置）</summary>
    public static string? LogDirOverride;

    public static string LogDir =>
        string.IsNullOrWhiteSpace(LogDirOverride) ? DataDir : LogDirOverride!;

    public static string LogFile => Path.Combine(LogDir, "app.log");

    /// <summary>单个日志文件上限 4 MB</summary>
    public const long MaxLogBytes = 4L * 1024 * 1024;
    /// <summary>最多保留 5 份（app.log + app.log.1~4，合计约 20 MB）</summary>
    public const int MaxLogFiles = 5;

    public static string RolledLogFile(int n) => Path.Combine(LogDir, $"app.log.{n}");
    public static string SnapshotFile => Path.Combine(DataDir, "last-status.txt");
    public static string SettingsFile => Path.Combine(DataDir, "settings.ini");
    public static string LegacySettingsFile => Path.Combine(DataDir, "settings.json");
    public static string RemoteFile => Path.Combine(DataDir, "remote.json");
    /// <summary>程序自己的临时文件也放在数据目录里，绝不写系统 TEMP</summary>
    public static string TmpDir => Path.Combine(DataDir, "tmp");


    /// <summary>
    /// 切换日志目录（剪切语义）：app.log 与 app.log.1~4 全部搬走，源文件删掉。
    /// 新目录已有同名文件就追加在后面（内容不丢）。targetDir 传 null/空 = 回默认 data 目录。
    /// </summary>
    public static (bool ok, string msg) MoveLogTo(string? targetDir)
    {
        lock (_logLock)
        {
            try
            {
                var oldDir = LogDir;
                var newDir = string.IsNullOrWhiteSpace(targetDir) ? DataDir : targetDir!;
                var newMain = Path.Combine(newDir, "app.log");

                if (string.Equals(Path.GetFullPath(LogFile), Path.GetFullPath(newMain), StringComparison.OrdinalIgnoreCase))
                {
                    LogDirOverride = string.IsNullOrWhiteSpace(targetDir) ? null : targetDir;
                    return (true, "目录没变，无需迁移");
                }

                Directory.CreateDirectory(newDir);

                int moved = 0;
                // 主文件
                if (File.Exists(LogFile))
                {
                    var text = File.ReadAllText(LogFile);
                    if (File.Exists(newMain)) File.AppendAllText(newMain, text, new UTF8Encoding(false));
                    else File.WriteAllText(newMain, text, new UTF8Encoding(false));
                    File.Delete(LogFile);
                    moved++;
                }
                // 滚动文件 1..MaxLogFiles-1
                for (int i = 1; i < MaxLogFiles; i++)
                {
                    var from = RolledLogFile(i);
                    if (!File.Exists(from)) continue;
                    var to = Path.Combine(newDir, $"app.log.{i}");
                    var text = File.ReadAllText(from);
                    if (File.Exists(to)) File.AppendAllText(to, text, new UTF8Encoding(false));
                    else File.WriteAllText(to, text, new UTF8Encoding(false));
                    File.Delete(from);
                    moved++;
                }

                LogDirOverride = string.IsNullOrWhiteSpace(targetDir) ? null : targetDir;
                _logSize = -1;   // 目录变了，重新数
                return (true, moved == 0
                    ? $"旧日志不存在，已直接切到 {newDir}"
                    : $"已把 {moved} 个日志文件从 {oldDir} 搬到 {newDir}");
            }
            catch (Exception ex)
            {
                return (false, "迁移日志失败：" + ex.Message);
            }
        }
    }

    public static void Ensure()
    {
        try { Directory.CreateDirectory(DataDir); } catch { }
        try { if (LogDir != DataDir) Directory.CreateDirectory(LogDir); } catch { }
        try { Directory.CreateDirectory(TmpDir); } catch { }
    }

    /// <summary>目录总大小（字节）</summary>
    public static long DirSize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    public static string Human(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
        : $"{bytes} B";

    // ---------------- 日志写入与滚动 ----------------

    private static readonly object _logLock = new();
    private static long _logSize = -1;          // -1 = 还没数过

    /// <summary>当前 app.log 字节数（面板显示用）</summary>
    public static long CurrentLogSize
    {
        get { lock (_logLock) { InitSize(); return _logSize < 0 ? 0 : _logSize; } }
    }

    private static void InitSize()
    {
        if (_logSize >= 0) return;
        try
        {
            var fi = new FileInfo(LogFile);
            _logSize = fi.Exists ? fi.Length : 0;
        }
        catch { _logSize = 0; }
    }

    public static void Log(string msg)
    {
        lock (_logLock)
        {
            try
            {
                Ensure();
                InitSize();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}";
                var bytes = Encoding.UTF8.GetByteCount(line);
                if (_logSize > 0 && _logSize + bytes > MaxLogBytes) RotateLocked();
                File.AppendAllText(LogFile, line, new UTF8Encoding(false));
                _logSize += bytes;
            }
            catch { /* 日志失败不影响主流程 */ }
        }
    }

    /// <summary>把 app.log 滚成 app.log.1，后面的顺延，最旧的丢掉</summary>
    private static void RotateLocked()
    {
        try
        {
            var last = RolledLogFile(MaxLogFiles - 1);
            if (File.Exists(last)) File.Delete(last);
            for (int i = MaxLogFiles - 2; i >= 1; i--)
            {
                var from = RolledLogFile(i);
                if (File.Exists(from)) File.Move(from, RolledLogFile(i + 1), overwrite: true);
            }
            if (File.Exists(LogFile)) File.Move(LogFile, RolledLogFile(1), overwrite: true);
            _logSize = 0;
        }
        catch { }
    }

    /// <summary>手动触发一次滚动（「清空日志」用：旧内容变成 app.log.1，可查）</summary>
    public static bool RotateNow()
    {
        lock (_logLock)
        {
            try { Ensure(); InitSize(); if (_logSize > 0) RotateLocked(); return true; }
            catch { return false; }
        }
    }

    /// <summary>硬删除：app.log 与所有滚动文件全部删掉</summary>
    public static (bool ok, int deleted) DeleteAllLogs()
    {
        lock (_logLock)
        {
            int n = 0;
            try
            {
                if (File.Exists(LogFile)) { File.Delete(LogFile); n++; }
                for (int i = 1; i < MaxLogFiles; i++)
                {
                    var f = RolledLogFile(i);
                    if (File.Exists(f)) { File.Delete(f); n++; }
                }
                _logSize = 0;
                return (true, n);
            }
            catch { return (false, n); }
        }
    }

    /// <summary>导出：把滚动文件（旧→新）和当前 app.log 拼成一份文本</summary>
    public static string BuildExportText()
    {
        lock (_logLock)
        {
            var sb = new StringBuilder();
            sb.AppendLine("网络面板 · 运行日志");
            sb.AppendLine("================================");
            sb.AppendLine($"导出时间  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"日志目录  {LogDir}");
            sb.AppendLine($"单文件上限 {Human(MaxLogBytes)} · 保留 {MaxLogFiles} 份");
            sb.AppendLine();
            try
            {
                for (int i = MaxLogFiles - 1; i >= 1; i--)
                {
                    var f = RolledLogFile(i);
                    if (!File.Exists(f)) continue;
                    sb.AppendLine($"---------- app.log.{i} ----------");
                    sb.AppendLine(File.ReadAllText(f));
                }
                if (File.Exists(LogFile))
                {
                    sb.AppendLine("---------- app.log（当前）----------");
                    sb.AppendLine(File.ReadAllText(LogFile));
                }
            }
            catch (Exception ex) { sb.AppendLine("读取日志时出错：" + ex.Message); }
            return sb.ToString();
        }
    }
}
