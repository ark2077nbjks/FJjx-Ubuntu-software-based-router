#!/usr/bin/env bash
# ============================================================
#  GrayRouter 图形安装器（双击运行）
#  只用 zenity 收集参数，真正的安装在 install.sh 里，逻辑不重复
#  没有 zenity / pkexec 时自动退回终端模式
# ============================================================
set -u
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
. "$HERE/lib/common.sh"
. "$HERE/lib/detect.sh"

TITLE="GrayRouter 一键部署"

# ---- 依赖检查 ----
if ! have zenity; then
  MSG="这台机器没装 zenity（图形对话框），改用终端安装。

  sudo apt install zenity
  然后重新双击；或者直接在终端里跑：
  sudo bash install.sh"
  if have x-terminal-emulator; then x-terminal-emulator -e bash -c "echo '$MSG'; read -p '回车关闭'"; else echo "$MSG"; fi
  exit 1
fi

zenity --info --title="$TITLE" --width=520 --no-wrap \
  --text="这个程序会把当前 Ubuntu 变成软路由。\n\n它会先检查网卡，再让你选一种接法，\n最后才动手（动手前会再确认一次）。" 2>/dev/null || exit 0

# ---- 探测 ----
zenity --info --title="$TITLE" --text="正在检查网卡，请稍候…" --timeout=1 2>/dev/null || true
gr_scan_nics
gr_scan_wan
AVAIL=$(gr_topologies | tr -s ' ')

if [ -z "${AVAIL// /}" ]; then
  zenity --error --title="$TITLE" --width=560 \
    --text="这台机器不满足任何部署条件。\n\n要求：至少两块网口（有线/无线混搭也行）。\n检测到：有线 ${#GR_WIRED[@]} 块，无线 ${#GR_WIFI[@]} 块。" 2>/dev/null
  exit 3
fi

# 网卡概览
SUMMARY="检测到的网卡：\n"
for i in "${GR_WIRED[@]:-}"; do [ -n "$i" ] && SUMMARY="$SUMMARY\n  有线  $i"; done
for i in "${GR_WIFI[@]:-}";  do [ -n "$i" ] && SUMMARY="$SUMMARY\n  无线  $i"; done
[ -n "${GR_DEF_IF:-}" ] && SUMMARY="$SUMMARY\n\n当前联网走：$GR_DEF_IF（${GR_WAN_KIND:-未知}）"

# ---- 选拓扑 ----
LIST=""
case " $AVAIL " in *" A "*) LIST="$LIST FALSE A  有线 WAN → 无线发热点（笔记本/小主机 推荐）" ;; esac
case " $AVAIL " in *" B "*) LIST="$LIST FALSE B  有线 WAN → 有线 LAN（双网口，最稳）" ;; esac
case " $AVAIL " in *" C "*) LIST="$LIST FALSE C  无线 WAN（连别人 WiFi）→ 有线 LAN" ;; esac
# 默认选中第一条
LIST=$(echo "$LIST" | sed '0,/FALSE/s//TRUE/')

TOPO=$(zenity --list --title="$TITLE" --width=640 --height=360 \
  --text="$SUMMARY\n\n选一种接法：" \
  --radiolist --column="" --column="编号" --column="说明" \
  $LIST 2>/dev/null) || exit 0
TOPO=$(echo "$TOPO" | awk '{print $1}')
[ -z "$TOPO" ] && exit 0

# ---- 参数 ----
SSID=""; PASS=""; LANIP=""
if [ "$TOPO" = "A" ]; then
  SSID=$(zenity --entry --title="$TITLE" --text="给这个热点起个名字：" --entry-text="Gray-Router" 2>/dev/null) || exit 0
  [ -z "$SSID" ] && SSID="Gray-Router"
  PASS=$(zenity --password --title="$TITLE" 2>/dev/null) || exit 0
  while [ ${#PASS} -lt 8 ]; do
    PASS=$(zenity --password --title="$TITLE" \
      --text="WiFi 密码至少 8 位，再输一次：" 2>/dev/null) || exit 0
  done
elif [ "$TOPO" = "C" ]; then
  UPS=$(zenity --entry --title="$TITLE" --text="要连的上游 WiFi 名称：" 2>/dev/null) || exit 0
  [ -z "$UPS" ] && exit 0
  zenity --info --title="$TITLE" --text="上游 WiFi 的密码请在接下来的终端提示里输入（不经过本窗口）。" 2>/dev/null || true
fi

LANIP=$(zenity --entry --title="$TITLE" --text="LAN 网关地址（其他设备连上后的网关）：" --entry-text="192.168.50.1" 2>/dev/null) || exit 0
[ -z "$LANIP" ] && LANIP="192.168.50.1"

HARDEN="none"
zenity --question --title="$TITLE" --width=560 \
  --text="要不要顺便做 TTL 伪装？\n\n校园网共享常用；普通宽带不需要。" 2>/dev/null && HARDEN="ttl"

# ---- 确认 ----
CONF="即将执行：\n\n拓扑：$TOPO\n热点：${SSID:-无（有线 LAN）}\n网关：$LANIP\nTTL 伪装：$HARDEN\n\n注意：会弹一次系统密码框（授权安装）。"
zenity --question --title="$TITLE" --width=520 --text="$CONF" --ok-label="开始安装" --cancel-label="取消" 2>/dev/null || exit 0

# ---- 执行 ----
LOG="$HOME/grayrouter-install-$(date +%Y%m%d_%H%M%S).log"
ARGS=(--yes --topology="$TOPO" --lan-ip="$LANIP" --harden="$HARDEN")
[ -n "$SSID" ] && ARGS+=(--ssid="$SSID")
[ -n "$PASS" ] && ARGS+=(--password="$PASS")

run_install() {
  if have pkexec; then
    pkexec env PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
      bash "$HERE/install.sh" "${ARGS[@]}"
  else
    if have x-terminal-emulator; then
      x-terminal-emulator -e bash -c "bash '$HERE/install.sh' ${ARGS[*]} | tee '$LOG'; echo; read -p '完成后回车关闭'"
    else
      sudo -S bash "$HERE/install.sh" "${ARGS[@]}" </dev/null
    fi
  fi
}

# 进度窗口（安装过程中显示不确定进度条，装完关掉）
( zenity --progress --title="$TITLE" --pulsate --auto-close --no-cancel \
    --text="正在安装，请在弹出的系统密码框里输入密码…\n装完这个窗口会自己关掉。" 2>/dev/null ) &
PROG_PID=$!

run_install >"$LOG" 2>&1
RC=$?
kill $PROG_PID 2>/dev/null
wait $PROG_PID 2>/dev/null

# ---- 结果 ----
if [ $RC -eq 0 ]; then
  zenity --info --title="$TITLE" --width=560 \
    --text="安装完成。\n\n手机或电脑搜 WiFi「${SSID:-有线 LAN}」连上即可上网。\n\n日志：$LOG\n卸载：双击同目录的「GrayRouter-卸载.desktop」" 2>/dev/null
  zenity --text-info --title="安装日志" --filename="$LOG" --width=780 --height=520 --no-markup 2>/dev/null
else
  zenity --error --title="$TITLE" --width=640 \
    --text="安装没有成功（退出码 $RC）。\n\n下面打开日志，把它发给部署方即可。\n可以双击「GrayRouter-卸载.desktop」回滚。" 2>/dev/null
  zenity --text-info --title="安装日志（失败）" --filename="$LOG" --width=780 --height=560 2>/dev/null
fi
exit $RC
