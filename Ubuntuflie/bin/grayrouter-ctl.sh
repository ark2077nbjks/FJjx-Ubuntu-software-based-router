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
RDIR=/run/grayrouter/res
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
  export STATE CODE RID CMD MSG RES RDIR
  mkdir -p "$RDIR" 2>/dev/null; chmod 777 "$RDIR" 2>/dev/null
  python3 - <<'PY' 2>/dev/null || echo "{\"state\":\"$1\",\"exit\":$2}" > "$RES"
import json, os, time
d = {"state": os.environ["STATE"], "exit": int(os.environ["CODE"]),
     "id": os.environ["RID"], "cmd": os.environ["CMD"],
     "msg": os.environ["MSG"], "ts": int(time.time())}
for p in (os.environ["RES"], os.path.join(os.environ["RDIR"], os.environ["RID"] + ".json")):
    try:
        json.dump(d, open(p, "w"), ensure_ascii=False)
    except Exception:
        pass
PY
  chmod 644 "$RES" 2>/dev/null
  chmod 644 "$RDIR/$rid.json" 2>/dev/null
  # 只留最近 40 份，别把 /run 塞满
  ls -t "$RDIR"/*.json 2>/dev/null | tail -n +41 | xargs -r rm -f 2>/dev/null
}

# 设备命令的参数只允许 MAC / 数字，防止被塞奇怪的东西
case "$cmd" in
  dev-block|dev-unblock)
    printf '%s' "$rest" | grep -qiE '^([0-9a-f]{2}:){5}[0-9a-f]{2}$' || {
      write_res "error" 65 "MAC 格式不对：$rest"; log "拒绝 MAC: $rest"; exit 0; } ;;
  sqm-apply)
    printf '%s' "$rest" | grep -qE '^[0-9]{1,4} [0-9]{1,4}$' || {
      write_res "error" 65 "带宽参数不对：$rest"; log "拒绝 sqm-apply: $rest"; exit 0; } ;;
  sys-restart)
    printf '%s' "$rest" | grep -qE '^[a-zA-Z0-9@._-]+\.service$' || { write_res "error" 65 "单元名不合法：$rest"; log "拒绝 sys-restart: $rest"; exit 0; } ;;
  sys-power)
    case "$rest" in net-restart|reboot|shutdown) : ;; *) write_res "error" 65 "电源动作不允许：$rest"; exit 0 ;; esac ;;
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
  dev-names)     SCRIPT=$HOMEDIR/grayrouter-devmgr/grayrouter-devices.sh; ARGS="names"; DEVCAP=1 ;;
  roles-get)     SCRIPT=/usr/local/bin/grayrouter-roles.sh; ARGS="get"; DEVCAP=1 ;;
  roles-set)     SCRIPT=/usr/local/bin/grayrouter-roles.sh; ARGS="set ${rest:-auto}"; DEVCAP=1 ;;
  rescue)        SCRIPT=/usr/local/bin/grayrouter-rescue.sh; ARGS=""; DEVCAP=1 ;;
  auth-now)      SCRIPT=/usr/local/bin/grayrouter-auth.sh; ARGS=""; DEVCAP=1 ;;
  acct-status)   SCRIPT=/usr/local/bin/grayrouter-acct.sh; ARGS="status"; DEVCAP=1 ;;
  acct-apply)    SCRIPT=/usr/local/bin/grayrouter-acct.sh; ARGS="apply"; DEVCAP=1 ;;
  snapshot-save)  SCRIPT=/usr/local/bin/grayrouter-snapshot.sh; ARGS="save panel"; DEVCAP=1 ;;
  snapshot-list)  SCRIPT=/usr/local/bin/grayrouter-snapshot.sh; ARGS="list"; DEVCAP=1 ;;
  snapshot-restore-soft) SCRIPT=/usr/local/bin/grayrouter-snapshot.sh; ARGS="restore-soft ${rest:-latest}"; DEVCAP=1
                 printf '%s' "${rest:-latest}" | grep -Eq '^[A-Za-z0-9_.+-]+$' || {
                   write_res "error" 72 "快照名不合法：$rest"; exit 0; } ;;
  snapshot-restore) SCRIPT=/usr/local/bin/grayrouter-snapshot.sh; ARGS="restore ${rest:-latest}"; DEVCAP=1 ;;
  usbnet-now)    SCRIPT=/usr/local/bin/grayrouter-usbnet-now.sh; ARGS=""; DEVCAP=1 ;;
  wanfail-get)   SCRIPT=/usr/local/bin/grayrouter-wanfail.sh; ARGS="get"; DEVCAP=1 ;;
  wanfail-set)   SCRIPT=/usr/local/bin/grayrouter-wanfail.sh; ARGS="set ${rest:-}"; DEVCAP=1 ;;
  wanfail-switch) SCRIPT=/usr/local/bin/grayrouter-wanfail.sh; ARGS="switch ${rest:-primary}"; DEVCAP=1 ;;
  sqm-stats)     SCRIPT=/usr/local/bin/grayrouter-sqm.sh; ARGS="stats"; DEVCAP=1 ;;
  traffic-report) SCRIPT=/usr/local/bin/grayrouter-usage.sh; ARGS="${arg:-30}"; DEVCAP=1 ;;
  snap-list)     SCRIPT=/usr/local/bin/grayrouter-snapshot.sh; ARGS="list"; DEVCAP=1 ;;
  snap-diff)     SCRIPT=/usr/local/bin/grayrouter-snapshot.sh; ARGS="diff $rest"; DEVCAP=1
                 printf '%s' "$rest" | grep -Eq '^[A-Za-z0-9_.+-]+[[:space:]]+[A-Za-z0-9_.+-]+$' || {
                   write_res "error" 71 "快照名不合法：$rest"; exit 0; } ;;
  netdiag-trace) SCRIPT=/usr/local/bin/grayrouter-netdiag.sh; ARGS="trace ${rest:-223.5.5.5}"; DEVCAP=1
                 printf '%s' "${rest:-}" | grep -Eq '^[A-Za-z0-9.:_-]*$' || {
                   write_res "error" 69 "主机名不合法：$rest"; log "拒绝 netdiag: $rest"; exit 0; } ;;
  netdiag-mtu)   SCRIPT=/usr/local/bin/grayrouter-netdiag.sh; ARGS="mtu ${rest:-223.5.5.5}"; DEVCAP=1
                 printf '%s' "${rest:-}" | grep -Eq '^[A-Za-z0-9.:_-]*$' || {
                   write_res "error" 70 "主机名不合法：$rest"; log "拒绝 netdiag: $rest"; exit 0; } ;;
  lease-list)    SCRIPT=/usr/local/bin/grayrouter-lease.sh; ARGS="list"; DEVCAP=1 ;;
  lease-set)     SCRIPT=/usr/local/bin/grayrouter-lease.sh; ARGS="set $rest"; DEVCAP=1
                 printf '%s' "$rest" | grep -Eqi '^([0-9a-f]{2}:){5}[0-9a-f]{2} +([0-9]{1,3}\.){3}[0-9]{1,3}' || {
                   write_res "error" 66 "参数要 MAC IP [备注]：$rest"; log "拒绝 lease-set: $rest"; exit 0; } ;;
  lease-del)     SCRIPT=/usr/local/bin/grayrouter-lease.sh; ARGS="del $rest"; DEVCAP=1
                 printf '%s' "$rest" | grep -Eqi '^([0-9a-f]{2}:){5}[0-9a-f]{2}$' || {
                   write_res "error" 67 "参数要 MAC：$rest"; log "拒绝 lease-del: $rest"; exit 0; } ;;
  lease-apply)   SCRIPT=/usr/local/bin/grayrouter-lease.sh; ARGS="apply"; DEVCAP=1 ;;
  lease-clear)   SCRIPT=/usr/local/bin/grayrouter-lease.sh; ARGS="clear"; DEVCAP=1 ;;
  drift-check)   SCRIPT=/usr/local/bin/grayrouter-drift.sh; ARGS="${rest:-}"; DEVCAP=1 ;;
  restore-file)  SCRIPT=/usr/local/bin/grayrouter-restore.sh; ARGS="$rest"; DEVCAP=1
                 printf '%s' "$rest" | grep -Eq '^/home/[A-Za-z0-9._/-]+\.tar\.gz$' || {
                   write_res "error" 68 "参数要备份包绝对路径：$rest"; log "拒绝 restore-file: $rest"; exit 0; } ;;
  cron-get)      SCRIPT=/usr/local/bin/grayrouter-cron.sh; ARGS="get"; DEVCAP=1 ;;
  cron-set)      SCRIPT=/usr/local/bin/grayrouter-cron.sh; ARGS="set $rest"; DEVCAP=1 ;;
  cron-off)      SCRIPT=/usr/local/bin/grayrouter-cron.sh; ARGS="off"; DEVCAP=1 ;;
  dhcp-leases)   SCRIPT=/usr/local/bin/grayrouter-dhcpleases.sh; ARGS=""; DEVCAP=1 ;;
  limit-list)    SCRIPT=/usr/local/bin/grayrouter-limit.sh; ARGS="list"; DEVCAP=1 ;;
  limit-set)     SCRIPT=/usr/local/bin/grayrouter-limit.sh; ARGS="set $rest"; DEVCAP=1 ;;
  limit-del)     SCRIPT=/usr/local/bin/grayrouter-limit.sh; ARGS="del $rest"; DEVCAP=1 ;;
  limit-clear)   SCRIPT=/usr/local/bin/grayrouter-limit.sh; ARGS="clear"; DEVCAP=1 ;;
  traffic-now)   SCRIPT=/usr/local/bin/grayrouter-traffic.sh; ARGS="now ${arg:-30}"; DEVCAP=1 ;;
  traffic-summary) SCRIPT=/usr/local/bin/grayrouter-traffic.sh; ARGS="summary ${arg:-30}"; DEVCAP=1 ;;
  traffic-reset) SCRIPT=/usr/local/bin/grayrouter-traffic.sh; ARGS="reset"; DEVCAP=1 ;;
  sqm-get)       SCRIPT=/usr/local/bin/grayrouter-sqm.sh; ARGS="json"; DEVCAP=1 ;;
  sqm-apply)     SCRIPT=/usr/local/bin/grayrouter-sqm.sh; ARGS="apply ${rest}"; DEVCAP=1 ;;
  sqm-clear)     SCRIPT=/usr/local/bin/grayrouter-sqm.sh; ARGS="clear"; DEVCAP=1 ;;
  sqm-measure)   SCRIPT=/usr/local/bin/grayrouter-sqm.sh; ARGS="measure"; DEVCAP=1 ;;
  opt-status)    SCRIPT=/usr/local/bin/grayrouter-opt.sh; ARGS="status"; DEVCAP=1 ;;
  opt-apply)     SCRIPT=/usr/local/bin/grayrouter-opt.sh; ARGS="apply"; DEVCAP=1 ;;
  opt-rollback)  SCRIPT=/usr/local/bin/grayrouter-opt.sh; ARGS="rollback"; DEVCAP=1 ;;
  guard-arm)     SCRIPT=/usr/local/bin/grayrouter-guard.sh; ARGS="arm ${rest}"; DEVCAP=1
                 printf '%s' "$rest" | grep -qE '^[0-9]{2,3} (router|hotspot)$' || {
                   write_res "error" 66 "守卫参数不合法：$rest"; exit 0; } ;;
  guard-ok)      SCRIPT=/usr/local/bin/grayrouter-guard.sh; ARGS="confirm"; DEVCAP=1 ;;
  sys-restart)   SCRIPT=/usr/local/bin/grayrouter-sys.sh; ARGS="restart ${rest}"; DEVCAP=1 ;;
  sys-power)     SCRIPT=/usr/local/bin/grayrouter-sys.sh; ARGS="power ${rest}"; DEVCAP=1 ;;
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
  errf=$(mktemp)
  out=$(bash "$SCRIPT" ${ARGS:-} 2>"$errf")
  code=$?
  err=$(cat "$errf" 2>/dev/null); rm -f "$errf"
  printf '%s\n' "$out" >> "$LOG"
  if [ -n "$err" ]; then printf '%s\n' "$err" >> "$LOG"; fi
  if [ "$code" -eq 0 ]; then tailmsg=$out; else tailmsg="$out"$'\n'"$err"; fi
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
