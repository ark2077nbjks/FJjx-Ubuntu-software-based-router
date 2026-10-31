using System;
using System.Collections.Generic;

namespace NetPanel;

/// <summary>
/// 按 MAC 厂商前缀（OUI）+ DHCP 主机名猜设备类型，给设备表出图标。
/// 参考 UniFi / 爱快的做法：OUI 定厂商，主机名兜底，猜不出给通用图标。
/// 全程离线，不查任何在线库。
/// </summary>
public static class DeviceType
{
    private enum Kind { Phone, Laptop, Tv, Printer, Router, Iot, Game, Unknown }

    // 只收常见前缀（够用且不会误判）；小写
    private static readonly Dictionary<string, Kind> Oui = new(StringComparer.OrdinalIgnoreCase)
    {
        // Apple
        ["3c:22:fb"] = Kind.Phone, ["f0:18:98"] = Kind.Phone, ["68:ab:1e"] = Kind.Phone,
        ["9c:f4:8e"] = Kind.Phone, ["dc:a9:04"] = Kind.Phone, ["04:0c:ce"] = Kind.Laptop,
        ["a4:83:e7"] = Kind.Laptop, ["7c:d1:c3"] = Kind.Laptop,
        // 小米 / Redmi
        ["8c:be:be"] = Kind.Phone, ["64:cc:2e"] = Kind.Phone, ["78:11:dc"] = Kind.Iot,
        ["28:6c:07"] = Kind.Iot, ["74:23:44"] = Kind.Phone, ["f0:b4:29"] = Kind.Iot,
        // 华为 / 荣耀
        ["48:db:50"] = Kind.Phone, ["00:e0:fc"] = Kind.Iot, ["70:72:3c"] = Kind.Phone,
        ["88:53:d4"] = Kind.Phone,
        // 三星
        ["8c:77:12"] = Kind.Phone, ["34:23:87"] = Kind.Phone, ["e8:50:8b"] = Kind.Phone,
        // OPPO / vivo / realme / 一加
        ["30:5a:3a"] = Kind.Phone, ["c0:ee:fb"] = Kind.Phone, ["2c:5b:b8"] = Kind.Phone,
        // 网卡厂（多半是电脑）
        ["3c:ef:a5"] = Kind.Laptop, ["b0:82:e2"] = Kind.Laptop, ["00:e0:4c"] = Kind.Laptop,
        ["52:54:00"] = Kind.Laptop, ["00:1b:21"] = Kind.Laptop,
        // 路由 / 网络设备
        ["50:c7:bf"] = Kind.Router, ["98:da:c4"] = Kind.Router, ["c4:41:1e"] = Kind.Router,
        ["b0:be:76"] = Kind.Router,
        // 电视 / 盒子
        ["b0:a7:37"] = Kind.Tv, ["64:b4:73"] = Kind.Tv, ["d4:9a:20"] = Kind.Tv,
        // 打印机
        ["00:26:73"] = Kind.Printer, ["3c:2a:f4"] = Kind.Printer, ["9c:93:4e"] = Kind.Printer,
        // IoT
        ["50:02:91"] = Kind.Iot, ["60:01:94"] = Kind.Iot, ["dc:4f:22"] = Kind.Iot,
        ["84:f3:eb"] = Kind.Iot,
        // 游戏机
        ["00:9e:c8"] = Kind.Game, ["7c:bb:8a"] = Kind.Game,
    };

    private static readonly (string Key, Kind Kind)[] NameHints =
    {
        ("iphone", Kind.Phone), ("ipad", Kind.Phone), ("redmi", Kind.Phone), ("mi ", Kind.Phone),
        ("mi-", Kind.Phone), ("xiaomi", Kind.Phone), ("huawei", Kind.Phone), ("honor", Kind.Phone),
        ("oppo", Kind.Phone), ("vivo", Kind.Phone), ("realme", Kind.Phone), ("oneplus", Kind.Phone),
        ("galaxy", Kind.Phone), ("pixel", Kind.Phone), ("mate", Kind.Phone), ("note", Kind.Phone),
        ("laptop", Kind.Laptop), ("desktop", Kind.Laptop), ("pc-", Kind.Laptop), ("thinkpad", Kind.Laptop),
        ("macbook", Kind.Laptop), ("notebook", Kind.Laptop), ("-pc", Kind.Laptop),
        ("tv", Kind.Tv), ("tvbox", Kind.Tv), ("box", Kind.Tv), ("chromecast", Kind.Tv), ("shield", Kind.Tv),
        ("printer", Kind.Printer), ("laserjet", Kind.Printer), ("epson", Kind.Printer), ("canon", Kind.Printer),
        ("router", Kind.Router), ("openwrt", Kind.Router), ("miwifi", Kind.Router), ("asus", Kind.Router),
        ("switch", Kind.Game), ("playstation", Kind.Game), ("xbox", Kind.Game),
        ("esp", Kind.Iot), ("tuya", Kind.Iot), ("bulb", Kind.Iot), ("plug", Kind.Iot), ("camera", Kind.Iot),
        ("speaker", Kind.Iot), ("vacuum", Kind.Iot), ("robot", Kind.Iot),
        ("网关", Kind.Router), ("路由", Kind.Router), ("光猫", Kind.Router), ("猫", Kind.Router),
        ("电视", Kind.Tv), ("盒子", Kind.Tv), ("投影", Kind.Tv),
        ("打印机", Kind.Printer), ("打印", Kind.Printer),
        ("音箱", Kind.Iot), ("摄像", Kind.Iot), ("扫地", Kind.Iot), ("插座", Kind.Iot), ("空调", Kind.Iot),
    };

    public static string Label(string mac, string name, string conn, string ip = "")
        => LabelOf(Guess(mac, name, conn, ip));

    public static string IconFor(string mac, string name, string conn, string ip = "")
        => $"/icons/ui_{FileOf(Guess(mac, name, conn, ip))}_24_dark.png";

    private static Kind Guess(string mac, string name, string conn, string ip = "")
    {
        // 网关地址（x.x.x.1）基本就是路由器
        if (!string.IsNullOrWhiteSpace(ip) && ip.Trim().EndsWith(".1")) return Kind.Router;
        var lower = (name ?? "").ToLowerInvariant();
        foreach (var (key, kind) in NameHints)
            if (lower.Contains(key)) return kind;

        var m = (mac ?? "").ToLowerInvariant();
        if (m.Length >= 8)
        {
            var oui = m[..8];
            if (Oui.TryGetValue(oui, out var k)) return k;
        }

        // 随机 MAC（本地管理位）：手机/平板默认开隐私 MAC
        if (m.Length >= 2 && int.TryParse(m[..2], System.Globalization.NumberStyles.HexNumber, null, out var b0))
        {
            if ((b0 & 0x02) != 0) return Kind.Phone;
        }
        if ((conn ?? "").Contains("无线")) return Kind.Phone;
        if ((conn ?? "").Contains("有线")) return Kind.Laptop;
        return Kind.Unknown;
    }

    private static string FileOf(Kind k) => k switch
    {
        Kind.Phone => "smartphone",
        Kind.Laptop => "laptop",
        Kind.Tv => "tv",
        Kind.Printer => "printer",
        Kind.Router => "router",
        Kind.Iot => "lightbulb",
        Kind.Game => "gamepad",
        _ => "circle-help",
    };

    private static string LabelOf(Kind k) => k switch
    {
        Kind.Phone => "手机 / 平板",
        Kind.Laptop => "电脑",
        Kind.Tv => "电视 / 盒子",
        Kind.Printer => "打印机",
        Kind.Router => "网络设备",
        Kind.Iot => "智能家居",
        Kind.Game => "游戏机",
        _ => "未知设备",
    };

    /// <summary>判断一个「名字」是不是没信息量（空 / 就是我们自己的类型词 / Android 默认名 / 纯 MAC），
    /// 这种就退回显示 IP，避免流量页、设备页被「手机」「android」这种名字盖住。</summary>
    public static bool IsWeakName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return true;
        var lower = n.ToLowerInvariant();
        foreach (var w in new[] { "手机", "手机 / 平板", "电脑", "平板", "电视", "电视 / 盒子", "打印机",
                                  "网络设备", "智能家居", "游戏机", "未知设备", "未知", "unknown",
                                  "android", "localhost", "localhost.localdomain", "-", "*" })
            if (lower == w) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"^([0-9a-f]{2}[:-]){5}[0-9a-f]{2}$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
        return false;
    }

    /// <summary>展示用名字：名字有信息就用名字，否则用 IP，再不行用 MAC。</summary>
    public static string DisplayName(string? name, string? ip, string? mac)
    {
        if (!IsWeakName(name)) return name!.Trim();
        if (!string.IsNullOrWhiteSpace(ip)) return ip!.Trim();
        return (mac ?? "").Trim();
    }
}
