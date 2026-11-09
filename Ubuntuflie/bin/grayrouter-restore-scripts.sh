#!/usr/bin/env bash
# ============================================================
#  grayrouter-restore-scripts.sh —— 从「装前脚本快照」恢复 GrayRouter 脚本
#
#  背景：install.sh 安装前会把现有脚本打包到 $GR_VAR/snapshots/，
#        本脚本负责列出 / 校验 / 恢复这些快照。
#        改坏脚本时不用重装整个安装包，一条命令回到装之前。
#
#  用法：
#    grayrouter-restore-scripts.sh list               列快照
#    grayrouter-restore-scripts.sh show   [名字]      看包内文件
#    grayrouter-restore-scripts.sh verify [名字]      校验包
#    grayrouter-restore-scripts.sh restore [名字]     恢复（默认 latest）
#    grayrouter-restore-scripts.sh restore <名字> --yes  跳过确认
#
#  保险：restore 前会先把「当前脚本」也打一份快照，收不了场还能再回来。
# ============================================================
set -uo pipefail

SNAP_DIR=${GR_SNAP_DIR:-/var/lib/grayrouter/snapshots}
LOG=/var/log/grayrouter-restore-scripts.log
SELF=$(basename "$0")

say()  { printf '%s\n' "$*"; }
ok()   { printf '✓ %s\n' "$*"; }
warn() { printf '! %s\n' "$*"; }
die()  { printf '✗ %s\n' "$*" >&2; exit 1; }
log()  { echo "$(date '+%F %T') $*" >> "$LOG" 2>/dev/null || true; }
need_root() { [ "$(id -u)" -eq 0 ] || die "需要 root：sudo bash $0 $*"; }

# 把名字解析成快照路径：支持 full name / 前缀 / latest
resolve_snap() {
  local want="${1:-latest}" f
  if [ "$want" = "latest" ]; then
    ls -t "$SNAP_DIR"/scripts-*.tar.gz 2>/dev/null | head -1
    return
  fi
  for f in "$SNAP_DIR"/scripts-*.tar.gz; do
    [ -e "$f" ] || continue
    case "$(basename "$f")" in
      *"$want"*) printf '%s\n' "$f"; return ;;
    esac
  done
  return 1
}

do_list() {
  if [ ! -d "$SNAP_DIR" ]; then
    say "还没有任何装前快照（$SNAP_DIR 不存在）"
    return 0
  fi
  local n=0 f
  say "=== 装前脚本快照（$SNAP_DIR） ==="
  for f in $(ls -t "$SNAP_DIR"/scripts-*.tar.gz 2>/dev/null); do
    n=$((n+1))
    printf '  %-52s %6s  %s\n' "$(basename "$f")" "$(du -h "$f" | cut -f1)" "$(stat -c %y "$f" 2>/dev/null | cut -d. -f1)"
  done
  [ "$n" -gt 0 ] || say "（没有快照）"
  say ""
  say "恢复最近一份：sudo $SELF restore latest"
}

do_show() {
  local p
  p=$(resolve_snap "${1:-latest}") || die "找不到快照：${1:-latest}"
  say "包内文件（$(basename "$p")）："
  tar tzf "$p" 2>/dev/null | head -60
  local cnt; cnt=$(tar tzf "$p" 2>/dev/null | wc -l)
  say "共 $cnt 个文件"
}

do_verify() {
  local p
  p=$(resolve_snap "${1:-latest}") || die "找不到快照：${1:-latest}"
  gzip -t "$p" 2>/dev/null || die "包损坏：$(basename "$p")"
  tar tzf "$p" >/dev/null 2>&1 || die "tar 结构损坏：$(basename "$p")"
  ok "校验通过：$(basename "$p")（$(tar tzf "$p" 2>/dev/null | wc -l) 个文件）"
}

do_restore() {
  local want="${1:-latest}" yes=0
  [ "${2:-}" = "--yes" ] && yes=1
  need_root "$@"
  local p
  p=$(resolve_snap "$want") || die "找不到快照：$want（先 list 看有哪些）"
  gzip -t "$p" 2>/dev/null || die "快照包损坏，拒绝恢复：$(basename "$p")"

  say "== 将恢复 =="
  say "  快照：$(basename "$p")"
  say "  文件：$(tar tzf "$p" 2>/dev/null | wc -l) 个"
  tar tzf "$p" 2>/dev/null | head -12
  say ""

  if [ "$yes" -ne 1 ]; then
    printf '  确认恢复？（输入 y 继续） '
    read -r ans
    [ "$ans" = "y" ] || { say "已取消，脚本未动"; return 0; }
  fi

  # 恢复前先把当前脚本再存一份（防二次翻车）
  local pre
  pre="$SNAP_DIR/scripts-$(date '+%Y%m%d_%H%M%S')_pre-restore.tar.gz"
  local list
  list=$(mktemp 2>/dev/null || echo /tmp/gr-restore-list.$$)
  { ls /usr/local/bin/grayrouter-*.sh 2>/dev/null; ls /opt/grayrouter/bin/grayrouter-*.sh 2>/dev/null; } > "$list"
  if [ -s "$list" ]; then
    tar czf "$pre" -P -T "$list" 2>/dev/null && ok "当前脚本已存为：$(basename "$pre")" || warn "当前脚本快照失败（继续）"
  fi
  rm -f "$list"

  # 解包到 /（快照里是绝对路径，-P 保留）
  tar xzf "$p" -C / -P 2>/dev/null || die "解包失败"
  ok "已恢复脚本"
  log "restore $(basename "$p")"

  # 让 systemd 感知
  systemctl daemon-reload 2>/dev/null && say "  已 daemon-reload（未重启任何服务）" || true
  say ""
  say "提示：脚本已换回旧版，但服务还在跑新版的进程。"
  say "  需要重启对应服务才完全生效，例如："
  say "    sudo systemctl restart grayrrouter-watch.timer grayrrouter-audit.timer"
  say "  注意：重启网络相关服务会短暂断网，由你决定时机。"
  return 0
}

case "${1:-list}" in
  list)    do_list ;;
  show)    shift; do_show "${1:-latest}" ;;
  verify)  shift; do_verify "${1:-latest}" ;;
  restore) shift; do_restore "${1:-latest}" "${2:-}" ;;
  help|-h|--help)
    sed -n '2,22p' "$0" | sed 's/^# \{0,1\}//'
    ;;
  *) say "用法：$SELF {list|show [名字]|verify [名字]|restore [名字] [--yes]}"; exit 2 ;;
esac
