#!/bin/bash
# ============================================================
#  grayrouter-sqm.sh —— 智能队列（cake），治「一有人下载就打游戏卡」
#
#  用法：
#    grayrouter-sqm.sh status              看当前状态（只读）
#    grayrouter-sqm.sh measure             测一下实际带宽，给出建议值（约 10 秒）
#    grayrouter-sqm.sh apply [下M] [上M]   启用整形（不填则用配置文件里的值）
#    grayrouter-sqm.sh clear               关闭整形（恢复默认排队）
#
#  原理跟 OpenWrt 的 sqm-scripts 一样：
#   上行 —— 直接在 WAN 口挂 cake
#   下行 —— 借 IFB 虚拟网卡把入向流量拐进来再整形
# ============================================================
set -uo pipefail

CONF=/etc/grayrouter/sqm.env
IFB=ifb-gray
QRUN=/run/grayrouter

say(){ printf '%s\n' "$*"; }
die(){ printf '✗ %s\n' "$*" >&2; exit 1; }
need_root(){ [ "$(id -u)" -eq 0 ] || die "要 sudo：sudo $0 $*"; }

wan_if(){
  ip route show default 2>/dev/null | awk '{print $5; exit}'
}

load_conf(){
  DOWN=0; UP=0; ENABLED=0
  [ -r "$CONF" ] || return 0
  # shellcheck disable=SC1090
  . "$CONF" 2>/dev/null || true
  DOWN=${DOWN_KBIT:-0}; UP=${UP_KBIT:-0}; ENABLED=${SQM_ENABLED:-0}
}

save_conf(){
  mkdir -p "$(dirname "$CONF")"
  cat > "$CONF" <<EOF
# GrayRouter 智能队列配置（由 grayrrouter-sqm.sh 写入，改完 apply 生效）
SQM_ENABLED=$1
DOWN_KBIT=$2
UP_KBIT=$3
EOF
}

do_status(){
  load_conf
  local wan; wan=$(wan_if)
  say "WAN 网口：${wan:-未找到}"
  say "配置：启用=${ENABLED} 下行=${DOWN}kbit 上行=${UP}kbit"
  say ""
  if [ -n "${wan:-}" ]; then
    say "--- ${wan} 出向（管上传）---"
    tc qdisc show dev "$wan" 2>/dev/null | sed 's/^/  /'
  fi
  if ip link show "$IFB" >/dev/null 2>&1; then
    say "--- ${IFB} 入向（管下载）---"
    tc qdisc show dev "$IFB" 2>/dev/null | sed 's/^/  /'
  else
    say "--- ${IFB}：未创建（说明整形没开）---"
  fi
}

do_measure(){
  command -v curl >/dev/null || die "没有 curl"
  local url="https://mirrors.aliyun.com/ubuntu/ls-lR.gz"
  say "测 10 秒（4 线程，只测下行）…"
  local tmp; tmp=$(mktemp -d)
  for i in 1 2 3 4; do
    curl -s -o /dev/null --max-time 10 "$url" \
      -w "%{speed_download}\n" > "$tmp/$i" 2>/dev/null &
  done
  wait
  local sum=0 v
  for f in "$tmp"/*; do v=$(cat "$f" 2>/dev/null); [ -n "${v:-}" ] && sum=$(awk -v a="$sum" -v b="$v" 'BEGIN{print a+b}'); done
  rm -rf "$tmp"
  local mbit; mbit=$(awk -v b="$sum" 'BEGIN{printf "%.1f", b*8/1000000}')
  awk -v m="$mbit" 'BEGIN{exit !(m<1)}' && die "测速几乎没流量（网络不通？），别拿这个值去设带宽"
  local rec; rec=$(awk -v m="$mbit" 'BEGIN{printf "%d", m*0.9*1000}')
  say "实测下行：${mbit} Mbit/s"
  say "建议整形值：${rec} kbit（约 90%）"
  say "上行不知道就先填跟下行一样（宁可高估：不会误限速，只是上行整形效果弱）"
  say ""
  say "启用示例：sudo $0 apply $(awk -v r="$rec" 'BEGIN{printf "%d", r/1000}') $(awk -v r="$rec" 'BEGIN{printf "%d", r/1000}')"
}

do_apply(){
  need_root "$@"
  local wan; wan=$(wan_if)
  [ -n "${wan:-}" ] || die "找不到默认路由，接不上网？"
  load_conf
  local down=${1:-$((DOWN/1000))} up=${2:-$((UP/1000))}
  [ "$down" -gt 0 ] 2>/dev/null || die "下行带宽没给：apply <下行Mbit> [上行Mbit]"
  [ "$up" -gt 0 ] 2>/dev/null || up=$down

  modprobe sch_cake 2>/dev/null || true
  modprobe ifb 2>/dev/null || true

  # 上行：WAN 口出向
  if ! tc qdisc replace dev "$wan" root cake bandwidth "${up}mbit" besteffort dual-srchost nat >/dev/null 2>&1; then
    tc qdisc del dev "$wan" root >/dev/null 2>&1 || true    # mq 多队列时先拆再加
    tc qdisc add dev "$wan" root cake bandwidth "${up}mbit" besteffort dual-srchost nat >/dev/null || die "上行整形失败"
  fi
  say "✓ 上行 ${up} Mbit → ${wan}"

  # 下行：IFB 拐弯
  ip link show "$IFB" >/dev/null 2>&1 || ip link add "$IFB" type ifb
  ip link set "$IFB" up
  tc qdisc del dev "$wan" ingress 2>/dev/null || true
  tc qdisc add dev "$wan" handle ffff: ingress >/dev/null || die "入向钩子失败"
  tc filter add dev "$wan" parent ffff: protocol all u32 match u32 0 0 \
     action mirred egress redirect dev "$IFB" >/dev/null || die "入向重定向失败"
  tc qdisc replace dev "$IFB" root cake bandwidth "${down}mbit" besteffort dual-dsthost nat >/dev/null || die "入向整形失败"
  say "✓ 下行 ${down} Mbit → ${IFB}"

  save_conf 1 $((down*1000)) $((up*1000))
  mkdir -p "$QRUN"; echo "on $down $up" > "$QRUN/sqm.state" 2>/dev/null || true
  say ""
  do_status
}

do_clear(){
  need_root "$@"
  local wan; wan=$(wan_if)
  [ -n "${wan:-}" ] && tc qdisc del dev "$wan" root 2>/dev/null || true
  [ -n "${wan:-}" ] && tc qdisc del dev "$wan" ingress 2>/dev/null || true
  ip link show "$IFB" >/dev/null 2>&1 && ip link del "$IFB" 2>/dev/null || true
  load_conf
  save_conf 0 "${DOWN:-0}" "${UP:-0}"
  say "✓ 已关闭整形，恢复系统默认排队"
  rm -f "$QRUN/sqm.state" 2>/dev/null || true
}

do_restore(){
  load_conf
  [ "${ENABLED:-0}" = "1" ] || { say "未启用，跳过"; exit 0; }
  [ "${DOWN:-0}" -gt 0 ] 2>/dev/null || { say "配置里没有带宽，跳过"; exit 0; }
  # 开机时 network-online 可能比 DHCP 早几秒：等默认路由出现（最多 30 秒）
  local i
  for i in $(seq 1 30); do
    [ -n "$(wan_if)" ] && break
    sleep 1
  done
  [ -n "$(wan_if)" ] || { say "等不到默认路由，先不整形（稍后可 apply）"; exit 0; }
  do_apply $((DOWN/1000)) $((UP/1000))
}

do_flush(){  # 出口换了口：把旧口上的整形全拆掉，配置保持不变
  need_root "$@"
  local d
  for d in /sys/class/net/*; do
    d=$(basename "$d"); [ "$d" = "lo" ] && continue
    tc qdisc del dev "$d" root 2>/dev/null || true
    tc qdisc del dev "$d" ingress 2>/dev/null || true
  done
  ip link show "$IFB" >/dev/null 2>&1 && ip link del "$IFB" 2>/dev/null || true
  say "✓ 已清掉所有网口上的整形（配置保留）"
}


do_json(){
  load_conf
  local wan; wan=$(wan_if)
  local up_on=0 down_on=0
  if [ -n "${wan:-}" ] && tc qdisc show dev "$wan" 2>/dev/null | grep -q "cake "; then up_on=1; fi
  if ip link show "$IFB" >/dev/null 2>&1 && tc qdisc show dev "$IFB" 2>/dev/null | grep -q "cake "; then down_on=1; fi
  printf '{"enabled":%d,"down":%d,"up":%d,"wan":"%s","up_on":%d,"down_on":%d}\n' \
    "${ENABLED:-0}" "${DOWN:-0}" "${UP:-0}" "${wan:-}" "$up_on" "$down_on"
}

do_stats(){
  # 只读：返回每个方向的累计字节 / 丢包 / 队列积压，面板自己算速率
  load_conf
  local wan; wan=$(wan_if)
  local out="" k d s b dr bl
  for pair in "up:${wan:-}" "down:${IFB:-ifb-gray}"; do
    k=${pair%%:*}; d=${pair#*:}
    [ -n "$d" ] || continue
    s=$(tc -s qdisc show dev "$d" 2>/dev/null | tr '\n' ' ')
    b=$(printf '%s' "$s"  | sed -n 's/.*Sent \([0-9]*\) bytes.*/\1/p' | head -1)
    dr=$(printf '%s' "$s" | sed -n 's/.*dropped \([0-9]*\).*/\1/p' | head -1)
    bl=$(printf '%s' "$s" | sed -n 's/.*backlog \([0-9]*\)b.*/\1/p' | head -1)
    out="$out,\"${k}_bytes\":${b:-0},\"${k}_drops\":${dr:-0},\"${k}_backlog\":${bl:-0}"
  done
  printf '{"enabled":%s,"wan":"%s"%s}\n' "${ENABLED:-0}" "${wan:-}" "$out"
}

cmd=${1:-status}
shift || true
case "$cmd" in
  status)  do_status ;;
  measure) do_measure ;;
  apply)   do_apply "$@" ;;
  clear)   do_clear ;;
  restore) do_restore ;;
  flush)   do_flush ;;
  json)    do_json ;;
  stats)   do_stats ;;
  *) die "用法：$0 {status|measure|apply [下M] [上M]|clear|restore|flush|json}" ;;
esac
