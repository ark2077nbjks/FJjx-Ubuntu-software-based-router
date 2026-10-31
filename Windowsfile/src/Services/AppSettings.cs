using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NetPanel.Services;

/// <summary>
/// 面板设置：纯文本 settings.ini，记事本能直接看懂、直接改。
/// 每行 key=value，# 开头是说明。旧的 settings.json 首次启动自动迁移。
/// </summary>
public sealed class AppSettings
{
    public string Sites { get; set; } = "";
    public bool CloseToTray { get; set; } = true;
    public bool TrayTipShown { get; set; }
    public bool NotifyOnOffline { get; set; } = true;
    /// <summary>设备上线时弹托盘提醒（对标 UniFi 客户端上线通知）</summary>
    public bool NotifyDeviceOnline { get; set; } = true;
    /// <summary>日志文件夹；空 = 用 data 目录</summary>
    public string LogDir { get; set; } = "";
    /// <summary>上次导出测速结果的目录</summary>
    public string LastExportDir { get; set; } = "";

    public static AppSettings Load()
    {
        var s = new AppSettings();
        try
        {
            if (File.Exists(AppPaths.SettingsFile)) s.Parse(File.ReadAllLines(AppPaths.SettingsFile, Encoding.UTF8));
            else if (File.Exists(AppPaths.LegacySettingsFile)) s.MigrateFromJson();
        }
        catch { }
        AppPaths.LogDirOverride = s.LogDir;
        return s;
    }

    private void Parse(string[] lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int i = line.IndexOf('=');
            if (i <= 0) continue;
            var key = line[..i].Trim().ToLowerInvariant();
            var val = line[(i + 1)..].Trim();
            switch (key)
            {
                case "sites": Sites = val; break;
                case "close_to_tray": CloseToTray = IsTrue(val); break;
                case "tray_tip_shown": TrayTipShown = IsTrue(val); break;
                case "notify_on_offline": NotifyOnOffline = IsTrue(val); break;
                case "notify_device_online": NotifyDeviceOnline = IsTrue(val); break;
                case "log_dir": LogDir = val; break;
                case "last_export_dir": LastExportDir = val; break;
            }
        }
    }

    private static bool IsTrue(string v) => v is "1" or "true" or "True" or "yes" or "on";

    private void MigrateFromJson()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.LegacySettingsFile));
            var r = doc.RootElement;
            string Get(string n) => r.TryGetProperty(n, out var e) ? (e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString()) : "";
            foreach (var p in r.EnumerateObject())
            {
                switch (p.Name.ToLowerInvariant())
                {
                    case "sites": Sites = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : ""; break;
                    case "closetotray": CloseToTray = p.Value.ValueKind == JsonValueKind.True; break;
                    case "traytipshown": TrayTipShown = p.Value.ValueKind == JsonValueKind.True; break;
                    case "notifyonoffline": NotifyOnOffline = p.Value.ValueKind == JsonValueKind.True; break;
                    case "notifydeviceonline": NotifyDeviceOnline = p.Value.ValueKind == JsonValueKind.True; break;
                }
            }
            Save();
            File.Move(AppPaths.LegacySettingsFile, AppPaths.LegacySettingsFile + ".bak_转ini", true);
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            AppPaths.Ensure();
            var sb = new StringBuilder();
            sb.AppendLine("# ===== 网络面板 设置文件 =====");
            sb.AppendLine("# 这个文件可以直接用记事本改，改完保存，下次启动面板生效。");
            sb.AppendLine("# 以 # 开头的行是说明文字，不用管。");
            sb.AppendLine();
            sb.AppendLine("# 网址检测里保存的站点，多个用英文逗号分开");
            sb.AppendLine("sites=" + Sites);
            sb.AppendLine();
            sb.AppendLine("# 点右上角 × 的时候：1 = 缩到右下角托盘继续跑，0 = 直接退出");
            sb.AppendLine("close_to_tray=" + (CloseToTray ? "1" : "0"));
            sb.AppendLine();
            sb.AppendLine("# 掉线、恢复时是否弹提醒：1 = 要，0 = 不要");
            sb.AppendLine("notify_on_offline=" + (NotifyOnOffline ? "1" : "0"));
            sb.AppendLine();
            sb.AppendLine("# 有设备连上局域网时弹托盘提醒：1 = 要，0 = 不要");
            sb.AppendLine("notify_device_online=" + (NotifyDeviceOnline ? "1" : "0"));
            sb.AppendLine();
            sb.AppendLine("# 日志文件夹。留空 = 用默认的 data 目录；也可以填绝对路径，例如 D:\\我的日志");
            sb.AppendLine("log_dir=" + LogDir);
            sb.AppendLine();
            sb.AppendLine("# 上次导出测速结果的文件夹（面板自己记，一般不用管）");
            sb.AppendLine("last_export_dir=" + LastExportDir);
            sb.AppendLine();
            sb.AppendLine("# 第一次缩到托盘时是否弹过提示（弹过就是 1，别手改）");
            sb.AppendLine("tray_tip_shown=" + (TrayTipShown ? "1" : "0"));
            File.WriteAllText(AppPaths.SettingsFile, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }
}
