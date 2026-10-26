#!/usr/bin/env bash
# ============================================================
#  grayrouter-panic.sh —— 回滚到 2.4GHz（四层，逐层兜底）
#   L1 nmcli 改参数   L2 用备份文件覆盖   L3 重启 NetworkManager
#   L4 提示重启机器（netplan 开机会重建 2.4G 配置）
#  用法：sudo grayrouter-panic.sh [auto|manual]
# ============================================================
set -uo pipefail
CON="Gray-Router"
IF="wlp2s0"
DIR="/root/grayrouter-backup"
SET="$DIR/settings.txt"
BK="$DIR/Gray-Router.good.nmconnection"
LOG="/var/log/grayrouter-panic.log"
WHY="${1:-manual}"

if [ "$(id -u)" -ne 0 ]; then
  exec sudo bash "$0" "$@"
fi

echo "===== $(date '+%F %T') 回滚到 2.4GHz（触发：$WHY）=====" | tee -a "$LOG"

# ---- 读出原始参数 ----
BAND="bg"; CH="6"
if [ -f "$SET" ]; then
  b=$(sed -n 's/^BAND=//p' "$SET" | head -1); c=$(sed -n 's/^CH=//p' "$SET" | head -1)
  [ -n "${b:-}" ] && BAND="$b"
  [ -n "${c:-}" ] && CH="$c"
fi
echo "      目标：band=$BAND channel=$CH" | tee -a "$LOG"

ok() { iw dev "$IF" info 2>/dev/null | grep -q "channel $CH"; }

# ---- L1：改参数 ----
echo "[L1] nmcli 改回 band=$BAND channel=$CH" | tee -a "$LOG"
nmcli con modify "$CON" 802-11-wireless.band "$BAND" 802-11-wireless.channel "$CH" >>"$LOG" 2>&1
nmcli con down "$CON" >>"$LOG" 2>&1
nmcli con up "$CON" >>"$LOG" 2>&1
sleep 8
if ok; then echo "✅ L1 成功，已回到 2.4GHz（channel $CH）" | tee -a "$LOG"; exit 0; fi

# ---- L2：覆盖备份的 profile 文件 ----
if [ -f "$BK" ]; then
  echo "[L2] 用备份文件覆盖 profile" | tee -a "$LOG"
  TARGET=$(nmcli -t -f NAME,FILENAME con show | awk -F: -v n="$CON" '$1==n{print $2}')
  [ -n "${TARGET:-}" ] || TARGET="/run/NetworkManager/system-connections/$CON.nmconnection"
  cp -f "$BK" "$TARGET" && chmod 600 "$TARGET" >>"$LOG" 2>&1
  nmcli con reload >>"$LOG" 2>&1
  nmcli con up "$CON" >>"$LOG" 2>&1
  sleep 8
  if ok; then echo "✅ L2 成功，已回到 2.4GHz" | tee -a "$LOG"; exit 0; fi
else
  echo "[L2] 没有备份文件，跳过" | tee -a "$LOG"
fi

# ---- L3：重启 NetworkManager ----
echo "[L3] 重启 NetworkManager" | tee -a "$LOG"
systemctl restart NetworkManager >>"$LOG" 2>&1
sleep 15
nmcli con up "$CON" >>"$LOG" 2>&1
sleep 6
if ok; then echo "✅ L3 成功，已回到 2.4GHz" | tee -a "$LOG"; exit 0; fi

# ---- L4 ----
echo "❌ 前三层都没成功。最后一招：重启这台机器" | tee -a "$LOG"
echo "   这个 AP 配置由 netplan 生成，开机会重新建成 2.4GHz，所以重启必然恢复。" | tee -a "$LOG"
echo "   命令：sudo reboot    （或长按电源键）" | tee -a "$LOG"
exit 3
