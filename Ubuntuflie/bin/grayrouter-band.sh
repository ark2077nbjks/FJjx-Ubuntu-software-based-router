#!/usr/bin/env bash
# ============================================================
#  grayrouter-band.sh —— 开机恢复 AP 频段（配合 set-band.sh）
#    apply    读 /etc/grayrouter/band.env，与运行时不一致才重设
#    status   只读 JSON
#  为什么需要：Ubuntu 26.04 的 AP 连接由 netplan/NM 在 /run 下重建，
#  重启后频段会回到默认 2.4G，故用 oneshot 单元在开机后补一次。
# ============================================================
set -uo pipefail
CON="Gray-Router"; IF="wlp2s0"
ENVF="/etc/grayrouter/band.env"; LOG="/var/log/grayrouter-band.log"
FB_BAND="bg"; FB_CH="6"

say() { echo "$(date '+%F %T') $*" >> "$LOG" 2>/dev/null || true; }
cur_ch() { iw dev "$IF" info 2>/dev/null | sed -n 's/.*channel \([0-9]*\).*/\1/p' | head -1; }
cfg_band() { nmcli -g 802-11-wireless.band con show "$CON" 2>/dev/null; }
cfg_ch() { nmcli -g 802-11-wireless.channel con show "$CON" 2>/dev/null; }

apply() { # $1=band $2=channel
  local b="$1" c="$2" i
  nmcli con modify "$CON" 802-11-wireless.band "$b" 802-11-wireless.channel "$c" >/dev/null 2>&1
  nmcli con down "$CON" >/dev/null 2>&1 || true
  sleep 1
  nmcli con up "$CON" >/dev/null 2>&1 || true
  for i in $(seq 1 12); do
    [ "$(cur_ch)" = "$c" ] && return 0
    sleep 1
  done
  return 1
}

case "${1:-status}" in
  status)
    printf '{"band":"%s","channel":"%s","runtime_channel":"%s","saved":"%s"}\n' \
      "$(cfg_band)" "$(cfg_ch)" "$(cur_ch)" "$([ -f "$ENVF" ] && tr '\n' ' ' < "$ENVF")"
    ;;
  apply)
    [ -f "$ENVF" ] || { say "没有 band.env，跳过"; exit 0; }
    . "$ENVF"
    BAND="${BAND:-}"; CH="${CH:-}"
    [ -z "$BAND" ] || [ -z "$CH" ] && exit 0
    if [ "$(cur_ch)" = "$CH" ]; then say "已是 $BAND/$CH"; exit 0; fi
    say "开机恢复 $BAND/$CH（当前 $(cur_ch)）"
    if apply "$BAND" "$CH"; then say "恢复成功 $BAND/$CH"; else
      say "恢复失败，回退 $FB_BAND/$FB_CH"
      apply "$FB_BAND" "$FB_CH" || true
      printf 'BAND=%s\nCH=%s\n' "$FB_BAND" "$FB_CH" > "$ENVF"
    fi
    ;;
  *) echo "用法: $0 {status|apply}"; exit 2;;
esac
