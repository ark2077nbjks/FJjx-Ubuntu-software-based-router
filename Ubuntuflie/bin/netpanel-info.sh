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
CLIENTS=$(ip neigh show dev "$LAN_IF" 2>/dev/null | awk '$4=="REACHABLE"||$4=="STALE"{print $1}' | grep -v '^fe80' | paste -sd, -)
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
echo "WATCH_STATE=${W_STATE:-none}"
echo "WATCH_TS=${W_TS}"
echo "WATCH_MODE=${W_MODE}"
echo "WATCH_ACTED=${W_ACTED:-false}"
echo "WATCH_HOT=${W_HOT:-0}"
