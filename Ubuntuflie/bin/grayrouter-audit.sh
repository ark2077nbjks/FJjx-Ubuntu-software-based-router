#!/usr/bin/env bash
# ============================================================
#  grayrouter-audit.sh —— 配置变更审计（/etc 快照进 git）
#    init            初始化仓库并提交首版
#    commit [msg]    重新快照并提交（有变化才 commit）
#    log [n]         最近 n 条变更
#    diff            最近一次提交改了什么
#    status          当前与上次快照的差异
#    verify          健康检查（心跳 + 提交是否真的在发生）
#  说明：只读拷贝，不修改 /etc；不收录含密码的连接配置。
#
#  2026-11-07 修复（审计静默失败 7 天）：
#   1. 仓库属主统一为 root，不再 chown 给调用 sudo 的普通用户
#      —— 这原是 dubious ownership 的根因，导致 root 跑 timer 时 git 拒绝操作
#   2. 每个关键步骤显式检查返回值，失败写 ERROR 日志并非 0 退出
#   3. 提交前后对比 rev-list 计数，计数不增加即判定失败（断言）
#   4. 每次运行写心跳文件；verify 检查心跳，能发现"timer 不跑"这件事
# ============================================================
set -uo pipefail

GITDIR=/var/lib/grayrouter/etcsnap
LOG=/var/log/grayrouter-audit.log
HEARTBEAT=/var/lib/grayrouter/.audit-heartbeat
FAILMARK=/var/lib/grayrouter/.audit-last-fail
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

# 失败收口：写错误日志、留失败标记、立即非 0 退出（systemd 会标 failed）
# 用 exit 而不是 return：调用处多为 `cmd || fail "..."`，return 只会让脚本继续往下跑
fail() {
  local msg="$1"
  log "ERROR: $msg"
  say "✗ $msg"
  mkdir -p "$(dirname "$FAILMARK")" 2>/dev/null || true
  printf '%s %s\n' "$(date '+%F %T')" "$msg" > "$FAILMARK" 2>/dev/null || true
  exit 1
}

# 并发锁：timer 和手动 commit 撞车会互踩 index.lock，两边都失败
acquire_lock() {
  local lock=/var/lib/grayrouter/.audit.lock
  mkdir -p /var/lib/grayrouter 2>/dev/null || return 0
  if command -v flock >/dev/null 2>&1; then
    exec 9>"$lock" 2>/dev/null || return 0
    if [ -e /proc/self/fd/9 ]; then
      flock -n 9 2>/dev/null || { say "⚠ 另一个审计实例正在运行，跳过本次"; exit 0; }
    fi
  fi
  return 0
}

# 磁盘不足时不要硬跑——写一半失败比不写更糟
check_disk() {
  local avail
  avail=$(df --output=avail /var/lib/grayrouter 2>/dev/null | tail -1 | tr -d ' ')
  [ -z "$avail" ] && return 0
  case "$avail" in ''|*[!0-9]*) return 0 ;; esac
  if [ "$avail" -lt 51200 ]; then
    fail "磁盘剩余不足 50MB（${avail}KB），停止本次审计"
  fi
  return 0
}

# 上一次异常退出残留的 index.lock 会让之后每次 commit 都失败（重现"静默 7 天"）
clear_stale_lock() {
  local l="$GITDIR/.git/index.lock"
  [ -e "$l" ] || return 0
  if pgrep -f "git.*${GITDIR}" >/dev/null 2>&1; then
    fail "检测到 git 正在操作仓库（index.lock 存在），本次跳过"
  fi
  rm -f "$l" 2>/dev/null || true
  log "WARN: 清理残留 index.lock"
  say "! 清理了残留的 index.lock"
  return 0
}

snapshot() {
  rm -rf "$DST" 2>/dev/null
  mkdir -p "$DST" || return 1
  local src
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
  } > "$DST/etc/NetworkManager/ap-band.txt" 2>/dev/null || true
  # 不收录可能含明文密码的文件（必须在 add 之前完成）
  find "$DST" -type f \( -name "*.psk" -o -name "*secret*" -o -name "*password*" \) -delete 2>/dev/null || true
  [ -d "$DST" ] || return 1
  return 0
}

do_init() {
  need_root "$@"
  acquire_lock
  mkdir -p "$GITDIR" || fail "无法创建 $GITDIR"
  [ -d "$GITDIR/.git" ] || git init -q "$GITDIR" || fail "git init 失败"
  git -C "$GITDIR" config user.name  "grayrouter-audit" || fail "配置 user.name 失败"
  git -C "$GITDIR" config user.email "audit@localhost"  || fail "配置 user.email 失败"
  snapshot || fail "快照失败"
  git -C "$GITDIR" add -A || fail "git add 失败"
  # 首次可能确实无变化
  git -C "$GITDIR" commit -q -m "init: $(date '+%F %T')" 2>/dev/null || true
  # 属主统一 root：timer 以 root 运行，属主不一致会触发 git 的 dubious ownership 保护
  chown -R root:root "$GITDIR" 2>/dev/null || fail "chown 仓库失败"
  # 仓库只给 root 看：快照里可能有口令类内容，别让其他用户读到
  chmod 700 "$GITDIR" 2>/dev/null || true
  local count
  count=$(git -C "$GITDIR" rev-list --count HEAD 2>/dev/null || echo 0)
  say "✓ 审计仓库已建立：$GITDIR（$count 次提交，属主 root）"
  printf '%s count=%s\n' "$(date '+%F %T')" "$count" > "$HEARTBEAT" 2>/dev/null || true
  return 0
}

do_commit() {
  need_root "$@"
  local msg="${1:-auto}" before after
  acquire_lock
  check_disk
  if [ ! -d "$GITDIR/.git" ]; then
    do_init "$@" || return 1
  fi
  clear_stale_lock
  snapshot || fail "快照失败"
  git -C "$GITDIR" add -A || fail "git add 失败"
  if git -C "$GITDIR" diff --cached --quiet 2>/dev/null; then
    say "无变化（仍是 $(git -C "$GITDIR" rev-list --count HEAD 2>/dev/null || echo 0) 次提交）"
    printf '%s count=%s changed=0\n' "$(date '+%F %T')" "$(git -C "$GITDIR" rev-list --count HEAD 2>/dev/null || echo 0)" > "$HEARTBEAT" 2>/dev/null || true
    rm -f "$FAILMARK" 2>/dev/null || true
    return 0
  fi
  before=$(git -C "$GITDIR" rev-list --count HEAD 2>/dev/null || echo 0)
  git -C "$GITDIR" commit -q -m "$msg $(date '+%F %T')" || fail "git commit 失败"
  after=$(git -C "$GITDIR" rev-list --count HEAD 2>/dev/null || echo 0)
  # 断言：提交必须让计数增加，否则说明 commit 实际没生效（历史上就是这样静默失败的）
  if [ "$after" -le "$before" ]; then
    fail "断言失败——提交数未增加（$before -> $after）"
    return 1
  fi
  say "✓ 已记录变更 #$after：$(git -C "$GITDIR" log -1 --oneline 2>/dev/null)"
  log "commit: $msg (#$after)"
  printf '%s count=%s changed=1\n' "$(date '+%F %T')" "$after" > "$HEARTBEAT" 2>/dev/null || true
  rm -f "$FAILMARK" 2>/dev/null || true
  return 0
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
  snapshot || fail "快照失败"
  git -C "$GITDIR" status --short 2>/dev/null | head -30
}

# 健康检查：心跳是否在更新、是否有未提交变更、最后一次提交时间
do_verify() {
  local ok=0 count last ts now hours dirty
  say "=== 配置审计健康检查 ==="
  if [ ! -d "$GITDIR/.git" ]; then
    say "✗ 审计仓库不存在：$GITDIR"
    return 1
  fi
  count=$(git -C "$GITDIR" rev-list --count HEAD 2>/dev/null || echo 0)
  last=$(git -C "$GITDIR" log -1 --format='%cI' 2>/dev/null || true)
  say "仓库：$GITDIR"
  say "提交数：$count"
  if [ -n "$last" ]; then
    now=$(date +%s)
    ts=$(date -d "$last" +%s 2>/dev/null || echo "$now")
    hours=$(( (now - ts) / 3600 ))
    say "最后提交：$last（约 $hours 小时前）"
  fi
  # 心跳：每次 commit 运行都会写，所以它能证明 timer 有没有在跑
  if [ -f "$HEARTBEAT" ]; then
    local hb_age hb_ts
    hb_ts=$(date -r "$HEARTBEAT" +%s 2>/dev/null || echo 0)
    hb_age=$(( ($(date +%s) - hb_ts) / 60 ))
    say "心跳：$(cat "$HEARTBEAT" 2>/dev/null)（${hb_age} 分钟前）"
    if [ "$hb_age" -gt 120 ]; then
      say "⚠ 心跳超过 2 小时没更新——audit.timer 可能没在跑"
      ok=1
    fi
  else
    say "⚠ 没有心跳文件——audit 从未成功运行过"
    ok=1
  fi
  # 未提交变更 = 快照在更新但提交失败
  dirty=$(git -C "$GITDIR" status --porcelain 2>/dev/null | head -1)
  if [ -n "$dirty" ]; then
    say "⚠ 工作区有未提交的变更（快照在更新、提交没成功）"
    ok=1
  else
    say "工作区：干净"
  fi
  if [ -f "$FAILMARK" ]; then
    say "⚠ 存在失败标记：$(cat "$FAILMARK" 2>/dev/null)"
    ok=1
  fi
  if [ "$ok" -eq 0 ]; then
    say "✓ 审计正常"
    return 0
  fi
  return 1
}

case "${1:-status}" in
  init) do_init "$@" ;;
  commit) shift; do_commit "${1:-auto}" ;;
  log) shift; do_log "${1:-10}" ;;
  diff) do_diff ;;
  status) do_status ;;
  verify) do_verify ;;
  *) say "用法：$0 {init|commit [msg]|log [n]|diff|status|verify}"; exit 2 ;;
esac
