#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""每设备流量统计 —— 读 mac80211 的 station dump 累计计数器，只读、不发包、不碰数据面。

用法：
  sample [n]   采一次样，把和上次的差值追加到当天 CSV（systemd timer 每小时跑）
  summary [n]  汇总最近 n 天（默认 30），输出 JSON
  now [n]      先采样再汇总（面板手动刷新用这个）
"""
import os, sys, json, time, glob, datetime, subprocess

BASE = os.environ.get("GR_TRAFFIC_DIR", "/var/lib/grayrouter/traffic")
STATE = os.path.join(BASE, "state.json")
DEVJSON = "/etc/grayrouter/devices.json"
KEEP_DAYS = 32


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
    res, mac = {}, None
    for line in run(["iw", "dev", iface, "station", "dump"]).splitlines():
        s = line.strip()
        if line.startswith("Station "):
            mac = line.split()[1].lower()
            res[mac] = {"rx": 0, "tx": 0, "signal": None}
        elif mac and s.startswith("rx bytes:"):
            res[mac]["rx"] = int(s.split()[2])
        elif mac and s.startswith("tx bytes:"):
            res[mac]["tx"] = int(s.split()[2])
        elif mac and s.startswith("signal:"):
            try: res[mac]["signal"] = int(s.split()[1])
            except Exception: pass
    return res


def meta():
    names, ips = {}, {}
    try:
        d = json.load(open(DEVJSON, encoding="utf-8")).get("devices", {})
        for m, v in d.items():
            names[m.lower()] = (v.get("name") or "").strip()
    except Exception:
        pass
    for line in run(["ip", "neigh"]).splitlines():
        p = line.split()
        if len(p) >= 5 and "lladdr" in p:
            ips[p[p.index("lladdr") + 1].lower()] = p[0]
    return names, ips


def prune():
    cut = datetime.date.today() - datetime.timedelta(days=KEEP_DAYS)
    for path in glob.glob(os.path.join(BASE, "*.csv")):
        try:
            d = datetime.datetime.strptime(os.path.basename(path)[:8], "%Y%m%d").date()
            if d < cut: os.remove(path)
        except Exception:
            pass


def sample():
    iface = lan_if()
    cur = stations(iface)
    state = {}
    try: state = json.load(open(STATE, encoding="utf-8"))
    except Exception: state = {}

    names, ips = meta()
    now = datetime.datetime.now()
    os.makedirs(BASE, exist_ok=True)
    path = os.path.join(BASE, now.strftime("%Y%m%d") + ".csv")
    fresh = not os.path.exists(path)

    rows = 0
    lines = []
    for mac, c in cur.items():
        prev = state.get(mac, {})
        if not prev:                 # 第一次见到这台设备：只记基线，不把历史会话量算进今天
            drx = dtx = 0
        else:
            drx = c["rx"] - int(prev.get("rx", 0))
            dtx = c["tx"] - int(prev.get("tx", 0))
        if drx < 0: drx = 0          # 设备重连 / AP 重启，计数器归零
        if dtx < 0: dtx = 0
        if drx or dtx or mac not in state:
            lines.append(f"{now:%Y-%m-%d %H:%M:%S},{mac},{ips.get(mac,'')},{names.get(mac,'')},{drx},{dtx}")
            rows += 1
        state[mac] = {"rx": c["rx"], "tx": c["tx"], "seen": int(time.time()), "signal": c["signal"]}

    with open(path, "a", encoding="utf-8") as f:
        if fresh:
            f.write("时间,MAC,IP,名称,下行字节,上行字节\n")
        for l in lines:
            f.write(l + "\n")

    json.dump(state, open(STATE, "w", encoding="utf-8"), ensure_ascii=False)
    prune()
    print(f"采样 {len(cur)} 台在线，写入 {rows} 行 → {path}", file=sys.stderr)


def summary(days=30):
    names, ips = meta()
    online = stations(lan_if())
    today = datetime.date.today()
    start = today - datetime.timedelta(days=days - 1)

    agg = {}
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
            mac = p[1].lower()
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
            a = agg.setdefault(mac, {"d": 0, "u": 0, "td": 0, "tu": 0, "last": "", "name": ""})
            a["d"] += dn; a["u"] += up; a["last"] = p[0]
            if p[3]: a["name"] = p[3]
            if day == today: a["td"] += dn; a["tu"] += up

    out = []
    for mac, a in agg.items():
        on = mac in online
        out.append({
            "mac": mac,
            "name": names.get(mac) or a["name"] or "",
            "ip": ips.get(mac, ""),
            "online": 1 if on else 0,
            "today_down": a["td"], "today_up": a["tu"],
            "span_down": a["d"], "span_up": a["u"],
            "last": a["last"],
            "session_down": online.get(mac, {}).get("rx", 0),
            "session_up": online.get(mac, {}).get("tx", 0),
            "signal": online.get(mac, {}).get("signal"),
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
