#!/usr/bin/env bash
# GrayRouter 看门狗 —— 检测"假在线"（AP 有 IP、有设备、但数据不动）
# MODE=observe 只记录；MODE=act 才真的重启 AP 连接
# 模式开关：面板写 /run/grayrouter/mode.request（act/observe），每轮读一次并清空
set -u
# LAN 口自动探测（优先 AP 模式网口，其次 192.168.x，最后兜底 wlp2s0）
if [ -z "${IFACE:-}" ] && [ -x /usr/local/bin/grayrouter-roles.sh ]; then
  IFACE=$(/usr/local/bin/grayrouter-roles.sh get 2>/dev/null | sed -n 's/^LAN_IF=//p')
fi
if [ -z "${IFACE:-}" ]; then
  for _i in $(iw dev 2>/dev/null | sed -n 's/^Interface //p'); do
    [ "$(iw dev "$_i" info 2>/dev/null | awk '/type/{print $2}')" = "AP" ] && { IFACE=$_i; break; }
  done
fi
[ -z "${IFACE:-}" ] && IFACE=$(ip -4 -o addr show 2>/dev/null | awk '$4 ~ /^192\.168\./ {print $2; exit}')
[ -z "${IFACE:-}" ] && IFACE=wlp2s0
EXPECT_IP="${EXPECT_IP:-192.168.50.1}"
MODE="${MODE:-observe}"
STATE_DIR="${STATE_DIR:-/run/grayrouter}"
OUT="$STATE_DIR/watch.json"
LAST="$STATE_DIR/last.txt"
LOG="${LOG:-/var/log/grayrouter-watch.log}"
SPAN=180                # 观察窗口：秒
HOT=0                    # 连续可疑次数

mkdir -p "$STATE_DIR" 2>/dev/null; chmod 755 "$STATE_DIR" 2>/dev/null
[ -f "$STATE_DIR/restart.request" ] || { : > "$STATE_DIR/restart.request"; chmod 666 "$STATE_DIR/restart.request" 2>/dev/null; }

# 模式开关：面板写 mode.request，这里每轮读一次并清空（act=真动手，observe=只看）
MREQ="$STATE_DIR/mode.request"
[ -f "$MREQ" ] || { : > "$MREQ"; chmod 666 "$MREQ" 2>/dev/null; }
if [ -s "$MREQ" ]; then
  _m=$(tr -d ' \r\n' < "$MREQ" 2>/dev/null)
  case "$_m" in act|observe) MODE="$_m";; esac
  : > "$MREQ"
fi

ts=$(date '+%Y-%m-%d %H:%M:%S')
now=$(date +%s)

REQ="$STATE_DIR/restart.request"
if [ -s "$REQ" ]; then
  : > "$REQ"
  logger -t grayrouter-watch "收到面板重启请求，重启 AP"
  echo "$ts [执行] 面板请求 → 重启 AP 连接" >> "$LOG"
  nmcli connection up Gray-Router >/dev/null 2>&1
  echo "$now 0 0" > "$LAST"
fi

ip_ok=false
if ip -4 -o addr show dev "$IFACE" 2>/dev/null | grep -q "$EXPECT_IP/"; then ip_ok=true; fi

stations=0
if command -v iw >/dev/null 2>&1; then
  stations=$(iw dev "$IFACE" station dump 2>/dev/null | grep -c '^Station')
fi

rx=$(cat /sys/class/net/$IFACE/statistics/rx_bytes 2>/dev/null || echo 0)
tx=$(cat /sys/class/net/$IFACE/statistics/tx_bytes 2>/dev/null || echo 0)

prev_ts=0; prev_rx=0; prev_tx=0
[ -f "$LAST" ] && read -r prev_ts prev_rx prev_tx < "$LAST"
echo "$now $rx $tx" > "$LAST"

span=$(( now - prev_ts ))
delta=0
[ "$prev_ts" -gt 0 ] && delta=$(( (rx + tx) - (prev_rx + prev_tx) ))

verdict=ok
would=false
# 首次运行 / 计数器重置 / 间隔过长：只当基线，不判定
if [ "$prev_ts" -le 0 ] || [ "$span" -gt 3600 ] || [ "$delta" -lt 0 ]; then
  verdict=ok
elif [ "$ip_ok" = true ] && [ "$stations" -gt 0 ] && [ "$span" -ge "$SPAN" ] && [ "$delta" -eq 0 ]; then
  verdict=suspect
  would=true
fi

# 连续可疑计数
if [ "$verdict" = suspect ]; then
  HOT=$(( $(cat "$STATE_DIR/hot" 2>/dev/null || echo 0) + 1 ))
else
  HOT=0
fi
echo "$HOT" > "$STATE_DIR/hot"

acted=false
if [ "$verdict" = suspect ] && [ "$MODE" = act ] && [ "$HOT" -le 3 ]; then
  logger -t grayrouter-watch "假在线（第 $HOT 次），重启 AP 连接 $IFACE"
  echo "$ts [执行] 第 ${HOT} 次假在线 → nmcli connection up Gray-Router" >> "$LOG"
  nmcli connection up Gray-Router >/dev/null 2>&1 && acted=true
  echo "$now 0 0" > "$LAST"   # 重置基线，避免立刻又判定
elif [ "$verdict" = suspect ]; then
  echo "$ts [观察] 本应重启 AP（模式=$MODE，连续第 ${HOT} 次，窗口 ${span}s，字节增量 ${delta}）" >> "$LOG"
fi

cat > "$OUT.tmp" <<JSON
{"ts":"$ts","iface":"$IFACE","ip_ok":$ip_ok,"stations":$stations,"span":$span,"delta":$delta,"verdict":"$verdict","mode":"$MODE","would_restart":$would,"acted":$acted,"hot":$HOT}
JSON
mv -f "$OUT.tmp" "$OUT"
chmod 644 "$OUT" 2>/dev/null
