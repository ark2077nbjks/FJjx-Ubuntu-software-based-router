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

# 免 root 轻量版：只读，不调用 nft/ss 的权限敏感部分
C_R=$'\033[31m'; C_G=$'\033[32m'; C_Y=$'\033[33m'; C_B=$'\033[36m'; C_D=$'\033[2m'; C_N=$'\033[0m'
ok(){ printf "  ${C_G}✓${C_N} %s\n" "$*"; }
bad(){ printf "  ${C_R}✗${C_N} %s\n" "$*"; }
info(){ printf "  ${C_D}·${C_N} %s\n" "$*"; }

printf "${C_B}【GrayRouter 状态】${C_N}\n"
printf "  拓扑 %s  WAN %s  LAN %s\n" "$TOPO" "${WAN_IF:-无线}" "$LAN_IF"
lip=$(ip -4 -br addr show "$LAN_IF" 2>/dev/null | awk '{print $3}')
[ -n "$lip" ] && ok "LAN $lip" || bad "LAN 无 IP"
nmcli -t -f NAME,DEVICE connection show --active 2>/dev/null | grep -q "^${LAN_CON}:" \
  && ok "$LAN_CON 活动中" || bad "$LAN_CON 未启动"
printf "  ip_forward=%s\n" "$(cat /proc/sys/net/ipv4/ip_forward 2>/dev/null)"
gw=$(ip route show default 2>/dev/null | awk '/default/{print $3; exit}')
printf "  网关 %s\n" "${gw:-无}"
