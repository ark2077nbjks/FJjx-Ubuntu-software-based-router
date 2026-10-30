#!/usr/bin/env bash
# ============================================================
#  grayrouter-rescue.sh —— 兜底恢复（一键把上网救回来）
#    1) 关掉自动切换，避免边修边打架
#    2) 删掉所有我们加的 metric 50 默认路由，交回 NetworkManager
#    3) 主线路由没了就重连主 WAN 的连接
#    4) 校园网有认证工具就重新认证一次
#    5) 自检：从主 WAN 探公网，报告结果
#  只动「我们自己加的」和「坏的」路由，不碰 LAN/AP
# ============================================================
set -uo pipefail
LOG=/var/log/grayrouter-wanfail.log
ROLES=/usr/local/bin/grayrouter-roles.sh
WF=/usr/local/bin/grayrouter-wanfail.sh
log() { echo "$(date '+%F %T') [rescue] $*" >> "$LOG" 2>/dev/null; }
[ "$(id -u)" -eq 0 ] || { echo "需要 root"; exit 1; }

echo "① 关闭自动切换（避免一边修一边被切走）"
[ -x "$WF" ] && "$WF" set disable >/dev/null 2>&1 && log "已关闭自动切换"

echo "② 清掉我们加的默认路由（metric 50）"
ip route show default | grep -F "metric 50" | while IFS= read -r line; do
  ip route del $line 2>/dev/null && { echo "   删除：$line"; log "删除路由 $line"; }
done

echo "③ 确认主 WAN 有默认路由"
WAN=$([ -x "$ROLES" ] && "$ROLES" get 2>/dev/null | sed -n 's/^WAN_IF=//p')
[ -z "${WAN:-}" ] && WAN=eno1
if ip route show default | grep -q "dev $WAN"; then
  echo "   已在用 $WAN"
else
  con=$(nmcli -g GENERAL.CONNECTION dev show "$WAN" 2>/dev/null | head -1)
  if [ -n "${con:-}" ]; then
    nmcli con up "$con" >/dev/null 2>&1 && { echo "   重连 $con"; log "重连 $con"; }
  fi
  ip route show default | grep -q "dev $WAN" || echo "   !! $WAN 还是没默认路由（网线/认证问题）"
fi

echo "③.5 主 WAN 有没有 IP（没有就拉起来，本脚本是 root，不用密码）"
if ! ip -4 addr show dev "$WAN" 2>/dev/null | grep -q 'inet '; then
  con=$(nmcli -g GENERAL.CONNECTION dev show "$WAN" 2>/dev/null | head -1)
  if [ -n "${con:-}" ]; then
    nmcli con up "$con" >/dev/null 2>&1 && echo "   重连 $con"
  else
    nmcli device connect "$WAN" >/dev/null 2>&1 && echo "   激活 $WAN"
  fi
  for i in 1 2 3 4 5 6; do
    ip -4 addr show dev "$WAN" 2>/dev/null | grep -q 'inet ' && break
    sleep 2
  done
fi
if ip -4 addr show dev "$WAN" 2>/dev/null | grep -q 'inet '; then
  echo "   $WAN 地址：$(ip -4 -o addr show dev "$WAN" | awk '{print $4}' | head -1)"
else
  echo "   !! $WAN 还是没地址 —— 检查网线有没有插好"
fi

echo "④ 校园网重新认证（有 bitsrun 就跑一次）"
for b in /home/*/bitsrun /root/bitsrun; do
  [ -x "$b" ] || continue
  d=$(dirname "$b")
  cfg="$d/Config.yaml"
  [ -f "$cfg" ] || continue
  ( cd "$d" && "./bitsrun" -config "$cfg" -interface "$WAN" >/dev/null 2>&1 ) \
    && { echo "   已用 $d 重新认证 $WAN"; log "重新认证 $WAN"; } || echo "   认证没成功（可能已在线）"
  break
done

echo "⑤ 自检"
lip=$(ip -4 -o addr show dev "$WAN" 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1)
if [ -n "${lip:-}" ] && ping -I "$lip" -c 1 -W 3 -q 223.5.5.5 >/dev/null 2>&1; then
  echo "   √ 已恢复：$WAN（$lip）能上公网"
  log "恢复成功 $WAN"
else
  echo "   × 还是不通：检查网线 / 校园网是否被别的设备占用（一号一设备）"
  log "恢复失败 $WAN"
fi

echo
echo "默认路由现在的样子："
ip route show default
