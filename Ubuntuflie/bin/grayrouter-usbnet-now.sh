#!/usr/bin/env bash
# ============================================================
#  grayrouter-usbnet-now.sh —— 立刻激活所有 USB 网卡 / 手机共享口（面板按钮用）
#  用法：grayrouter-usbnet-now.sh（无参数）
#  依赖：iproute2、grayrouter-usbnet-up.sh
#  退出码：0=成功（没找到设备也是 0）
#  注：会改网口状态和地址，可能瞬时影响该网口的连通性
# ============================================================
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
