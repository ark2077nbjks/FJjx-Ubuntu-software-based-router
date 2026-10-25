#!/usr/bin/env bash
# GrayRouter 卸载：按 journal 逆序撤销，恢复原状
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
. "$HERE/lib/common.sh"

GR_ASSUME_YES=0
for a in "$@"; do
  case "$a" in
    -y|--yes) GR_ASSUME_YES=1 ;;
    -h|--help) sed -n '2,4p' "$0"; exit 0 ;;
  esac
done

need_root
[ -f "$GR_JOURNAL" ] || { warn "没找到安装记录 $GR_JOURNAL（可能没装过）"; }

printf "将撤销 GrayRouter 的所有改动（现有网络配置不受影响）。\n"
confirm "继续？" "n" || die "已取消"

step "1/3 停止服务"
systemctl disable --now grayrouter.service >/dev/null 2>&1 && ok "服务已停" || info "服务本来就没在跑"
systemctl disable --now ua3f.service >/dev/null 2>&1 && ok "伪装层已停" || true
[ -x /opt/grayrouter/bin/ua3f-rules.sh ] && bash /opt/grayrouter/bin/ua3f-rules.sh clear >/dev/null 2>&1 || true

step "2/3 逆序回滚"
if [ -f "$GR_JOURNAL" ]; then
  tac "$GR_JOURNAL" | while IFS=$'\t' read -r kind val; do
    case "$kind" in
      NM_CON)       nmcli connection delete "$val" >/dev/null 2>&1 && ok "删除连接 $val" ;;
      SYSTEMD_UNIT) rm -f "$val" && ok "删除 unit $(basename "$val")" ;;
      FILE_CREATED) rm -rf "$val" && ok "删除 $val" ;;
      FILE_BACKUP)  orig=${val%%|*}; bak=${val##*|}
                    [ -f "$bak" ] && cp -a "$bak" "$orig" && ok "恢复 $orig" ;;
      DIR_CREATED)  rmdir "$val" 2>/dev/null && ok "删除目录 $val" || info "目录非空，保留 $val" ;;
      SYSCTL)       info "sysctl 已由文件删除回退" ;;
    esac
  done
fi

step "3/3 清理"
rm -f /etc/sysctl.d/99-grayrouter.conf
systemctl daemon-reload
sysctl -q --system >/dev/null 2>&1 || true
rm -rf "$GR_OPT"
mkdir -p "$GR_VAR"
mv "$GR_JOURNAL" "$GR_VAR/journal.uninstalled.$(date +%s)" 2>/dev/null
ok "已清理（日志留在 $GR_VAR）"

echo
printf "${C_G}卸载完成。${C_N}WiFi 热点已删除；原有网络连接未受影响。\n"
