#!/usr/bin/env bash
# ============================================================
#  set-band.sh <5g|2g> [信道]  —— 统一切频段入口
#    5g → band a  ，信道默认 36（36/40/44/48/149/153/157/161/165）
#    2g → band bg ，信道默认 6 （1~13）
#    切之前先上 150 秒死手保险；失败自动回滚到备份里的原设置
# ============================================================
set -uo pipefail
WANT="${1:-}"; CH="${2:-}"
CON="Gray-Router"
IF="wlp2s0"
DIR="/root/grayrouter-backup"
SET="$DIR/settings.txt"
BK="$DIR/Gray-Router.good.nmconnection"
PANIC="/usr/local/bin/grayrouter-panic.sh"
SELF=$(cd "$(dirname "$0")" && pwd)

if [ "$(id -u)" -ne 0 ]; then
  echo "需要管理员权限，正在提权…"
  exec sudo bash "$0" "$@"
fi

case "$WANT" in
  5g|5|a)   BAND="a";  DEF=36; LABEL="5 GHz" ;;
  2g|24|bg) BAND="bg"; DEF=6;  LABEL="2.4 GHz" ;;
  *) echo "用法：set-band.sh <5g|2g> [信道]"; exit 2 ;;
esac
[ -n "${CH:-}" ] || CH="$DEF"

# ---- 信道合法性（只放实测能用的）----
if [ "$BAND" = "a" ]; then
  case "$CH" in
    36|40|44|48|149|153|157|161|165) ;;
    *) echo "❌ 5GHz 不支持信道 $CH（可选 36/40/44/48/149/153/157/161/165）"; exit 2 ;;
  esac
else
  case "$CH" in ''|*[!0-9]*) echo "❌ 信道必须是数字"; exit 2 ;; esac
  if [ "$CH" -lt 1 ] || [ "$CH" -gt 13 ]; then
    echo "❌ 2.4GHz 只支持信道 1~13"; exit 2
  fi
fi

echo "===== 切到 $LABEL / 信道 $CH ====="
mkdir -p "$DIR"

# ---- 0) 回滚程序 + 国家码 ----
[ -f "$SELF/grayrouter-panic.sh" ] && install -m 755 "$SELF/grayrouter-panic.sh" "$PANIC"
[ -x "$PANIC" ] || { echo "❌ 缺少 $PANIC，先别切"; exit 1; }

UNIT=/etc/systemd/system/wifi-regdom.service
if [ ! -f "$UNIT" ]; then
  echo "[0/6] 安装国家码固化服务（只做一次）"
  cat > "$UNIT" <<'EOF'
[Unit]
Description=Set WiFi regulatory domain CN
After=network-pre.target
Before=NetworkManager.service

[Service]
Type=oneshot
ExecStart=/usr/sbin/iw reg set CN
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload
  systemctl enable --now wifi-regdom.service >/dev/null 2>&1
fi
iw reg set CN

# ---- 1) 备份（只在没有备份时做）----
if [ -f "$SET" ]; then
  echo "[1/6] 已有备份：$(tr '\n' ' ' < "$SET")"
else
  B=$(nmcli -g 802-11-wireless.band con show "$CON" 2>/dev/null)
  C=$(nmcli -g 802-11-wireless.channel con show "$CON" 2>/dev/null)
  { echo "BAND=${B:-bg}"; echo "CH=${C:-6}"; } > "$SET"
  PROFILE=$(nmcli -t -f NAME,FILENAME con show | awk -F: -v n="$CON" '$1==n{print $2}')
  [ -n "${PROFILE:-}" ] && [ -f "$PROFILE" ] && cp -f "$PROFILE" "$BK"
  echo "[1/6] 已备份原设置：band=${B:-bg} channel=${C:-6}"
fi

# ---- 2) 死手保险 ----
systemctl stop gr5g-deadman.timer >/dev/null 2>&1 || true
systemctl reset-failed gr5g-deadman.timer >/dev/null 2>&1 || true
if systemd-run --unit=gr5g-deadman --on-active=150 "$PANIC" auto >/dev/null 2>&1; then
  echo "[2/6] ✅ 已上保险：150 秒内没确认成功，systemd 自动回滚"
else
  echo "[2/6] ⚠ 保险没装上，只能靠脚本自己回滚"
fi

# ---- 3) 写参数 ----
echo "[3/6] band=$BAND channel=$CH"
if ! nmcli con modify "$CON" 802-11-wireless.band "$BAND" 802-11-wireless.channel "$CH"; then
  echo "❌ 写参数失败，立即回滚"; "$PANIC" manual; exit 1
fi

# ---- 4) 重启 AP ----
echo "[4/6] 重启 AP（WiFi 会断 15~25 秒）"
nmcli con down "$CON" >/dev/null 2>&1 || true
nmcli con up "$CON" >/dev/null 2>&1 || true
echo "[5/6] 等 25 秒看结果…"
sleep 25

# ---- 5) 核验 ----
GB=$(nmcli -g 802-11-wireless.band con show "$CON" 2>/dev/null)
GC=$(nmcli -g 802-11-wireless.channel con show "$CON" 2>/dev/null)
CUR=$(iw dev "$IF" info 2>/dev/null | grep -o "channel [0-9]*" | head -1)

if [ "$GB" = "$BAND" ] && [ "$GC" = "$CH" ] && [ "$CUR" = "channel $CH" ]; then
  systemctl stop gr5g-deadman.timer >/dev/null 2>&1 || true
  systemctl reset-failed gr5g-deadman.timer >/dev/null 2>&1 || true
  mkdir -p /etc/grayrouter; printf 'BAND=%s\nCH=%s\n' "$BAND" "$CH" > /etc/grayrouter/band.env
  echo "[6/6] ✅ 成功：$LABEL 信道 $CH 已生效（$CUR）"
else
  echo "[6/6] ❌ 没成功（band=$GB channel=$GC 实际=$CUR），立即回滚"
  "$PANIC" auto
fi
