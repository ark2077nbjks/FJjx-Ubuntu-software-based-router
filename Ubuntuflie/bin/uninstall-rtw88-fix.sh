#!/usr/bin/env bash
# 回滚：驱动参数、省电配置、信标自愈、开机自启
set -u
[ "$(id -u)" -eq 0 ] || { echo "要用 sudo 跑"; exit 1; }
rm -f /etc/modprobe.d/rtw88-stable.conf
CONF=/etc/NetworkManager/conf.d/default-wifi-powersave-on.conf
BAK=$(ls -1t "$CONF".bak_* 2>/dev/null | head -1)
[ -n "${BAK:-}" ] && cp -a "$BAK" "$CONF"
systemctl disable --now grayrouter-beacon.timer 2>/dev/null
systemctl disable grayrouter-boot.service 2>/dev/null
rm -f /etc/systemd/system/grayrouter-beacon.service /etc/systemd/system/grayrouter-beacon.timer \
      /etc/systemd/system/grayrouter-boot.service \
      /usr/local/bin/grayrouter-beacon-watch.sh /usr/local/bin/grayrouter-boot-up.sh
rm -f /etc/grayrouter/boot-mode
systemctl daemon-reload
echo "已回滚。驱动参数重启后失效。"
