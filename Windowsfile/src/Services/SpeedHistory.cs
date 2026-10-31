using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetPanel.Services
{
    /// <summary>测速历史：每次测速完成追加一行 CSV，供「历史」窗口画趋势。</summary>
    public sealed class SpeedRecord
    {
        public DateTime When;
        public string Source = "";
        public double Down;
        public double Peak;
        public double Up;
    }

    public static class SpeedHistory
    {
        private static string PathOf() => System.IO.Path.Combine(AppPaths.DataDir, "speed_history.csv");

        public static void Append(string source, double down, double peak, double up)
        {
            try
            {
                AppPaths.Ensure();
                var path = PathOf();
                bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
                using var sw = new StreamWriter(path, append: true, new UTF8Encoding(true));
                if (isNew) sw.WriteLine("时间,测速源,下行Mbps,峰值Mbps,上行Mbps");
                sw.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{Escape(source)},{down:F1},{peak:F1},{up:F1}");
            }
            catch { }
        }

        public static List<SpeedRecord> ReadLast(int count)
        {
            var list = new List<SpeedRecord>();
            try
            {
                var path = PathOf();
                if (!File.Exists(path)) return list;
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = lines.Length - 1; i >= 1 && list.Count < count; i--)
                {
                    var parts = lines[i].Split(',');
                    if (parts.Length < 5) continue;
                    if (!DateTime.TryParse(parts[0], out var when)) continue;
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var d);
                    double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var pk);
                    double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var u);
                    list.Add(new SpeedRecord { When = when, Source = parts[1], Down = d, Peak = pk, Up = u });
                }
            }
            catch { }
            list.Reverse();
            return list;
        }

        private static string Escape(string s) => (s ?? "").Replace(",", "，");
    }
}
