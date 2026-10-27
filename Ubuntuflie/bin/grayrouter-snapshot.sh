#!/usr/bin/env bash
# ============================================================
#  grayrrouter-snapshot.sh —— 配置快照（改动前存档，出问题一键回滚）
#    sudo bash grayrrouter-snapshot.sh save            存一份
#    sudo bash grayrrouter-snapshot.sh list            列出
#    sudo bash grayrrouter-snapshot.sh restore [名字]  恢复（默认最新）
#    快照目录：/var/lib/grayrouter/snapshots  保留最近 10 份
# ============================================================
set -uo pipefail
DIR=/var/lib/grayrouter/snapshots
KEEP=10
[ "$(id -u)" -eq 0 ] || { echo "需要 root：sudo bash $0 $*"; exit 1; }
mkdir -p "$DIR" || exit 1

gr_user() { sed -n 's/^GR_USER="\{0,1\}\([^"]*\)"\{0,1\}$/\1/p' /etc/grayrouter/ctl.conf 2>/dev/null | head -1; }

collect() {
  local list="" f u h
  for f in /etc/netplan /etc/NetworkManager/system-connections /etc/grayrouter; do
    [ -e "$f" ] && list="$list ${f#/}"
  done
  for f in /etc/systemd/system/grayrouter* /etc/systemd/system/ua3f* /etc/systemd/system/netplan*; do
    [ -e "$f" ] && list="$list ${f#/}"
  done
  u=$(gr_user); [ -z "$u" ] && u=$(logname 2>/dev/null || echo "")
  if [ -n "$u" ]; then
    h=$(getent passwd "$u" | cut -d: -f6)
    for f in "$h/config.yaml" "$h/Config.yaml" "$h/ua3f.yaml"; do
      [ -f "$f" ] && list="$list ${f#/}"
    done
  fi
  echo "$list"
}

do_save() {
  local ts tag="$1"
  ts=$(date '+%Y%m%d_%H%M%S')${tag:+_$tag}
  local list; list=$(collect)
  [ -z "${list// /}" ] && { echo "没有可备份的配置"; exit 2; }
  ( cd / && tar czf "$DIR/$ts.tar.gz" $list ) 2>/dev/null || { echo "打包失败"; exit 3; }
  echo "已保存：$DIR/$ts.tar.gz（$(stat -c %s "$DIR/$ts.tar.gz") 字节）"
  ls -1t "$DIR"/*.tar.gz 2>/dev/null | tail -n +$((KEEP+1)) | xargs -r rm -f
}

do_list() {
  [ -d "$DIR" ] || { echo "还没有快照"; return; }
  ls -1t "$DIR"/*.tar.gz 2>/dev/null | head -15 | while read -r f; do
    printf '%s  %8s  %s\n' "$(basename "$f" .tar.gz)" "$(stat -c %s "$f")" "$(stat -c %y "$f" | cut -d. -f1)"
  done
}

do_restore() {
  local want="$1" f
  if [ -z "$want" ] || [ "$want" = "latest" ]; then
    f=$(ls -1t "$DIR"/*.tar.gz 2>/dev/null | head -1)
  else
    case "$want" in */*) f="$want" ;; *) f="$DIR/$want.tar.gz" ;; esac
  fi
  [ -f "$f" ] || { echo "找不到快照：$want"; exit 2; }
  echo "恢复前先把当前配置存一份…"
  do_save pre-restore >/dev/null 2>&1 || true
  echo "正在恢复：$(basename "$f")"
  ( cd / && tar xzf "$f" ) || { echo "解包失败"; exit 3; }
  systemctl daemon-reload 2>/dev/null
  echo "已恢复。正在重启 NetworkManager…"
  systemctl restart NetworkManager 2>/dev/null || true
  sleep 6
  systemctl is-active --quiet NetworkManager && echo "√ NetworkManager 已起来" || echo "× NetworkManager 没起来，请本地检查"
}

case "${1:-list}" in
  save)    do_save "${2:-manual}" ;;
  list)    do_list ;;
  restore) do_restore "${2:-latest}" ;;
  *) echo "用法：sudo bash $0 save [备注] | list | restore [名字|latest]"; exit 2 ;;
esac
