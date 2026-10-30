#!/usr/bin/env bash
# 信标自愈：rtw88 在 AP 模式下有时发不出 beacon（系统看接口还是 up，手机搜不到）
# 双判据才动手，避免把正常的安静时段当成故障。1 小时最多自愈 1 次。
set -u

IFACE="${IFACE:-wlp2s0}"
CON="${CON:-Gray-Router}"
LOG="${LOG:-/var/log/grayrouter-beacon.log}"
STATE="${STATE:-/run/grayrouter/beacon.state}"
WINDOW="${WINDOW:-10 min}"
ERR_MIN="${ERR_MIN:-20}"          # 10 分钟内 error beacon valid 的次数门槛
COOL="${COOL:-3600}"              # 冷却秒数

mkdir -p "$(dirname "$STATE")"
now=$(date +%s)

log() { echo "$(date '+%F %T') $*" >> "$LOG"; }

# 日志超过 1MB 就截断
if [ -f "$LOG" ] && [ "$(stat -c %s "$LOG" 2>/dev/null || echo 0)" -gt 1048576 ]; then
    tail -n 500 "$LOG" > "$LOG.tmp" && mv "$LOG.tmp" "$LOG"
fi

# 冷却
last=0
[ -f "$STATE" ] && last=$(cat "$STATE" 2>/dev/null || echo 0)
case "$last" in ''|*[!0-9]*) last=0 ;; esac
[ $((now - last)) -lt "$COOL" ] && exit 0

# 判据 0：AP 得是 AP 模式
iw dev "$IFACE" info 2>/dev/null | grep -q 'type AP' || exit 0

# 判据 1：内核日志里的信标错误
errs=$(journalctl -k --since "-$WINDOW" --no-pager 2>/dev/null \
        | grep -cE 'error beacon valid|failed to download beacon|failed to download drv rsvd page')
case "$errs" in ''|*[!0-9]*) errs=0 ;; esac
[ "$errs" -lt "$ERR_MIN" ] && exit 0

# 判据 2：现在没有客户端，但最近一天有过设备（说明有人用，不是空机房）
stations=$(iw dev "$IFACE" station dump 2>/dev/null | grep -c '^Station')
[ "$stations" -gt 0 ] && exit 0
recent=$(journalctl -u NetworkManager --since "-24 hour" --no-pager 2>/dev/null \
        | grep -cE "DHCPACK\($IFACE\)|AP-STA-CONNECTED")
[ "${recent:-0}" -lt 1 ] && exit 0

log "信标异常：${WINDOW}内 $errs 次错误，当前 0 客户端（近 24h 有 $recent 次连接记录）→ 重启 AP"
nmcli con down "$CON" >>"$LOG" 2>&1
sleep 2
nmcli con up "$CON" >>"$LOG" 2>&1
sleep 60

errs2=$(journalctl -k --since "-3 min" --no-pager 2>/dev/null \
        | grep -cE 'error beacon valid|failed to download beacon|failed to download drv rsvd page')
case "$errs2" in ''|*[!0-9]*) errs2=0 ;; esac

if [ "$errs2" -ge 1 ]; then
    log "重启 AP 后仍有 $errs2 次错误 → 重载驱动 rtw88_8821ce"
    modprobe -r rtw88_8821ce 2>>"$LOG"
    modprobe rtw88_8821ce 2>>"$LOG"
    sleep 6
    nmcli con up "$CON" >>"$LOG" 2>&1
    sleep 20
    errs3=$(journalctl -k --since "-2 min" --no-pager 2>/dev/null \
            | grep -cE 'error beacon valid|failed to download beacon')
    case "$errs3" in ''|*[!0-9]*) errs3=0 ;; esac
    [ "$errs3" -ge 1 ] && log "重载驱动后仍有 $errs3 次错误，需要人工重启或换 2.4G"
fi

echo "$now" > "$STATE"
log "自愈流程结束"
exit 0
