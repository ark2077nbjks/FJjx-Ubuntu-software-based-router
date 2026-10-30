#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
grayrouter-usage.py —— 流量日/月汇总（只读）

数据来源：/var/lib/grayrouter/traffic/YYYYMMDD.csv
格式：时间,MAC,IP,名称,下行字节,上行字节（每行是「这一段时间的增量」，不是累计值）
所以某天总量 = 当天所有行求和。

用法：grayrouter-usage.py [天数]        默认 30 天
输出：一行 JSON
  {"days":[{"date":"20261030","up":123,"down":456}...],
   "month_up":..., "month_down":..., "total_up":..., "total_down":..., "file_count":N}
"""
import csv
import glob
import json
import os
import sys
import datetime

DIR = "/var/lib/grayrouter/traffic"


def day_totals(path):
    """返回 (up, down, rows)；坏行跳过，不抛异常。"""
    up = down = rows = 0
    try:
        with open(path, "r", encoding="utf-8", errors="replace", newline="") as f:
            reader = csv.DictReader(f)
            for r in reader:
                try:
                    down += int((r.get("下行字节") or "0").strip() or 0)
                    up += int((r.get("上行字节") or "0").strip() or 0)
                    rows += 1
                except (ValueError, AttributeError):
                    continue
    except Exception:
        return None
    return up, down, rows


def main():
    days = 30
    if len(sys.argv) > 1:
        try:
            days = max(1, min(400, int(sys.argv[1])))
        except ValueError:
            pass

    files = sorted(glob.glob(os.path.join(DIR, "20*.csv")))
    picked = files[-days:]

    out_rows = []
    for p in picked:
        t = day_totals(p)
        if not t or t[2] == 0:
            continue
        out_rows.append({"date": os.path.basename(p)[:8], "up": t[0], "down": t[1], "rows": t[2]})

    month = datetime.date.today().strftime("%Y%m")
    month_rows = [r for r in out_rows if r["date"].startswith(month)]

    print(json.dumps({
        "days": out_rows,
        "month_up": sum(r["up"] for r in month_rows),
        "month_down": sum(r["down"] for r in month_rows),
        "total_up": sum(r["up"] for r in out_rows),
        "total_down": sum(r["down"] for r in out_rows),
        "file_count": len(out_rows),
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
