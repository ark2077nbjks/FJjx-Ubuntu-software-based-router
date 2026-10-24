#!/usr/bin/env bash
# ------------------------------------------------------------
# 通用版：参数全部从 /etc/grayrouter/config.env 读取
# 可用环境变量覆盖，便于调试：GR_ETC=/tmp/x ./router-status.sh
# ------------------------------------------------------------
set -u
GR_ETC=${GR_ETC:-/etc/grayrouter}
GR_OPT=${GR_OPT:-/opt/grayrouter}
[ -r "$GR_ETC/config.env" ] && . "$GR_ETC/config.env"

TOPO=${TOPO:-A}
WAN_IF=${WAN_IF:-}
LAN_IF=${LAN_IF:-}
LAN_TYPE=${LAN_TYPE:-wifi-ap}
LAN_IP=${LAN_IP:-192.168.50.1}
SSID=${SSID:-Gray-Router}
LAN_CON=${LAN_CON:-GrayRouter-LAN}

# 给 Windows 「网络面板」读的机器可读状态：KEY=VALUE
wip=$(ip -4 -br addr show "${WAN_IF:-}" 2>/dev/null | awk '{print $3}')
lip=$(ip -4 -br addr show "$LAN_IF" 2>/dev/null | awk '{print $3}')
gw=$(ip route show default 2>/dev/null | awk '/default/{print $3; exit}')
active=$(nmcli -t -f NAME,DEVICE connection show --active 2>/dev/null | grep -q "^${LAN_CON}:" && echo 1 || echo 0)
clients=$(ip neigh show dev "$LAN_IF" 2>/dev/null | awk '/REACHABLE|STALE|DELAY/{print $1}' | grep -v '^fe80' | wc -l)
code=$(curl -s -m 5 -o /dev/null -w '%{http_code}' https://www.baidu.com 2>/dev/null)

echo "TOPOLOGY=$TOPO"
echo "WAN_IF=$WAN_IF"
echo "WAN_IP=${wip:-}"
echo "GATEWAY=${gw:-}"
echo "LAN_IF=$LAN_IF"
echo "LAN_IP=${lip:-}"
echo "SSID=$SSID"
echo "LAN_ACTIVE=$active"
echo "CLIENTS=$clients"
echo "IP_FORWARD=$(cat /proc/sys/net/ipv4/ip_forward 2>/dev/null)"
echo "OUT_HTTP=$code"
echo "TS=$(date '+%F %T')"
