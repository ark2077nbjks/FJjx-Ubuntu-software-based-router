#!/usr/bin/env bash
# 定时备份（对标 iKuai「定时任务」/ OpenWrt cron）：每天固定时间打包配置
# 用法：get | set HH:MM | off
set -u
CONF=/etc/grayrouter/cron.conf
CRON=/etc/cron.d/grayrouter-panel
mkdir -p /etc/grayrouter

apply() {
  [ -f "$CONF" ] && . "$CONF"
  rm -f "$CRON"
  [ "${BACKUP:-off}" = "off" ] && return 0
  local hh=${BACKUP%%:*} mm=${BACKUP##*:}
  cat >"$CRON" <<EOF
SHELL=/bin/sh
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
$mm $hh * * * root /usr/local/bin/grayrouter-backup.sh /var/lib/grayrouter/backups >/var/log/grayrouter-cron.log 2>&1
EOF
  chmod 644 "$CRON"
}

case "${1:-get}" in
  get)
    [ -f "$CONF" ] && . "$CONF"
    echo "BACKUP=${BACKUP:-off}"
    ;;
  set)
    t=${2:-}
    if ! [[ "$t" =~ ^([01][0-9]|2[0-3]):[0-5][0-9]$ ]]; then
      echo "时间格式要 HH:MM（24 小时制），例如 03:30"; exit 2
    fi
    echo "BACKUP=$t" > "$CONF"
    apply
    echo "已设为每天 $t 自动备份"
    ;;
  off)
    echo "BACKUP=off" > "$CONF"
    apply
    echo "已关闭定时备份"
    ;;
  *) echo "用法: get | set HH:MM | off"; exit 2 ;;
esac
