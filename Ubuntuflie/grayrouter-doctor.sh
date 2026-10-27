#!/usr/bin/env bash
# ============================================================
#  GrayRouter 环境体检（只读，不改任何系统设置）
#  用途：自动识别这台 Ubuntu 有什么网卡、能做什么拓扑
#  用法：bash grayrouter-doctor.sh
# ============================================================
set -u

G='\033[32m'; R='\033[31m'; Y='\033[33m'; B='\033[36m'; D='\033[2m'; N='\033[0m'
ok()   { printf "  ${G}✓${N} %s\n" "$*"; }
bad()  { printf "  ${R}✗${N} %s\n" "$*"; }
warn() { printf "  ${Y}!${N} %s\n" "$*"; }
info() { printf "  ${D}·${N} %s\n" "$*"; }
sec()  { printf "\n${B}【%s】${N}\n" "$*"; }

# ---------- 1. 系统 ----------
sec "系统"
. /etc/os-release 2>/dev/null || true
printf "  发行版    %s %s\n" "${NAME:-?}" "${VERSION_ID:-?}"
printf "  内核      %s\n" "$(uname -r)"
printf "  架构      %s    CPU %s 核    内存 %s\n" "$(uname -m)" "$(nproc)" \
       "$(awk '/MemTotal/{printf "%.1fG", $2/1048576}' /proc/meminfo)"
[ "$(id -u)" -eq 0 ] && info "当前是 root" || warn "当前不是 root（体检不需要，安装时才要）"

# ---------- 2. 依赖 ----------
sec "依赖"
for c in nmcli nft ip iw ss; do
  if command -v "$c" >/dev/null 2>&1; then ok "$c"; else bad "$c 未安装"; fi
done
command -v hostapd >/dev/null 2>&1 && info "hostapd 已装（本方案用 NetworkManager 的 AP 模式，不需要它）" || true

# ---------- 3. 网卡清单 ----------
sec "网卡清单"
WIRED=(); WIFI=(); AP_OK=(); VIRTUAL=()
declare -A IF_DESC IF_STATE IF_SPEED IF_IP IF_CARRIER

have_ap_mode() {
  local ifc=$1
  if command -v iw >/dev/null 2>&1; then
    if iw list 2>/dev/null | awk '/Supported interface modes/,/^$/' | grep -qw AP; then
      echo yes; return
    fi
  fi
  # 退回 NetworkManager 的属性
  local v
  v=$(nmcli -g WIFI-PROPERTIES.AP device show "$ifc" 2>/dev/null | head -1)
  [ "$v" = "yes" ] && echo yes || echo no
}

for ifc in $(ls /sys/class/net); do
  case "$ifc" in
    lo|docker*|veth*|br-*|virbr*|tun*|tap*|wg*) VIRTUAL+=("$ifc"); continue ;;
  esac
  [ -e "/sys/class/net/$ifc/device" ] || { VIRTUAL+=("$ifc"); continue; }

  local_type="wired"
  if [ -d "/sys/class/net/$ifc/wireless" ]; then local_type="wifi"; fi

  carrier=$(cat "/sys/class/net/$ifc/carrier" 2>/dev/null || echo 0)
  speed=$(cat "/sys/class/net/$ifc/speed" 2>/dev/null || echo "")
  ipv4=$(ip -4 -br addr show "$ifc" 2>/dev/null | awk '{print $3}')
  state=$(nmcli -g GENERAL.STATE device show "$ifc" 2>/dev/null | head -1)
  desc=$(nmcli -g GENERAL.HWADDR device show "$ifc" 2>/dev/null | head -1)

  if [ "$local_type" = wifi ]; then
    link_txt=$([ "$carrier" = "1" ] && echo 已启用 || echo 未启用)
    spd_txt="—"
  else
    link_txt=$([ "$carrier" = "1" ] && echo 已插线 || echo 未连接)
    spd_txt=$([ -n "$speed" ] && echo "${speed}Mb/s" || echo "—")
  fi
  printf "  %-10s %-6s %-8s 速度:%-9s IP:%-18s\n" \
    "$ifc" "$([ "$local_type" = wifi ] && echo 无线 || echo 有线)" \
    "$link_txt" "$spd_txt" "${ipv4:-—}"

  if [ "$local_type" = "wired" ]; then
    WIRED+=("$ifc")
    info "  → 可当 WAN（有线）或 LAN（有线）"
  else
    WIFI+=("$ifc")
    ap=$(have_ap_mode "$ifc")
    if [ "$ap" = "yes" ]; then
      AP_OK+=("$ifc"); info "  → 支持 AP 模式，可当 LAN（无线热点）"
    else
      warn "  → 不支持 AP 模式，只能当 WAN（连别人 WiFi）"
    fi
  fi
done 2>/dev/null
[ ${#VIRTUAL[@]} -gt 0 ] && info "忽略虚拟接口：${VIRTUAL[*]}"

# ---------- 4. 默认路由 / 出口 ----------
sec "当前出网"
DEF_IF=$(ip route show default 2>/dev/null | awk '/default/{print $5; exit}')
DEF_GW=$(ip route show default 2>/dev/null | awk '/default/{print $3; exit}')
if [ -n "${DEF_IF:-}" ]; then
  ok "默认路由 $DEF_IF → 网关 $DEF_GW"
  case " ${WIFI[*]:-} " in *" $DEF_IF "*) info "出口类型：无线" ;; esac
  case " ${WIRED[*]:-} " in *" $DEF_IF "*) info "出口类型：有线" ;; esac
else
  warn "没有默认路由（当前可能没联网）"
fi
printf "  ip_forward = %s\n" "$(cat /proc/sys/net/ipv4/ip_forward 2>/dev/null)"

# ---------- 5. 已有的 AP 连接 ----------
sec "已有无线连接 / 热点"
nmcli -t -f NAME,TYPE,DEVICE connection show --active 2>/dev/null | while IFS=: read -r n t d; do
  case "$t" in
    wifi) 
      mode=$(nmcli -g 802-11-wireless.mode connection show "$n" 2>/dev/null | head -1)
      if [ "$mode" = "ap" ]; then info "$n  [AP 热点]  设备 $d"; else info "$n  [客户端]  设备 $d"; fi ;;
    802-11-wireless)
      mode=$(nmcli -g 802-11-wireless.mode connection show "$n" 2>/dev/null | head -1)
      if [ "$mode" = "ap" ]; then info "$n  [AP 热点]  设备 $d"; else info "$n  [客户端]  设备 $d"; fi ;;
    ethernet) info "$n  [有线]  设备 $d" ;;
  esac
done

# ---------- 6. 端口冲突 ----------
sec "端口占用（7890/1080/80/443；53 留给 DNS 不动）"
found=0
if command -v ss >/dev/null 2>&1; then
  for p in 7890 1080 80 443; do
    if ss -lnt 2>/dev/null | awk '{print $4}' | grep -qE "[:.]$p$"; then
      warn "端口 $p 已被占用"; found=1
    fi
  done
fi
[ "$found" -eq 0 ] && ok "没有冲突端口"

# ---------- 7. 拓扑判定 ----------
sec "这台机器能做哪些拓扑"
n_wired=${#WIRED[@]}; n_wifi=${#WIFI[@]}; n_ap=${#AP_OK[@]}

A=no; B=no; C=no
[ "$n_wired" -ge 1 ] && [ "$n_ap" -ge 1 ] && A=yes
[ "$n_wired" -ge 2 ] && B=yes
[ "$n_wifi" -ge 1 ] && [ "$n_wired" -ge 1 ] && C=yes

verdict() { [ "$1" = yes ] && ok "$2" || bad "$2"; }

verdict "$A" "A  有线 WAN → 无线 AP 当 LAN        （需要 1 有线 + 1 支持 AP 的无线）"
verdict "$B" "B  有线 WAN → 有线 LAN              （需要 2 块有线网卡）"
verdict "$C" "C  无线 WAN（连别人 WiFi）→ 有线 LAN（需要 1 无线 + 1 有线）"

echo
if [ "$A" = yes ] || [ "$B" = yes ] || [ "$C" = yes ]; then
  printf "  ${G}结论：可以装。${N}建议拓扑 "
  [ "$B" = yes ] && printf "B（双有线最稳）" || { [ "$A" = yes ] && printf "A" || printf "C"; }
  echo
else
  printf "  ${R}结论：不满足任何拓扑，不能装软路由。${N}\n"
  printf "  ${D}需要至少两块网口（有线/无线混搭也行）${N}\n"
fi

echo
printf "${D}报告结束。把这段输出发给部署方即可判断能不能装。${N}\n"
