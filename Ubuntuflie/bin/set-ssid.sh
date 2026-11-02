#!/usr/bin/env bash
# ============================================================
#  set-ssid.sh <新名字>  —— 改 WiFi 名称（SSID）
#    · 改前备份原名称，改后重启 AP 并读回验证
#    · 验证失败自动回滚到原名
#    · 名字限制：1~32 个字符，不能含空格（遥控通道按空格分隔参数）
# ============================================================
set -uo pipefail
NEW="${1:-}"
CON="Gray-Router"
DIR="/root/grayrouter-backup"
BAK="$DIR/ssid.bak"

if [ "$(id -u)" -ne 0 ]; then
  echo "需要管理员权限，正在提权…"
  exec sudo bash "$0" "$@"
fi

[ -n "$NEW" ] || { echo "用法：set-ssid.sh <新名字>"; exit 2; }

# 校验：不含空白/控制字符，UTF-8 字节数 1~32（802.11 标准上限 32 octets）
if ! printf '%s' "$NEW" | grep -qE '^[^[:space:][:cntrl:]]+$'; then
  echo "❌ 名字不合法：不能含空格或控制字符"
  exit 2
fi
BYTES=$(printf '%s' "$NEW" | wc -c | tr -d ' ')
if [ "$BYTES" -lt 1 ] || [ "$BYTES" -gt 32 ]; then
  echo "❌ 名字太长：当前 ${BYTES} 字节，上限 32 字节（一个汉字算 3 字节）"
  exit 2
fi

OLD=$(nmcli -g 802-11-wireless.ssid con show "$CON" 2>/dev/null)
if [ -z "$OLD" ]; then
  echo "❌ 找不到无线连接 $CON（先确认 AP 已配置）"
  exit 1
fi

if [ "$OLD" = "$NEW" ]; then
  echo "√ 名称没变，当前就是 $NEW"
  exit 0
fi

echo "===== 修改 WiFi 名称 ====="
echo "原名称：$OLD"
echo "新名称：$NEW"

mkdir -p "$DIR" 2>/dev/null
printf '%s\n' "$OLD" > "$BAK"
echo "[1/4] 已备份原名"

if ! nmcli con modify "$CON" 802-11-wireless.ssid "$NEW"; then
  echo "❌ 写入失败，无线设置未改动"
  exit 1
fi
echo "[2/4] 已写入新名称"

nmcli con down "$CON" >/dev/null 2>&1 || true
sleep 2
nmcli con up "$CON" >/dev/null 2>&1 || true
echo "[3/4] 正在重启 AP…"
sleep 8

CUR=$(nmcli -g 802-11-wireless.ssid con show "$CON" 2>/dev/null)
if [ "$CUR" = "$NEW" ]; then
  echo "[4/4] ✅ 成功：WiFi 名称已改为 $NEW"
  echo "     所有无线设备需用新名称重新连接（约 10~20 秒）"
  exit 0
fi

echo "[4/4] ❌ 读回不一致（当前=$CUR），正在回滚…"
nmcli con modify "$CON" 802-11-wireless.ssid "$OLD" >/dev/null 2>&1
nmcli con down "$CON" >/dev/null 2>&1 || true
sleep 2
nmcli con up "$CON" >/dev/null 2>&1 || true
sleep 6
RB=$(nmcli -g 802-11-wireless.ssid con show "$CON" 2>/dev/null)
if [ "$RB" = "$OLD" ]; then
  echo "✅ 已回滚到原名称 $OLD"
else
  echo "⚠ 回滚也没成功（当前=$RB），请用有线 SSH 手动检查 Gray-Router 连接"
fi
exit 1
