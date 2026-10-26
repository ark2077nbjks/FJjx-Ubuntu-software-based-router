#!/usr/bin/env bash
# 手机 USB 共享 / USB 网卡插上后：自动建连接 + 要 DHCP 地址
#   关键：ipv4.never-default=yes —— 它永远不会自己抢默认路由
#   想用它当出口，只能由 grayrrouter-wanfail.sh 显式加路由（切不通会被拒）
# 由 udev 触发（grayrouter-usbnet@<iface>.service）
set -uo pipefail
IF="${1:-}"
[ -z "$IF" ] && exit 0
[ -d "/sys/class/net/$IF" ] || exit 0
LOG=/var/log/grayrouter-usbnet.log
log(){ echo "$(date '+%F %T') $*" >> "$LOG" 2>/dev/null; }

sleep 2
if ip -4 addr show dev "$IF" 2>/dev/null | grep -q 'inet '; then
  log "$IF 已有地址，跳过"; exit 0
fi

NAME="USB共享-$IF"
if ! nmcli -t -f NAME con show 2>/dev/null | grep -qx "$NAME"; then
  nmcli con add type ethernet con-name "$NAME" ifname "$IF" \
        ipv4.method auto ipv4.never-default yes ipv4.route-metric 700 \
        ipv6.method ignore connection.autoconnect yes >>"$LOG" 2>&1 \
    && log "已建连接 $NAME（永不抢默认路由）"
fi
nmcli con up "$NAME" >>"$LOG" 2>&1
sleep 3
if ip -4 addr show dev "$IF" 2>/dev/null | grep -q 'inet '; then
  log "$IF 已拿到 $(ip -4 -o addr show dev "$IF" | awk '{print $4}' | head -1)"
else
  log "$IF 仍无地址（手机没开共享 / 手机没网 / 数据线只充电）"
fi
