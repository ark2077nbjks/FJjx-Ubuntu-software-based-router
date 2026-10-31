#!/usr/bin/env bash
# 给 Windows 端「网络面板」读的机器可读状态（KEY=VALUE，无中文，便于解析）
# 网口全部自动探测：
#   WAN_IF = 默认路由出口；没有默认路由时取第一个「已插线的有线口」
#   LAN_IF = AP 模式网口；没有 AP 时取第一个 192.168.x 地址的口；再不行退回 WAN_IF

# 优先用角色解析（支持手动指定）；没装就退回自动探测
if [ -x /usr/local/bin/grayrouter-roles.sh ]; then
  _R=$(/usr/local/bin/grayrouter-roles.sh get 2>/dev/null)
  WAN_IF=$(printf '%s\n' "$_R" | sed -n 's/^WAN_IF=//p')
  LAN_IF=$(printf '%s\n' "$_R" | sed -n 's/^LAN_IF=//p')
fi
[ -n "${WAN_IF:-}" ] || WAN_IF=$(ip -o -4 route show default 2>/dev/null | awk '{print $5; exit}')
if [ -z "$WAN_IF" ]; then
  for d in /sys/class/net/*; do
    n=${d##*/}
    [ "$n" = "lo" ] && continue
    [ -e "$d/device" ] || continue
    [ -e "$d/wireless" ] && continue
    [ "$(cat "$d/carrier" 2>/dev/null)" = "1" ] && { WAN_IF=$n; break; }
  done
fi

[ -n "${LAN_IF:-}" ] || LAN_IF=$(for i in $(iw dev 2>/dev/null | sed -n 's/^Interface //p'); do
           [ "$(iw dev "$i" info 2>/dev/null | awk '/type/{print $2}')" = "AP" ] && { echo "$i"; break; }
         done)
[ -z "$LAN_IF" ] && LAN_IF=$(ip -4 -o addr show 2>/dev/null | awk '$4 ~ /^192\.168\./ {print $2; exit}')
[ -z "$LAN_IF" ] && LAN_IF=$WAN_IF

WAN_IP=$(ip -4 -br addr show "$WAN_IF" 2>/dev/null | awk '{print $3}')
CARRIER=$(cat /sys/class/net/"$WAN_IF"/carrier 2>/dev/null)
SPEED=$(cat /sys/class/net/"$WAN_IF"/speed 2>/dev/null)
AP_TYPE=$(iw dev "$LAN_IF" info 2>/dev/null | awk '/type/{print $2}')
AP_SSID=$(iw dev "$LAN_IF" info 2>/dev/null | awk '/ssid/{print $2}')
AP_IP=$(ip -4 -br addr show "$LAN_IF" 2>/dev/null | awk '{print $3}')
BOT=$(pgrep -f '[.]venv/bin/python bot.py' | head -1)
PORT=$(ss -tln 2>/dev/null | grep -c ':8080')
UA=$(systemctl is-active ua3f 2>/dev/null)
CLIENTS=$(ip neigh show 2>/dev/null | awk '$1 ~ /^192\.168\./ {print $1}' | sort -u | paste -sd, -)
HTTPS=$(curl -s -m 5 -o /dev/null -w '%{http_code}' https://www.baidu.com 2>/dev/null)
EGRESS=$(curl -s -m 5 http://ip.3322.net 2>/dev/null)
FWD=$(sysctl -n net.ipv4.ip_forward 2>/dev/null)
UPTIME=$(uptime -p 2>/dev/null | sed 's/^up //')

# —— 系统健康（CPU 负载 / 内存 / 温度 / 磁盘）——
LOAD1=$(awk '{print $1}' /proc/loadavg 2>/dev/null)
CORES=$(nproc 2>/dev/null || echo 1)
MEM_LINE=$(awk '/^MemTotal:/{t=$2} /^MemAvailable:/{a=$2} END{if(t>0){u=t-a; printf "%d %d %d", u/1024, t/1024, u*100/t}}' /proc/meminfo 2>/dev/null)
MEM_USED=${MEM_LINE%% *}
_rest=${MEM_LINE#* }; MEM_TOTAL=${_rest%% *}; MEM_PCT=${_rest#* }
[ "$MEM_LINE" = "$MEM_PCT" ] && MEM_PCT=""
TEMP_MILLI=$(for z in /sys/class/thermal/thermal_zone*/temp; do cat "$z" 2>/dev/null; done | sort -n | tail -1)
TEMP_C=$(( ${TEMP_MILLI:-0} / 1000 ))
DISK_PCT=$(df -h / 2>/dev/null | awk 'NR==2{print $5}')
DISK_FREE=$(df -h / 2>/dev/null | awk 'NR==2{print $4}')

echo "WAN_IF=$WAN_IF"
echo "LAN_IF=$LAN_IF"
echo "WAN_IP=$WAN_IP"
echo "CARRIER=$CARRIER"
echo "SPEED=$SPEED"
echo "AP_TYPE=$AP_TYPE"
echo "AP_SSID=$AP_SSID"
echo "AP_IP=$AP_IP"
echo "BOT=$BOT"
echo "PORT=$PORT"
echo "UA3F=$UA"
echo "CLIENTS=$CLIENTS"
echo "HTTPS=$HTTPS"
echo "EGRESS=$EGRESS"
echo "FWD=$FWD"
echo "UPTIME=$UPTIME"
echo "LOAD1=${LOAD1:-}"
echo "CORES=${CORES:-}"
echo "MEM_USED_MB=${MEM_USED:-}"
echo "MEM_TOTAL_MB=${MEM_TOTAL:-}"
echo "MEM_PCT=${MEM_PCT:-}"
echo "TEMP_C=${TEMP_C:-}"
echo "DISK_PCT=${DISK_PCT:-}"
echo "DISK_FREE=${DISK_FREE:-}"

# —— 看门狗状态（grayrouter-watch）——
WJ=/run/grayrouter/watch.json
if [ -r "$WJ" ]; then
  W_STATE=$(sed -n 's/.*"verdict":"\([a-z]*\)".*/\1/p' "$WJ")
  W_TS=$(sed -n 's/.*"ts":"\([^"]*\)".*/\1/p' "$WJ")
  W_MODE=$(sed -n 's/.*"mode":"\([a-z]*\)".*/\1/p' "$WJ")
  W_ACTED=$(sed -n 's/.*"acted":\([a-z]*\).*/\1/p' "$WJ")
  W_HOT=$(sed -n 's/.*"hot":\([0-9-]*\).*/\1/p' "$WJ")
else
  W_STATE=none; W_TS=""; W_MODE=""; W_ACTED=false; W_HOT=0
fi
# —— 并发连接数 / DHCP 池 / 整形 / 静态租约（状态页「当前配置」用）——
CONNTRACK=$(cat /proc/sys/net/netfilter/nf_conntrack_count 2>/dev/null)
CONNTRACK_MAX=$(cat /proc/sys/net/netfilter/nf_conntrack_max 2>/dev/null)
# 租约文件是 root 600，普通用户读不到 → 用邻居表在线设备数（与「客户端」同口径）
LEASES=$(printf '%s' "${CLIENTS:-}" | tr ',' '\n' | grep -c . )
POOL=$(ip -4 -o addr show 2>/dev/null | awk '$4 ~ /^192\.168\./ {split($4,a,"/"); if(a[2]+0>=16) printf "%d\n", 2^(32-a[2])-3}' | awk '{s+=$1} END{printf "%d", s+0}')
SQM_ON=0; SQM_DOWN=0; SQM_UP=0
if [ -r /etc/grayrouter/sqm.env ]; then
  . /etc/grayrouter/sqm.env 2>/dev/null || true
  SQM_ON=${SQM_ENABLED:-0}; SQM_DOWN=${DOWN_KBIT:-0}; SQM_UP=${UP_KBIT:-0}
fi
_SL=/etc/NetworkManager/dnsmasq-shared.d/10-static-leases.conf
STATIC_LEASES=0
[ -r "$_SL" ] && STATIC_LEASES=$(grep -c '^dhcp-host=' "$_SL" 2>/dev/null)
WANFAIL=$(systemctl is-active grayrouter-wanfail.timer 2>/dev/null)
[ -n "$WANFAIL" ] || WANFAIL=inactive

echo "WATCH_STATE=${W_STATE:-none}"
echo "WATCH_TS=${W_TS}"
echo "WATCH_MODE=${W_MODE}"
echo "WATCH_ACTED=${W_ACTED:-false}"
echo "WATCH_HOT=${W_HOT:-0}"
echo "CONNTRACK=${CONNTRACK:-}"
echo "CONNTRACK_MAX=${CONNTRACK_MAX:-}"
echo "LEASES=${LEASES:-0}"
echo "POOL=${POOL:-0}"
echo "SQM_ON=${SQM_ON:-0}"
echo "SQM_DOWN=${SQM_DOWN:-0}"
echo "SQM_UP=${SQM_UP:-0}"
echo "STATIC_LEASES=${STATIC_LEASES:-0}"
echo "WANFAIL=${WANFAIL:-inactive}"

# —— IPv6（校园网常见：有 IPv6 就能免费提速 / 直连）——
IPV6_ADDR=$(ip -6 -o addr show scope global 2>/dev/null | awk '{print $4}' | grep -v '^fe80' | head -1)
IPV6_IF=$(ip -6 -o addr show scope global 2>/dev/null | awk '{print $2; exit}')
IPV6_OK=0
if [ -n "$IPV6_ADDR" ]; then
  if ping6 -c 1 -W 2 2400:3200::1 >/dev/null 2>&1 || curl -6 -s -m 4 -o /dev/null https://ipv6.baidu.com 2>/dev/null; then
    IPV6_OK=1
  fi
fi
IPV6_DNS=$(awk '/^nameserver/ && /:/ {print $2; exit}' /etc/resolv.conf 2>/dev/null)
echo "IPV6_ADDR=${IPV6_ADDR:-}"
echo "IPV6_IF=${IPV6_IF:-}"
echo "IPV6_OK=${IPV6_OK}"
echo "IPV6_DNS=${IPV6_DNS:-}"

# —— 硬件资产（只读，任何 Linux 都能取）——
HW_CPU=$(LC_ALL=C lscpu 2>/dev/null | sed -n 's/^Model name:[[:space:]]*//p' | head -1)
HW_CORES=$(nproc 2>/dev/null)
HW_THREADS=$(LC_ALL=C lscpu 2>/dev/null | sed -n 's/^CPU(s):[[:space:]]*//p' | head -1)
HW_BOARD=$( { cat /sys/class/dmi/id/board_vendor 2>/dev/null; printf ' '; cat /sys/class/dmi/id/board_name 2>/dev/null; } | sed 's/^ *//; s/ *$//')
HW_VENDOR=$(cat /sys/class/dmi/id/sys_vendor 2>/dev/null)
HW_BIOS=$(cat /sys/class/dmi/id/bios_version 2>/dev/null)
HW_KERNEL=$(uname -r)
HW_DISTRO=$( ( . /etc/os-release 2>/dev/null; printf '%s' "${PRETTY_NAME:-}" ) )
HW_DISK=$(LC_ALL=C lsblk -dno NAME,MODEL,SIZE 2>/dev/null | awk '$1 !~ /^(loop|ram|sr)/ {print $2" "$3; exit}')
[ -n "$HW_DISK" ] || HW_DISK=$(LC_ALL=C lsblk -dno NAME,SIZE 2>/dev/null | awk '$1 !~ /^(loop|ram|sr)/ {print $1" "$2; exit}')
echo "HW_CPU=${HW_CPU:-}"
echo "HW_CORES=${HW_CORES:-}"
echo "HW_THREADS=${HW_THREADS:-}"
echo "HW_BOARD=${HW_BOARD:-}"
echo "HW_VENDOR=${HW_VENDOR:-}"
echo "HW_BIOS=${HW_BIOS:-}"
echo "HW_KERNEL=${HW_KERNEL:-}"
echo "HW_DISTRO=${HW_DISTRO:-}"
echo "HW_DISK=${HW_DISK:-}"
