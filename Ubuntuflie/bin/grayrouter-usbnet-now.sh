#!/usr/bin/env bash
# 立刻把每个 USB 网卡/手机共享口激活一次（面板按钮用）
set -uo pipefail
U=/usr/local/bin/grayrouter-usbnet-up.sh
found=0
for n in $(ls /sys/class/net 2>/dev/null | grep -E '^(enx|usb|rndis|wwan)'); do
  found=1
  echo "→ $n"
  bash "$U" "$n" 2>&1 | tail -2
  ip -4 -br addr show "$n"
done
[ "$found" = "0" ] && echo "没有发现 USB 网卡 / 手机共享口（先插上手机并开 USB 共享）"
