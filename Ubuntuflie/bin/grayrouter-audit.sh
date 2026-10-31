#!/usr/bin/env bash
# ============================================================
#  grayrouter-audit.sh —— 配置变更审计（/etc 快照进 git）
#    init            初始化仓库并提交首版
#    commit [msg]    重新快照并提交（有变化才 commit）
#    log [n]         最近 n 条变更
#    diff            最近一次提交改了什么
#    status          当前与上次快照的差异
#  说明：只读拷贝，不修改 /etc；不收录含密码的连接配置。
# ============================================================
set -uo pipefail

GITDIR=/var/lib/grayrouter/etcsnap
LOG=/var/log/grayrouter-audit.log
SRC_LIST=(
  /etc/grayrouter
  /etc/sysctl.d
  /etc/systemd/journald.conf.d
  /etc/NetworkManager/dnsmasq-shared.d
  /etc/systemd/system/grayrouter-band.service
  /etc/cron.d
  /etc/systemd/system/grayrouter-limit.service
  /etc/systemd/system/grayrouter-opt.service
  /etc/systemd/system/grayrouter-sqm.service
  /etc/systemd/system/grayrouter-wanfail.service
  /etc/systemd/system/grayrouter-watch.service
  /etc/systemd/system/grayrouter-boot.service
)
DST=$GITDIR/files

log() { echo "$(date '+%F %T') $*" >> "$LOG" 2>/dev/null || true; }
say() { printf '%s\n' "$*"; }
need_root() { [ "$(id -u)" -eq 0 ] || { say "需要 root：sudo bash $0 $*"; exit 1; }; }

snapshot() {
  rm -rf "$DST"; mkdir -p "$DST"
  for src in "${SRC_LIST[@]}"; do
    [ -e "$src" ] || continue
    if [ -d "$src" ]; then
      mkdir -p "$DST${src}"
      cp -a "$src/." "$DST${src}/" 2>/dev/null || true
    else
      mkdir -p "$DST$(dirname "$src")"
      cp -a "$src" "$DST${src}" 2>/dev/null || true
    fi
  done
  # 只记录 AP 的频段/信道（不含 PSK），用于追踪频段是否被改
  mkdir -p "$DST/etc/NetworkManager"
  {
    printf 'SSID=%s\n' "$(nmcli -g 802-11-wireless.ssid con show Gray-Router 2>/dev/null)"
    printf 'BAND=%s\n' "$(nmcli -g 802-11-wireless.band con show Gray-Router 2>/dev/null)"
    printf 'CHANNEL=%s\n' "$(nmcli -g 802-11-wireless.channel con show Gray-Router 2>/dev/null)"
    printf 'RUNTIME_CHANNEL=%s\n' "$(iw dev wlp2s0 info 2>/dev/null | sed -n 's/.*channel \([0-9]*\).*/\1/p' | head -1)"
  } > "$DST/etc/NetworkManager/ap-band.txt"
  # 不收录可能含明文密码的文件
  find "$DST" -type f \( -name "*.psk" -o -name "*secret*" -o -name "*password*" \) -delete 2>/dev/null || true
}

do_init() {
  need_root "$@"
  mkdir -p "$GITDIR"
  [ -d "$GITDIR/.git" ] || git init -q "$GITDIR"
  git -C "$GITDIR" config user.name  "grayrouter-audit"
  git -C "$GITDIR" config user.email "audit@localhost"
  snapshot
  git -C "$GITDIR" add -A
  git -C "$GITDIR" commit -q -m "init: $(date '+%F %T')" || true
  say "✓ 审计仓库已建立：$GITDIR（$(git -C "$GITDIR" rev-list --count HEAD) 次提交）"
}

do_commit() {
  need_root "$@"
  [ -d "$GITDIR/.git" ] || { do_init "$@"; return; }
  snapshot
  git -C "$GITDIR" add -A
  if git -C "$GITDIR" diff --cached --quiet; then
    say "无变化（仍是 $(git -C "$GITDIR" rev-list --count HEAD) 次提交）"
    return
  fi
  local msg="${1:-auto}"
  git -C "$GITDIR" commit -q -m "$msg $(date '+%F %T')"
  say "✓ 已记录变更：$(git -C "$GITDIR" log -1 --oneline)"
  log "commit: $msg"
}

do_log() {
  local n="${1:-10}"
  git -C "$GITDIR" log --oneline -n "$n" --stat 2>/dev/null | head -40
}

do_diff() {
  git -C "$GITDIR" show --stat --oneline HEAD 2>/dev/null | head -20
  git -C "$GITDIR" show HEAD 2>/dev/null | head -60
}

do_status() {
  snapshot
  git -C "$GITDIR" status --short 2>/dev/null | head -30
}

case "${1:-status}" in
  init) do_init "$@" ;;
  commit) shift; do_commit "${1:-auto}" ;;
  log) shift; do_log "${1:-10}" ;;
  diff) do_diff ;;
  status) do_status ;;
  *) say "用法：$0 {init|commit [msg]|log [n]|diff|status}"; exit 2 ;;
esac
