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

C_Y=$'\033[33m'; C_N=$'\033[0m'
[ "$(id -u)" -ne 0 ] && { echo "需要 root：sudo $0"; exit 2; }
echo "停止 $LAN_CON ..."
nmcli connection down "$LAN_CON" >/dev/null 2>&1 || true
[ -r "$GR_ETC/ttl.nft" ] && nft delete table ip grayrouter 2>/dev/null || true
printf "${C_Y}已停止${C_N}（上游网络未改动）\n"
