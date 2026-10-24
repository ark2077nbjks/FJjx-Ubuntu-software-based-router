using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NetPanel.Services;

/// <summary>面板设置：固定放 data\settings.json，字段用可选（旧文件缺字段也能读）。</summary>
public sealed class AppSettings
{
    /// <summary>网址检测里保存的站点串</summary>
    public string Sites { get; set; } = "";

    /// <summary>true = 点 × 缩到托盘继续跑；false = 点 × 直接退出</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>第一次缩到托盘时的提示气泡是否已经弹过</summary>
    public bool TrayTipShown { get; set; }

    /// <summary>掉线/恢复提醒是否开启</summary>
    public bool NotifyOnOffline { get; set; } = true;

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,      // 老文件里是小写 key（sites），大小写不敏感才读得回
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Opt);
                if (s != null) return s;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Opt), new UTF8Encoding(false));
        }
        catch { }
    }
}
