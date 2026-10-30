using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NetPanel.Services;

/// <summary>设备上下线事件（写 data\events.csv，界面上只显示最近几条，不弹窗）</summary>
public static class DeviceEvents
{
    private static readonly object _lock = new();
    public static string FilePath => Path.Combine(AppPaths.DataDir, "events.csv");

    /// <summary>记一条事件（up=true 上线）</summary>
    public static void Record(string mac, string name, bool up)
    {
        try
        {
            lock (_lock)
            {
                var dir = AppPaths.DataDir;
                Directory.CreateDirectory(dir);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{mac},{(string.IsNullOrWhiteSpace(name) ? "-" : name.Replace(",", " "))},{(up ? "上线" : "下线")}";
                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }

    /// <summary>最近 n 条事件（新的在前）</summary>
    public static List<string> Recent(int n = 3)
    {
        var list = new List<string>();
        try
        {
            lock (_lock)
            {
                if (!File.Exists(FilePath)) return list;
                var lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                foreach (var l in lines.Reverse())
                {
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    var p = l.Split(',');
                    if (p.Length < 4) continue;
                    var name = p[2] == "-" ? p[1] : p[2];
                    list.Add($"{name} {p[3]}（{p[0][11..16]}）");
                    if (list.Count >= n) break;
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>把文件裁到最近 2000 行，避免无限增长</summary>
    public static void TrimIfNeeded()
    {
        try
        {
            lock (_lock)
            {
                if (!File.Exists(FilePath)) return;
                var lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                if (lines.Length <= 2000) return;
                File.WriteAllLines(FilePath, lines.Skip(lines.Length - 2000), Encoding.UTF8);
            }
        }
        catch { }
    }
}
