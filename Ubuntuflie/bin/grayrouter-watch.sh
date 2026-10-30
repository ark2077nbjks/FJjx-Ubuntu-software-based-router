#!/usr/bin/env bash
# ============================================================
#  grayrouter-watch.sh v2 —— 本地自愈看门狗（60 秒一轮）
#    只在本机跑，不依赖面板/网络连接
#    检查：LAN IP / AP 模式 / SSH 22 / 默认路由 / 外网 204 / 假在线
#    自愈：重启热点 → 重启 ssh → 跑 rescue → 重启 NetworkManager → 回滚快照
#    模式：MODE=observe 只看；MODE=act 真动手（默认 act）
# ============================================================
set -u
STATE=/run/grayrouter
LOG=/var/log/grayrouter-watch.log
OUT=$STATE/watch.json
FAILS=$STATE/watch.fails

mkdir -p "$STATE" 2>/dev/null; chmod 755 "$STATE" 2>/dev/null
# 遥控通道保险：请求文件丢了（/run 被清）就补回来，否则面板永远发不出命令
[ -f "$STATE/request" ] || { : > "$STATE/request"; }
chmod 666 "$STATE/request" 2>/dev/null
[ -f "$STATE/result.json" ] || echo '{}' > "$STATE/result.json"
chmod 644 "$STATE/result.json" 2>/dev/null
[ -f "$STATE/mode.request" ] || { : > "$STATE/mode.request"; chmod 666 "$STATE/mode.request" 2>/dev/null; }
[ -f "$STATE/restart.request" ] || { : > "$STATE/restart.request"; chmod 666 "$STATE/restart.request" 2>/dev/null; }

MODE="${MODE:-act}"
MREQ="$STATE/mode.request"
if [ -s "$MREQ" ]; then
  _m=$(tr -d ' \r\n' < "$MREQ" 2>/dev/null)
  case "$_m" in act|observe) MODE="$_m" ;; esac
  : > "$MREQ"
fi

log() { echo "$(date '+%F %T') $*" >> "$LOG" 2>/dev/null; logger -t grayrouter-watch "$*" 2>/dev/null; }

# --- 角色 ---
LAN=""; WAN=""
if [ -x /usr/local/bin/grayrouter-roles.sh ]; then
  _r=$(/usr/local/bin/grayrouter-roles.sh get 2>/dev/null)
  LAN=$(echo "$_r" | sed -n 's/^LAN_IF=//p')
  WAN=$(echo "$_r" | sed -n 's/^WAN_IF=//p')
fi
[ -z "$LAN" ] && LAN=$(ip -4 -o addr show 2>/dev/null | awk '$4 ~ /^192\.168\.50\./ {print $2; exit}')
[ -z "$LAN" ] && LAN=wlp2s0
EXPECT_IP="${EXPECT_IP:-192.168.50.1}"
AP_CON="${AP_CON:-Gray-Router}"

# --- 首次开机的宽限期 ---
UP=$(cut -d. -f1 /proc/uptime 2>/dev/null || echo 9999)
if [ "${UP:-9999}" -lt 120 ]; then
  echo "{\"ts\":\"$(date '+%F %T')\",\"skip\":\"boot\",\"uptime\":$UP}" > "$OUT"
  exit 0
fi

# --- 检查 ---
ip_ok=false
ip -4 -o addr show dev "$LAN" 2>/dev/null | grep -q "$EXPECT_IP/" && ip_ok=true

ap_ok=false
if command -v iw >/dev/null 2>&1; then
  [ "$(iw dev "$LAN" info 2>/dev/null | awk '/type/{print $2}')" = "AP" ] && ap_ok=true
fi

ssh_ok=false
ss -lnt 2>/dev/null | grep -qE '[:.]22[[:space:]]' && ssh_ok=true

route_ok=false
ip route show default 2>/dev/null | grep -q '^default' && route_ok=true

net_ok=false
if [ "$route_ok" = true ]; then
  c=$(curl -s -m 4 -o /dev/null -w '%{http_code}' http://connect.rom.miui.com/generate_204 2>/dev/null)
  [ "$c" = "204" ] && net_ok=true
fi

stations=0
command -v iw >/dev/null 2>&1 && stations=$(iw dev "$LAN" station dump 2>/dev/null | grep -c '^Station')
rx=$(cat "/sys/class/net/$LAN/statistics/rx_bytes" 2>/dev/null || echo 0)
tx=$(cat "/sys/class/net/$LAN/statistics/tx_bytes" 2>/dev/null || echo 0)
LASTF="$STATE/last.txt"; prev_ts=0; prev_rx=0; prev_tx=0
[ -f "$LASTF" ] && read -r prev_ts prev_rx prev_tx < "$LASTF"
now=$(date +%s); echo "$now $rx $tx" > "$LASTF"
span=$(( now - ${prev_ts:-0} )); delta=0
[ "${prev_ts:-0}" -gt 0 ] && delta=$(( (rx + tx) - (prev_rx + prev_tx) ))

# --- 保险：有线 LAN 互访规则（nft 表在内存里，重启会丢；不依赖 systemd 单元）---
if [ -x /usr/local/bin/grayrouter-acct.sh ]; then
  nft list table inet gray_acct >/dev/null 2>&1 || /usr/local/bin/grayrouter-acct.sh apply >/dev/null 2>&1
fi
if [ -x /usr/local/bin/lan-usb.sh ]; then
  nft list table inet gray_lan2 >/dev/null 2>&1 || /usr/local/bin/lan-usb.sh rules >/dev/null 2>&1
fi

# --- 判定 ---
verdict=ok
[ "$ip_ok" = false ] && verdict=lan-down
[ "$verdict" = ok ] && [ "$ap_ok" = false ] && verdict=ap-down
[ "$verdict" = ok ] && [ "$ssh_ok" = false ] && verdict=ssh-down
[ "$verdict" = ok ] && [ "$route_ok" = false ] && verdict=no-route
[ "$verdict" = ok ] && [ "$net_ok" = false ] && verdict=no-internet
[ "$verdict" = ok ] && [ "$stations" -gt 0 ] && [ "${prev_ts:-0}" -gt 0 ] && [ "$span" -ge 180 ] && [ "$delta" -eq 0 ] && verdict=stalled

# --- 连续失败计数（假在线单独算，不参与升级）---
if [ "$verdict" = ok ]; then
  echo 0 > "$FAILS"
  fails=0
else
  fails=$(( $(cat "$FAILS" 2>/dev/null || echo 0) + 1 ))
  echo "$fails" > "$FAILS"
fi

acted=""
if [ "$MODE" = act ]; then
  case "$verdict" in
    lan-down|ap-down)
      log "[$verdict] 重启热点连接 $AP_CON（第 $fails 次）"
      nmcli connection up "$AP_CON" >/dev/null 2>&1 && acted="up-ap"
      ;;
    ssh-down)
      log "[ssh-down] 重启 sshd（第 $fails 次）"
      systemctl restart ssh >/dev/null 2>&1 || systemctl restart sshd >/dev/null 2>&1
      acted="restart-ssh"
      ;;
    no-route|no-internet)
      tsfile="$STATE/watch.rescue.ts"; last=$(cat "$tsfile" 2>/dev/null || echo 0)
      if [ $(( now - last )) -ge 600 ]; then
        was_on=0
        [ -f /etc/grayrouter/wanfail.env ] && grep -q '^ENABLE=1' /etc/grayrouter/wanfail.env && was_on=1
        log "[$verdict] 跑 rescue 自愈（第 $fails 次）"
        [ -x /usr/local/bin/grayrouter-rescue.sh ] && /usr/local/bin/grayrouter-rescue.sh >/dev/null 2>&1
        [ "$was_on" = 1 ] && /usr/local/bin/grayrouter-wanfail.sh set enable >/dev/null 2>&1
        echo "$now" > "$tsfile"
        acted="rescue"
      else
        acted="rescue-skipped"
      fi
      ;;
    stalled)
      if [ "$fails" -le 3 ]; then
        log "[stalled] 假在线（第 $fails 次），重启热点"
        nmcli connection up "$AP_CON" >/dev/null 2>&1 && acted="up-ap"
        echo "$now 0 0" > "$LASTF"
      fi
      ;;
  esac

  # 升级：连续 4 轮没修好 → 重启 NetworkManager
  if [ "$verdict" != ok ] && [ "$fails" -ge 4 ] && [ "$fails" -lt 6 ]; then
    log "[升级] 连续 $fails 轮异常，重启 NetworkManager"
    systemctl restart NetworkManager >/dev/null 2>&1
    acted="$acted,restart-nm"
  fi
  # 再升级：连续 6 轮 → 回滚上一份配置快照（每小时最多一次）
  if [ "$verdict" != ok ] && [ "$fails" -ge 6 ]; then
    tsfile="$STATE/watch.restore.ts"; last=$(cat "$tsfile" 2>/dev/null || echo 0)
    if [ $(( now - last )) -ge 3600 ] && [ -x /usr/local/bin/grayrouter-snapshot.sh ]; then
      log "[升级] 连续 $fails 轮异常，回滚上一份快照"
      /usr/local/bin/grayrouter-snapshot.sh restore latest >/dev/null 2>&1
      echo "$now" > "$tsfile"
      acted="$acted,restore"
    fi
  fi
else
  [ "$verdict" != ok ] && log "[观察] $verdict（模式=observe，第 $fails 次）"
fi

ts=$(date '+%F %T')
cat > "$OUT.tmp" <<JSON
{"ts":"$ts","iface":"$LAN","ip_ok":$ip_ok,"ap_ok":$ap_ok,"ssh_ok":$ssh_ok,"route_ok":$route_ok,"net_ok":$net_ok,"stations":$stations,"span":$span,"delta":$delta,"verdict":"$verdict","mode":"$MODE","fails":$fails,"acted":"$acted"}
JSON
mv -f "$OUT.tmp" "$OUT" 2>/dev/null; chmod 644 "$OUT" 2>/dev/null
exit 0
