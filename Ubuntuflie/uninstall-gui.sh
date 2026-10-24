#!/usr/bin/env bash
# GrayRouter 图形卸载（双击运行）
set -u
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

if ! command -v zenity >/dev/null 2>&1; then
  x-terminal-emulator -e bash -c "sudo bash '$HERE/uninstall.sh'; read -p '回车关闭'" 2>/dev/null || sudo bash "$HERE/uninstall.sh"
  exit $?
fi

zenity --question --title="GrayRouter 卸载" --width=520 \
  --text="确定要卸载软路由吗？\n\n会删除热点、开机服务、TTL 规则；\n原有网络连接不受影响。" 2>/dev/null || exit 0

LOG="$HOME/grayrouter-uninstall-$(date +%Y%m%d_%H%M%S).log"
( zenity --progress --title="GrayRouter 卸载" --pulsate --auto-close --no-cancel \
    --text="正在卸载…" 2>/dev/null ) &
PROG_PID=$!

if command -v pkexec >/dev/null 2>&1; then
  pkexec env PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    bash "$HERE/uninstall.sh" -y >"$LOG" 2>&1
else
  sudo bash "$HERE/uninstall.sh" -y >"$LOG" 2>&1
fi
RC=$?
kill $PROG_PID 2>/dev/null

if [ $RC -eq 0 ]; then
  zenity --info --title="GrayRouter 卸载" --text="卸载完成。" 2>/dev/null
else
  zenity --error --title="GrayRouter 卸载" --text="卸载失败（$RC），下面打开日志。" 2>/dev/null
  zenity --text-info --filename="$LOG" --width=760 --height=460 2>/dev/null
fi
exit $RC
