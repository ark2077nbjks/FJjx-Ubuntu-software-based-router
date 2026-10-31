#!/usr/bin/env bash
# grayrouter-limit.sh —— 每设备下行限速（对标 UniFi / 爱快「终端限速」）
# 只在 LAN 侧网卡上按「目的 IP」做 HTB 分类，不影响 WAN 整形（SQM）。
# 用法：
#   list                      列出当前规则
#   set 192.168.50.100 8000   限到 8000 kbit（约 8 Mbit）
#   del 192.168.50.100        取消这台
#   clear                     全部取消
#   apply                     按配置文件重建 tc 规则（开机/重启后用）
set -u
CONF=/etc/grayrouter/limits.conf       # 每行：IP kbit
LANIFS=$(for i in $(ip -4 -o addr show 2>/dev/null | awk '$4 ~ /^192\.168\./ {print $2}' | sort -u); do echo "$i"; done)

need_root() { [ "$(id -u)" = "0" ] || { echo "需要 root：sudo bash $0 $*"; exit 1; }; }

lan_ifs() {
  [ -n "${LANIFS:-}" ] && { echo "$LANIFS"; return; }
  ip -4 -o addr show 2>/dev/null | awk '$4 ~ /^192\.168\./ {print $2}' | sort -u
}

do_list() {
  if [ ! -r "$CONF" ] || [ ! -s "$CONF" ]; then echo "没有限速规则"; return 0; fi
  printf '%-16s %10s\n' "IP" "下行kbit"
  while read -r ip kb; do
    [ -z "${ip:-}" ] && continue
    printf '%-16s %10s\n' "$ip" "$kb"
  done < "$CONF"
}

do_clear() {
  drop_all
  : > "$CONF" 2>/dev/null || true
  echo "已清除全部设备限速"
}

drop_all() {
  local ifc
  for ifc in $(lan_ifs); do tc qdisc del dev "$ifc" root 2>/dev/null || true; done
}

do_apply() {
  drop_all
  [ -r "$CONF" ] || { echo "没有规则，已清空"; return 0; }

  local ifc idx=10 added=0
  for ifc in $(lan_ifs); do
    tc qdisc add dev "$ifc" root handle 1: htb default 99 2>/dev/null || continue
    tc class add dev "$ifc" parent 1: classid 1:99 htb rate 1000mbit ceil 1000mbit 2>/dev/null || true
    while read -r ip kb; do
      [ -z "${ip:-}" ] && continue
      case "$ip" in 192.168.*) ;; *) continue ;; esac
      tc class add dev "$ifc" parent 1: classid "1:$idx" htb rate "${kb}kbit" ceil "${kb}kbit" 2>/dev/null || true
      tc filter add dev "$ifc" parent 1: protocol ip prio 1 u32 match ip dst "$ip/32" flowid "1:$idx" 2>/dev/null || true
      idx=$((idx + 1)); added=$((added + 1))
    done < "$CONF"
  done
  echo "已应用：$added 条（网卡：$(lan_ifs | tr '\n' ' ')）"
}

do_set() {
  local ip="${1:-}" kb="${2:-}"
  case "$ip" in 192.168.*) ;; *) echo "IP 不合法：$ip"; exit 2 ;; esac
  case "$kb" in ''|*[!0-9]*) echo "带宽要写数字（kbit），例如 8000"; exit 2 ;; esac
  [ "$kb" -ge 64 ] || { echo "太小了，建议 ≥ 512"; exit 2; }
  mkdir -p "$(dirname "$CONF")"
  touch "$CONF"
  grep -v "^$ip " "$CONF" > "$CONF.tmp" 2>/dev/null || true
  printf '%s %s\n' "$ip" "$kb" >> "$CONF.tmp"
  mv "$CONF.tmp" "$CONF"
  do_apply >/dev/null
  echo "已限速：$ip → ${kb}kbit（约 $((kb / 1000)) Mbit）"
}

do_del() {
  local ip="${1:-}"
  [ -r "$CONF" ] || { echo "没有规则"; return 0; }
  grep -v "^$ip " "$CONF" > "$CONF.tmp" 2>/dev/null || true
  mv "$CONF.tmp" "$CONF"
  do_apply >/dev/null
  echo "已取消：$ip"
}

case "${1:-list}" in
  list) do_list ;;
  set) need_root; do_set "${2:-}" "${3:-}" ;;
  del) need_root; do_del "${2:-}" ;;
  clear) need_root; do_clear ;;
  apply) need_root; do_apply ;;
  *) echo "用法：$0 list|set <IP> <kbit>|del <IP>|clear|apply"; exit 2 ;;
esac
