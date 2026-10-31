#!/usr/bin/env bash
# ============================================================
#  grayrouter-guard.sh —— 变更守卫：N 秒内不确认就自动回滚
#    arm <秒> <router|hotspot>   改模式前布防（undo = 回退到另一个模式）
#    confirm                     “一切正常，保留改动”
#    fire <id>                   定时器触发：没确认就执行 undo
#    status                      查看当前守卫状态
#  由 grayrouter-ctl.sh 以 root 调用；状态在 /run/grayrouter/
# ============================================================
set -uo pipefail
RUN=/run/grayrouter
STATE=$RUN/guard.state
LOG=/var/log/grayrouter-guard.log
mkdir -p "$RUN"
log() { echo "$(date '+%F %T') $*" >> "$LOG"; }

mode_cmd() {
  case "$1" in
    router)  echo "/root/router-up.sh" ;;
    hotspot) echo "/root/router-down.sh" ;;
    *) echo "" ;;
  esac
}
# 兼容脚本不在 /root 的情况（不写死用户名）
pick() {
  local base; base="$(basename "$1")"
  local home=""; [ -n "${SUDO_USER:-}" ] && home="$(getent passwd "$SUDO_USER" | cut -d: -f6)"
  for p in "$1" "${home:+$home/$base}" "/home/*/$base" "/usr/local/bin/$base"; do
    for q in $p; do [ -f "$q" ] && { echo "$q"; return; }; done
  done
  echo ""
}

arm() {
  local sec="${1:-120}" undo_mode="${2:-}"
  [ -n "$(mode_cmd "$undo_mode")" ] || { echo '{"ok":false,"msg":"undo 模式不合法"}'; exit 0; }
  case "$sec" in ''|*[!0-9]*) echo '{"ok":false,"msg":"秒数不合法"}'; exit 0 ;; esac
  [ "$sec" -ge 30 ] && [ "$sec" -le 900 ] || { echo '{"ok":false,"msg":"秒数需在 30-900"}'; exit 0; }

  local id; id="$(date +%s)$$"
  local undo; undo="$(pick "$(mode_cmd "$undo_mode")")"
  [ -n "$undo" ] || { echo '{"ok":false,"msg":"找不到回滚脚本"}'; exit 0; }

  printf 'id=%s\ndeadline=%s\nundo=%s\nmode=%s\n' "$id" "$(( $(date +%s) + sec ))" "$undo" "$undo_mode" > "$STATE"
  rm -f "$RUN"/guard-*.ok
  systemd-run --on-active="$sec" --unit="grayrouter-guard-$id" --collect \
      /usr/local/bin/grayrouter-guard.sh fire "$id" >/dev/null 2>&1 \
      || { echo '{"ok":false,"msg":"排程失败"}'; exit 0; }
  log "布防 id=$id ${sec}s，未确认则回滚到 $undo_mode（$undo）"
  echo "{\"ok\":true,\"id\":\"$id\",\"seconds\":$sec,\"undo\":\"$undo_mode\"}"
}

confirm() {
  [ -f "$STATE" ] || { echo '{"ok":true,"msg":"没有待确认的守卫"}'; exit 0; }
  local id; id="$(sed -n 's/^id=//p' "$STATE")"
  touch "$RUN/guard-$id.ok"
  log "确认保留 id=$id"
  echo "{\"ok\":true,\"msg\":\"已确认保留\"}"
}

fire() {
  local id="${1:-}"
  [ -f "$STATE" ] || exit 0
  local cur; cur="$(sed -n 's/^id=//p' "$STATE")"
  [ "$cur" = "$id" ] || { log "旧守卫 $id 已作废（当前 $cur）"; exit 0; }
  if [ -f "$RUN/guard-$id.ok" ]; then log "守卫 $id 已确认，不动作"; rm -f "$STATE" "$RUN/guard-$id.ok"; exit 0; fi
  local undo; undo="$(sed -n 's/^undo=//p' "$STATE")"
  log "守卫 $id 超时未确认 → 执行回滚 $undo"
  rm -f "$STATE"
  [ -n "$undo" ] && bash "$undo" >> "$LOG" 2>&1
  log "回滚结束 exit=$?"
}

case "${1:-status}" in
  arm)     arm "${2:-120}" "${3:-}" ;;
  confirm) confirm ;;
  fire)    fire "${2:-}" ;;
  status)
    if [ -f "$STATE" ]; then
      echo "{\"armed\":true,$(sed -n 's/^\([a-z]*\)=\(.*\)/"\1":"\2"/p' "$STATE" | paste -sd, -)}"
    else echo '{"armed":false}'; fi ;;
  *) echo '{"ok":false,"msg":"用法: arm <秒> <router|hotspot> | confirm | fire <id> | status"}' ;;
esac
