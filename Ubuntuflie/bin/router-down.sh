#!/usr/bin/env bash
# ------------------------------------------------------------
# 通用版：参数全部从 /etc/grayrouter/config.env 读取
# 可用环境变量覆盖，便于调试：GR_ETC=/tmp/x ./router-status.sh
# ------------------------------------------------------------
set -u
GR_ETC=${GR_ETC:-/etc/grayrouter}
GR_OPT=${GR_OPT:-/opt/grayrouter}
mkdir -p "$GR_ETC" 2>/dev/null || true
echo hotspot > "$GR_ETC/boot-mode" 2>/dev/null || true
[ -r "$GR_ETC/config.env" ] && . "$GR_ETC/config.env"

TOPO=${TOPO:-A}
WAN_IF=${WAN_IF:-}
LAN_IF=${LAN_IF:-}
LAN_TYPE=${LAN_TYPE:-wifi-ap}
LAN_IP=${LAN_IP:-192.168.50.1}
SSID=${SSID:-Gray-Router}
LAN_CON=${LAN_CON:-GrayRouter-LAN}

# 网口角色：config.env 里没写就按自动探测（roles.sh），再兜底常见名字
if [ -z "${WAN_IF:-}" ] || [ -z "${LAN_IF:-}" ]; then
  _roles=$(/usr/local/bin/grayrouter-roles.sh get 2>/dev/null || true)
  [ -z "${WAN_IF:-}" ] && WAN_IF=$(printf '%s\n' "$_roles" | sed -n 's/^WAN_IF=//p')
  [ -z "${LAN_IF:-}" ] && LAN_IF=$(printf '%s\n' "$_roles" | sed -n 's/^LAN_IF=//p')
fi
WAN_IF=${WAN_IF:-eno1}; LAN_IF=${LAN_IF:-wlp2s0}

C_Y=$'\033[33m'; C_N=$'\033[0m'
[ "$(id -u)" -ne 0 ] && { echo "需要 root：sudo $0"; exit 2; }
echo "停止 $LAN_CON ..."
nmcli connection down "$LAN_CON" >/dev/null 2>&1 || true
[ -r "$GR_ETC/ttl.nft" ] && nft delete table ip grayrouter 2>/dev/null || true
[ -x "$GR_OPT/bin/ua3f-rules.sh" ] && bash "$GR_OPT/bin/ua3f-rules.sh" clear >/dev/null 2>&1 || true
printf "${C_Y}已停止${C_N}（上游网络未改动）\n"
