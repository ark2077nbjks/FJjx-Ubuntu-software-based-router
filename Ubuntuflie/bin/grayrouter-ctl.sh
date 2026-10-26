#!/usr/bin/env bash
# ============================================================
#  grayrouter-ctl.sh —— 面板遥控入口（只认 3 个命令）
#    请求文件：/run/grayrouter/request   内容 "<id> <命令>"
#    结果文件：/run/grayrouter/result.json
#  由 grayrouter-ctl.path 监听请求文件变化后触发，root 运行
# ============================================================
set -uo pipefail

REQ=/run/grayrouter/request
RES=/run/grayrouter/result.json
SEEN=/run/grayrouter/ctl.seen
LOG=/var/log/grayrouter-ctl.log

# 安装时写入的用户（面板登录的那个账号），脚本都放在它的家目录
GR_USER=""
[ -f /etc/grayrouter/ctl.conf ] && . /etc/grayrouter/ctl.conf
HOMEDIR=$(getent passwd "${GR_USER:-root}" 2>/dev/null | cut -d: -f6)
[ -n "$HOMEDIR" ] || HOMEDIR=/root

mkdir -p /run/grayrouter 2>/dev/null
[ -f "$REQ" ] || { : > "$REQ"; chmod 666 "$REQ" 2>/dev/null; }
[ "$(id -u)" -eq 0 ] || exit 0

raw=$(tr -d '\r\n' < "$REQ" 2>/dev/null || true)
[ -z "${raw// /}" ] && exit 0

rid=$(printf '%s' "$raw" | awk '{print $1}')
cmd=$(printf '%s' "$raw" | awk '{print $2}')
arg=$(printf '%s' "$raw" | awk '{print $3}')
rest=$(printf '%s' "$raw" | cut -d' ' -f3- | tr -d '\r')
case "${arg:-}" in ''|*[!0-9]*) arg="" ;; esac

# 同一个 id 只执行一次（避免 path 重复触发）
last=$(cat "$SEEN" 2>/dev/null || echo "")
[ "$rid" = "$last" ] && exit 0
echo "$rid" > "$SEEN"

log() { echo "$(date '+%F %T') $*" >> "$LOG"; }

write_res() {   # $1=state  $2=exit  $3=msg
  STATE="$1" CODE="$2" RID="$rid" CMD="$cmd" MSG="$3"
  export STATE CODE RID CMD MSG
  python3 - <<'PY' 2>/dev/null || echo "{\"state\":\"$1\",\"exit\":$2}" > "$RES"
import json, os, time
json.dump({"state": os.environ["STATE"], "exit": int(os.environ["CODE"]),
           "id": os.environ["RID"], "cmd": os.environ["CMD"],
           "msg": os.environ["MSG"], "ts": int(time.time())},
          open("/run/grayrouter/result.json", "w"), ensure_ascii=False)
PY
  chmod 644 "$RES" 2>/dev/null
}

# 设备命令的参数只允许 MAC / 数字，防止被塞奇怪的东西
case "$cmd" in
  dev-block|dev-unblock)
    printf '%s' "$rest" | grep -qiE '^([0-9a-f]{2}:){5}[0-9a-f]{2}$' || {
      write_res "error" 65 "MAC 格式不对：$rest"; log "拒绝 MAC: $rest"; exit 0; } ;;
  dev-*) : ;;
esac

log "收到请求 id=$rid cmd=$cmd"

case "$cmd" in
  mode-router)   SCRIPT=$HOMEDIR/router-up.sh ;;
  mode-hotspot)  SCRIPT=$HOMEDIR/router-down.sh ;;
  harden)        SCRIPT=/usr/local/bin/grayrouter-harden.sh ;;
  wifi-5g)       SCRIPT=$HOMEDIR/grayrouter-wifi/set-band.sh; ARGS="5g ${arg:-36}" ;;
  wifi-2g)       SCRIPT=$HOMEDIR/grayrouter-wifi/set-band.sh; ARGS="2g ${arg:-6}" ;;
  dev-block)     SCRIPT=$HOMEDIR/grayrouter-devmgr/grayrouter-devices.sh; ARGS="block $rest"; DEVCAP=1 ;;
  dev-unblock)   SCRIPT=$HOMEDIR/grayrouter-devmgr/grayrouter-devices.sh; ARGS="unblock $rest"; DEVCAP=1 ;;
  dev-unblock-all) SCRIPT=$HOMEDIR/grayrouter-devmgr/grayrouter-devices.sh; ARGS="unblock-all"; DEVCAP=1 ;;
  dev-apply)     SCRIPT=$HOMEDIR/grayrouter-devmgr/grayrouter-devices.sh; ARGS="apply"; DEVCAP=1 ;;
  dev-list)      SCRIPT=$HOMEDIR/grayrouter-devmgr/grayrouter-devices.sh; ARGS="list"; DEVCAP=1 ;;
  traffic-now)   SCRIPT=/usr/local/bin/grayrouter-traffic.sh; ARGS="now ${arg:-30}"; DEVCAP=1 ;;
  traffic-summary) SCRIPT=/usr/local/bin/grayrouter-traffic.sh; ARGS="summary ${arg:-30}"; DEVCAP=1 ;;
  *)
    write_res "unknown" 64 "不认识的命令：$cmd"
    log "拒绝：$cmd"
    exit 0
    ;;
esac

if [ ! -f "$SCRIPT" ]; then
  write_res "error" 66 "找不到脚本：$SCRIPT"
  log "找不到 $SCRIPT"
  exit 0
fi

write_res "running" 0 "开始执行 $cmd ${ARGS:-}"
if [ "${DEVCAP:-0}" = "1" ]; then
  out=$(bash "$SCRIPT" ${ARGS:-} 2>>"$LOG")
  code=$?
  tailmsg=$out
else
  bash "$SCRIPT" ${ARGS:-} >> "$LOG" 2>&1
  code=$?
  tailmsg=$(tail -n 4 "$LOG" | tr '\n' ' ' | cut -c1-300)
fi

if [ "$code" -eq 0 ]; then
  write_res "done" 0 "$tailmsg"
  log "完成 $cmd (exit 0)"
else
  write_res "error" "$code" "$tailmsg"
  log "失败 $cmd (exit $code)"
fi
