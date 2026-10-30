#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""每设备流量统计。

数据来源（只管读，不发包，不改转发）：
  1) nft 计数表 inet gray_acct（up4/down4 两个动态集合，每个 IP 自带 counter）
     —— 覆盖所有 LAN：WiFi + 有线 + 静态 IP，因为统计点在 forward 链上
  2) iw station dump —— 只用来补充 WiFi 信号强度
  3) DHCP 租约 + ARP —— 把 IP 映射回 MAC / 主机名

用法：
  sample [n]   采一次样，把差值追加到当天 CSV（systemd timer 每小时跑）
  summary [n]  汇总最近 n 天（默认 30），输出 JSON
  now [n]      先采样再汇总（面板手动刷新用这个）
"""
import os, sys, json, time, glob, datetime, subprocess, re

BASE = os.environ.get("GR_TRAFFIC_DIR", "/var/lib/grayrouter/traffic")
STATE = os.path.join(BASE, "state.json")
IPMAP = os.path.join(BASE, "ip2mac.json")   # IP→MAC 绑定历史（回填用）
IPMAP_TTL = 30 * 86400                      # 30 天
IPMAP_MAX = 1024
DEVJSON = "/etc/grayrouter/devices.json"
KEEP_DAYS = 32
ACCT_TBL = "gray_acct"


def run(args, timeout=10):
    try:
        return subprocess.run(args, capture_output=True, text=True, timeout=timeout).stdout
    except Exception:
        return ""


def lan_if():
    cur = None
    for line in run(["iw", "dev"]).splitlines():
        s = line.strip()
        if s.startswith("Interface "):
            cur = s.split()[1]
        elif s.startswith("type ") and len(s.split()) > 1 and s.split()[1] == "AP":
            return cur
    return cur or "wlp2s0"


def stations(iface):
    """WiFi 关联表：只取信号强度（字节数改用 nft 计数，覆盖有线）"""
    res, mac = {}, None
    for line in run(["iw", "dev", iface, "station", "dump"]).splitlines():
        s = line.strip()
        if line.startswith("Station "):
            mac = line.split()[1].lower()
            res[mac] = {"signal": None}
        elif mac and s.startswith("signal:"):
            try: res[mac]["signal"] = int(s.split()[1])
            except Exception: pass
    return res


def acct_counts():
    """读 nft 计数表 → {ip: {'up': bytes, 'down': bytes}}；表不存在时返回 {}"""
    out = {}
    for name, key in (("up4", "up"), ("down4", "down")):
        txt = run(["nft", "-j", "list", "set", "inet", ACCT_TBL, name])
        got = False
        if txt.strip():
            try:
                for item in json.loads(txt).get("nftables", []):
                    st = item.get("set")
                    if not st or st.get("name") != name:
                        continue
                    for e in st.get("elem", []):
                        body = e.get("elem", {})
                        ip = body.get("val")
                        cnt = body.get("counter") or {}
                        if ip:
                            out.setdefault(ip, {"up": 0, "down": 0})[key] = int(cnt.get("bytes", 0))
                            got = True
            except Exception:
                got = False
        if not got:
            # 纯文本兜底：elements = { 1.2.3.4 counter packets 1 bytes 2 }
            txt = run(["nft", "list", "set", "inet", ACCT_TBL, name])
            for ip, _p, _b, byt in re.findall(r"(\d+\.\d+\.\d+\.\d+)[^,}]*?packets\s+(\d+)\s+bytes\s+(\d+)", txt):
                out.setdefault(ip, {"up": 0, "down": 0})[key] = int(byt)
    return out


def meta():
    names, ip2mac = {}, {}
    try:
        d = json.load(open(DEVJSON, encoding="utf-8")).get("devices", {})
        for m, v in d.items():
            names[m.lower()] = (v.get("name") or "").strip()
    except Exception:
        pass
    for line in run(["ip", "neigh"]).splitlines():
        p = line.split()
        if len(p) >= 5 and "lladdr" in p and p[-1].upper() not in ("FAILED", "INCOMPLETE"):
            ip2mac[p[0]] = p[p.index("lladdr") + 1].lower()
    # DHCP 租约补充（含已离线但租约仍在的）
    for path in glob.glob("/var/lib/NetworkManager/dnsmasq-*.leases") + ["/var/lib/misc/dnsmasq.leases"]:
        try:
            for line in open(path, errors="replace"):
                p = line.split()
                if len(p) >= 4 and p[1] != "*":
                    mac = p[1].lower()
                    ip2mac.setdefault(p[2], mac)
                    if p[3] != "*":
                        names.setdefault(mac, p[3])
        except Exception:
            continue
    return names, ip2mac


def load_ipmap():
    """IP→MAC 历史表：{ip: [mac, 最后见到秒]}"""
    try:
        d = json.load(open(IPMAP, encoding="utf-8"))
        return d if isinstance(d, dict) else {}
    except Exception:
        return {}


def save_ipmap(m):
    """TTL 过期剔除 + 上限淘汰最旧，然后落盘（几 KB）"""
    now = int(time.time())
    items = []
    for ip, v in m.items():
        mac, ts = ("", 0)
        if isinstance(v, list) and len(v) >= 2:
            mac, ts = str(v[0]), int(v[1])
        elif isinstance(v, str):
            mac, ts = v, now
        if mac and now - ts <= IPMAP_TTL:
            items.append((ts, ip, mac))
    if len(items) > IPMAP_MAX:
        items.sort(reverse=True)
        items = items[:IPMAP_MAX]
    out = {ip: [mac, ts] for ts, ip, mac in items}
    try:
        os.makedirs(BASE, exist_ok=True)
        tmp = IPMAP + ".tmp"
        json.dump(out, open(tmp, "w", encoding="utf-8"), ensure_ascii=False)
        os.replace(tmp, IPMAP)
    except Exception:
        pass


def ipmap_lookup(hist, ip):
    v = hist.get(ip)
    if isinstance(v, list) and len(v) >= 2:
        mac, ts = str(v[0]), int(v[1])
        if mac and time.time() - ts <= IPMAP_TTL:
            return mac
    elif isinstance(v, str):
        return v
    return ""


def ipmap_note(hist, ip, mac):
    if ip and mac:
        hist[ip] = [mac, int(time.time())]


def prune():
    cut = datetime.date.today() - datetime.timedelta(days=KEEP_DAYS)
    for path in glob.glob(os.path.join(BASE, "*.csv")):
        try:
            d = datetime.datetime.strptime(os.path.basename(path)[:8], "%Y%m%d").date()
            if d < cut: os.remove(path)
        except Exception:
            pass


def sample():
    state = {}
    try: state = json.load(open(STATE, encoding="utf-8"))
    except Exception: state = {}

    names, ip2mac = meta()
    hist = load_ipmap()
    counts = acct_counts()
    wifi = stations(lan_if())
    now = datetime.datetime.now()
    os.makedirs(BASE, exist_ok=True)
    path = os.path.join(BASE, now.strftime("%Y%m%d") + ".csv")
    fresh = not os.path.exists(path)

    rows = 0
    lines = []
    for ip, c in counts.items():
        mac = ip2mac.get(ip, "")
        if mac:
            ipmap_note(hist, ip, mac)
        else:
            mac = ipmap_lookup(hist, ip)      # 回填：这个 IP 曾经属于哪台设备
        key = mac or ("ip:" + ip)
        prev = state.get(key, {})
        if not prev:                    # 第一次见：只记基线
            dup = ddown = 0
        else:
            dup = c["up"] - int(prev.get("up", 0))
            ddown = c["down"] - int(prev.get("down", 0))
        if dup < 0: dup = 0             # 计数表重建 → 归零，不写负数
        if ddown < 0: ddown = 0
        if dup or ddown or key not in state:
            nm = names.get(mac, "") if mac else ""
            lines.append(f"{now:%Y-%m-%d %H:%M:%S},{key},{ip},{nm},{ddown},{dup}")
            rows += 1
        state[key] = {"up": c["up"], "down": c["down"], "ip": ip,
                      "seen": int(time.time()), "mac": mac}

    with open(path, "a", encoding="utf-8") as f:
        if fresh:
            f.write("时间,MAC,IP,名称,下行字节,上行字节\n")
        for l in lines:
            f.write(l + "\n")

    json.dump(state, open(STATE, "w", encoding="utf-8"), ensure_ascii=False)
    save_ipmap(hist)
    prune()
    print(f"采样 {len(counts)} 个 IP（表 {ACCT_TBL}），写入 {rows} 行 → {path}", file=sys.stderr)


def summary(days=30):
    names, ip2mac = meta()
    # 回填表：活体(租约/ARP) 优先，其次历史绑定
    hist = load_ipmap()
    mac_of = dict(ip2mac)
    for ip, v in hist.items():
        m = v[0] if isinstance(v, list) and v else (v if isinstance(v, str) else "")
        if m:
            mac_of.setdefault(ip, m)
    counts = acct_counts()
    wifi = stations(lan_if())
    online_macs = set(wifi) | {ip2mac.get(ip, "") for ip in counts} | {ip2mac[ip] for ip in counts if ip in ip2mac}
    online_macs |= {m for ip, m in ip2mac.items() if ip.startswith("192.168.") and m}
    today = datetime.date.today()
    start = today - datetime.timedelta(days=days - 1)

    agg = {}
    csv_ip2mac = {}
    hours = [[0, 0] for _ in range(24)]
    for path in sorted(glob.glob(os.path.join(BASE, "*.csv"))):
        try:
            day = datetime.datetime.strptime(os.path.basename(path)[:8], "%Y%m%d").date()
        except Exception:
            continue
        if day < start: continue
        try:
            body = open(path, encoding="utf-8", errors="replace").read().splitlines()
        except Exception:
            continue
        for line in body[1:]:
            p = line.split(",")
            if len(p) < 6: continue
            key = p[1].lower()
            ipf = (p[2] or "").strip().lower()
            if not key.startswith("ip:") and ipf:
                csv_ip2mac.setdefault(ipf, key)           # CSV 自身记录的 (MAC, IP)
            if key.startswith("ip:"):
                m2 = mac_of.get(ipf) or csv_ip2mac.get(ipf)
                if m2:
                    key = m2.lower()
            try:
                dn, up = int(p[4] or 0), int(p[5] or 0)
            except Exception:
                continue
            if day == today:
                try:
                    hh = int(p[0][11:13])
                    if 0 <= hh <= 23:
                        hours[hh][0] += dn
                        hours[hh][1] += up
                except Exception:
                    pass
            a = agg.setdefault(key, {"d": 0, "u": 0, "td": 0, "tu": 0, "last": "", "name": "", "ip": ""})
            a["d"] += dn; a["u"] += up; a["last"] = p[0]
            if p[3]: a["name"] = p[3]
            if p[2]: a["ip"] = p[2]
            if day == today: a["td"] += dn; a["tu"] += up

    # 剩下的 ip: 桶（行序问题导致当时没查到）在这里合并
    for key in [k for k in list(agg) if k.startswith("ip:")]:
        m2 = csv_ip2mac.get(key[3:]) or mac_of.get(key[3:])
        if not m2:
            continue
        src_a = agg.pop(key)
        dst = agg.setdefault(m2.lower(), {"d": 0, "u": 0, "td": 0, "tu": 0, "last": "", "name": "", "ip": ""})
        for f in ("d", "u", "td", "tu"):
            dst[f] += src_a[f]
        if src_a["last"] > dst["last"]:
            dst["last"] = src_a["last"]
        if not dst["name"] and src_a["name"]:
            dst["name"] = src_a["name"]
        if not dst["ip"] and src_a["ip"]:
            dst["ip"] = src_a["ip"]

    out = []
    for key, a in agg.items():
        mac = "" if key.startswith("ip:") else key
        ip = a["ip"] or (key[3:] if key.startswith("ip:") else "")
        cur = counts.get(ip, {})
        out.append({
            "mac": mac,
            "name": names.get(mac) or a["name"] or ("" if mac else "未知设备"),
            "ip": ip,
            "online": 1 if (mac and mac in online_macs) or (ip in counts) else 0,
            "today_down": a["td"], "today_up": a["tu"],
            "span_down": a["d"], "span_up": a["u"],
            "last": a["last"],
            "session_down": cur.get("down", 0),
            "session_up": cur.get("up", 0),
            "signal": wifi.get(mac, {}).get("signal") if mac else None,
        })
    out.sort(key=lambda x: (x["online"], x["span_down"] + x["span_up"]), reverse=True)
    print(json.dumps({
        "days": days,
        "devices": out,
        "hours": [{"h": i, "down": hours[i][0], "up": hours[i][1]} for i in range(24)],
    }, ensure_ascii=False))


if __name__ == "__main__":
    act = sys.argv[1] if len(sys.argv) > 1 else "summary"
    n = 30
    if len(sys.argv) > 2:
        try: n = max(1, min(31, int(sys.argv[2])))
        except Exception: n = 30
    if act == "reset":
        for f in glob.glob(os.path.join(BASE, "*")):
            try: os.remove(f)
            except Exception: pass
        print("已清空 " + BASE)
    elif act == "sample":
        sample()
    elif act == "now":
        sample(); summary(n)
    else:
        summary(n)
