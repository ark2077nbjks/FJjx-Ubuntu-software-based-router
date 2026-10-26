#!/usr/bin/env bash
# ============================================================
#  grayrrouter-roles.sh —— 网口角色解析（WAN / LAN-AP）
#    get           输出当前角色（自动探测，或手动指定）
#    set auto      恢复自动
#    set <wan> <lan>  手动指定（写 /etc/grayrouter/roles.env，需 root）
# ============================================================
set -uo pipefail
CONF=/etc/grayrouter/roles.env
MODE=auto; ROLE_WAN=""; ROLE_LAN=""
[ -f "$CONF" ] && . "$CONF" 2>/dev/null

# ---------- 自动探测 ----------
detect_wan() {
  local w
  w=$(ip -o -4 route show default 2>/dev/null | awk '{print $5; exit}')
  if [ -n "$w" ]; then echo "$w"; return; fi
  # 没有默认路由：取第一个「插了线的有线口」
  for i in /sys/class/net/*; do
    n=${i##*/}
    [ -e "$i/device" ] || continue
    [ -e "$i/wireless" ] && continue
    [ "$(cat "$i/carrier" 2>/dev/null)" = "1" ] && { echo "$n"; return; }
  done
}
detect_lan() {
  local n t
  for n in $(iw dev 2>/dev/null | sed -n 's/^Interface //p'); do
    t=$(iw dev "$n" info 2>/dev/null | awk '/type/{print $2}')
    [ "$t" = "AP" ] && { echo "$n"; return; }
  done
  # 没有 AP：取第一个 192.168.x 地址的口
  n=$(ip -4 -o addr show 2>/dev/null | awk '$4 ~ /^192\.168\./ {print $2; exit}')
  echo "${n:-}"
}

WAN_AUTO=$(detect_wan); LAN_AUTO=$(detect_lan)
WAN=$WAN_AUTO; LAN=$LAN_AUTO
if [ "$MODE" = "manual" ]; then
  [ -n "$ROLE_WAN" ] && [ -d "/sys/class/net/$ROLE_WAN" ] && WAN=$ROLE_WAN
  [ -n "$ROLE_LAN" ] && [ -d "/sys/class/net/$ROLE_LAN" ] && LAN=$ROLE_LAN
fi

case "${1:-get}" in
  get)
    echo "MODE=$MODE"
    echo "WAN_IF=$WAN"
    echo "LAN_IF=$LAN"
    echo "WAN_AUTO=$WAN_AUTO"
    echo "LAN_AUTO=$LAN_AUTO"
    # 候选网口：名字|有线/无线|插线(1/0)|AP/STA
    for i in /sys/class/net/*; do
      n=${i##*/}
      [ "$n" = "lo" ] && continue
      case "$n" in docker*|veth*|br-*|virbr*) continue ;; esac
      if [ -e "$i/wireless" ]; then kind=wifi; else kind=wired; fi
      car=$(cat "$i/carrier" 2>/dev/null || echo 0)
      [ "$kind" = "wifi" ] && car=1
      st=$(iw dev "$n" info 2>/dev/null | awk '/type/{print $2}')
      [ -z "$st" ] && st="-"
      ip4=$(ip -4 -o addr show dev "$n" 2>/dev/null | awk '{print $4; exit}')
      echo "IF=$n|$kind|$car|$st|${ip4:-}"
    done
    ;;
  set)
    want="${2:-auto}"
    [ "$(id -u)" -eq 0 ] || { echo "需要 root"; exit 1; }
    mkdir -p /etc/grayrouter
    if [ "$want" = "auto" ]; then
      printf 'MODE=auto\nROLE_WAN=""\nROLE_LAN=""\n' > "$CONF"
      echo "已恢复自动探测"
    else
      w="${2:-}"; l="${3:-}"
      [ -d "/sys/class/net/$w" ] || { echo "没有这个网口：$w"; exit 2; }
      [ -d "/sys/class/net/$l" ] || { echo "没有这个网口：$l"; exit 3; }
      printf 'MODE=manual\nROLE_WAN="%s"\nROLE_LAN="%s"\n' "$w" "$l" > "$CONF"
      echo "已设为手动：WAN=$w LAN=$l"
    fi
    ;;
  *) echo "用法: $0 get | set auto | set <wan> <lan>"; exit 1 ;;
esac
