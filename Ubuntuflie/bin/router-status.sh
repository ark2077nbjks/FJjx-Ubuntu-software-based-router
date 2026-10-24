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

C_R=$'\033[31m'; C_G=$'\033[32m'; C_Y=$'\033[33m'; C_B=$'\033[36m'; C_D=$'\033[2m'; C_N=$'\033[0m'
ok(){ printf "  ${C_G}✓${C_N} %s\n" "$*"; }
bad(){ printf "  ${C_R}✗${C_N} %s\n" "$*"; }
warn(){ printf "  ${C_Y}!${C_N} %s\n" "$*"; }
info(){ printf "  ${C_D}·${C_N} %s\n" "$*"; }
sec(){ printf "\n${C_B}【%s】${C_N}\n" "$*"; }

ip_of(){ ip -4 -br addr show "$1" 2>/dev/null | awk '{print $3}'; }
carrier(){ cat "/sys/class/net/$1/carrier" 2>/dev/null || echo 0; }

sec "模式"
printf "  拓扑 %s   WAN %s   LAN %s（%s）\n" "$TOPO" "${WAN_IF:-无线上游}" "$LAN_IF" "$LAN_TYPE"

sec "LAN / 热点"
if nmcli -t -f NAME,DEVICE connection show --active 2>/dev/null | grep -q "^${LAN_CON}:"; then
  ok "连接 $LAN_CON 活动"
else
  bad "连接 $LAN_CON 没起来（sudo systemctl start grayrouter）"
fi
lip=$(ip_of "$LAN_IF")
[ -n "$lip" ] && ok "LAN 地址 $lip" || bad "LAN 没有 IP"
[ "$LAN_TYPE" = "wifi-ap" ] && info "SSID $SSID（WPA2）"

sec "客户端"
clients=$(ip neigh show dev "$LAN_IF" 2>/dev/null | awk '/REACHABLE|STALE|DELAY/{print $1}' | grep -v '^fe80' | head -10)
if [ -n "$clients" ]; then
  echo "$clients" | while read -r c; do info "$c"; done
else
  info "暂时没有客户端"
fi

sec "WAN / 上游"
if [ -n "$WAN_IF" ]; then
  wip=$(ip_of "$WAN_IF")
  [ -n "$wip" ] && ok "WAN $WAN_IF $wip" || bad "WAN 没有 IP"
  [ "$(carrier "$WAN_IF")" = "1" ] && ok "网线已插" || bad "网线未插"
  info "速度 $(cat /sys/class/net/$WAN_IF/speed 2>/dev/null || echo '?')Mb/s"
else
  info "无线上游：$(nmcli -t -f GENERAL.CONNECTION device show "$(nmcli -t -f DEVICE,TYPE device | awk -F: '$2=="wifi"{print $1; exit}')" 2>/dev/null | cut -d: -f2)"
fi
gw=$(ip route show default 2>/dev/null | awk '/default/{print $3; exit}')
[ -n "$gw" ] && ok "默认网关 $gw" || bad "没有默认路由"

sec "转发 / 出网"
[ "$(cat /proc/sys/net/ipv4/ip_forward)" = "1" ] && ok "ip_forward=1" || bad "ip_forward=0"
code=$(curl -s -m 6 -o /dev/null -w '%{http_code}' https://www.baidu.com 2>/dev/null)
case "$code" in 2*|3*) ok "路由器能出网（HTTP $code）";; *) warn "路由器出网失败（$code）";; esac

sec "端口 / 冲突"
for p in 7890 1080 80 443; do
  ss -lnt 2>/dev/null | awk '{print $4}' | grep -qE "[:.]$p$" && info "端口 $p 占用中"
done

echo
