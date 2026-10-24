#!/usr/bin/env bash
# GrayRouter 自动探测：网卡分类 / AP 能力 / 拓扑候选 / 冲突
# shellcheck shell=bash

# 单独引用本文件时也能用（不依赖 common.sh）
command -v have >/dev/null 2>&1 || have() { command -v "$1" >/dev/null 2>&1; }

declare -a GR_WIRED GR_WIFI GR_APCAP GR_WAN_CAND
GR_DEF_IF=""; GR_DEF_GW=""; GR_WAN_KIND=""

gr_ap_capable() {
  local ifc=$1
  if have iw && iw list 2>/dev/null | awk '/Supported interface modes/,/^$/' | grep -qw AP; then
    echo yes; return
  fi
  local v; v=$(nmcli -g WIFI-PROPERTIES.AP device show "$ifc" 2>/dev/null | head -1)
  [ "$v" = "yes" ] && echo yes || echo no
}

# 枚举真实物理网卡，分类：有线 / 无线 / 支持AP
gr_scan_nics() {
  GR_WIRED=(); GR_WIFI=(); GR_APCAP=()
  local ifc
  for ifc in $(ls /sys/class/net); do
    case "$ifc" in
      lo|docker*|veth*|br-*|virbr*|tun*|tap*|wg*|vnet*) continue ;;
    esac
    [ -e "/sys/class/net/$ifc/device" ] || continue
    if [ -d "/sys/class/net/$ifc/wireless" ]; then
      GR_WIFI+=("$ifc")
      [ "$(gr_ap_capable "$ifc")" = yes ] && GR_APCAP+=("$ifc")
    else
      GR_WIRED+=("$ifc")
    fi
  done
}

# 默认路由 → 当前出口网卡与类型（有线/无线）
gr_scan_wan() {
  GR_DEF_IF=$(ip route show default 2>/dev/null | awk '/default/{print $5; exit}')
  GR_DEF_GW=$(ip route show default 2>/dev/null | awk '/default/{print $3; exit}')
  GR_WAN_KIND=""
  [ -z "$GR_DEF_IF" ] && return 0
  for i in "${GR_WIRED[@]:-}";   do [ "$i" = "$GR_DEF_IF" ] && GR_WAN_KIND="有线"; done
  for i in "${GR_WIFI[@]:-}";    do [ "$i" = "$GR_DEF_IF" ] && GR_WAN_KIND="无线"; done
}

# 这台机器能做哪些拓扑，输出 "A B" 形式
gr_topologies() {
  local t=""
  [ ${#GR_WIRED[@]} -ge 1 ] && [ ${#GR_APCAP[@]} -ge 1 ] && t="$t A"
  [ ${#GR_WIRED[@]} -ge 2 ] && t="$t B"
  [ ${#GR_WIFI[@]} -ge 1 ] && [ ${#GR_WIRED[@]} -ge 1 ] && t="$t C"
  echo "$t"
}

gr_iface_line() {
  local ifc=$1 kind link spd ip
  if [ -d "/sys/class/net/$ifc/wireless" ]; then
    kind="无线"
    link=$([ "$(cat /sys/class/net/$ifc/carrier 2>/dev/null || echo 0)" = 1 ] && echo 已启用 || echo 未启用)
    spd="—"
  else
    kind="有线"
    link=$([ "$(cat /sys/class/net/$ifc/carrier 2>/dev/null || echo 0)" = 1 ] && echo 已插线 || echo 未连接)
    spd=$(cat "/sys/class/net/$ifc/speed" 2>/dev/null || echo "")
    [ -n "$spd" ] && spd="${spd}Mb/s" || spd="—"
  fi
  ip=$(ip -4 -br addr show "$ifc" 2>/dev/null | awk '{print $3}')
  printf "  %-10s %-4s %-6s %-9s %s" "$ifc" "$kind" "$link" "$spd" "${ip:-—}"
  [ "$ifc" = "$GR_DEF_IF" ] && printf "   ← 当前出口（%s）" "${GR_WAN_KIND:-未知}"
  echo
}

# 端口占用（不含 53，那是 DNS）
gr_port_conflicts() {
  local p out=""
  if have ss; then
    for p in 7890 1080 8080 80 443; do
      ss -lnt 2>/dev/null | awk '{print $4}' | grep -qE "[:.]$p$" && out="$out $p"
    done
  fi
  echo "$out"
}
