#!/usr/bin/env bash
# ============================================================
#  set-ssid.sh <新名字>  —— 改 WiFi 名称（SSID）
#
#  设计要点（踩坑教训）：
#   · 绝不 nmcli con down —— 会让 NetworkManager 重排整卡网络，有线一起断
#   · 失败不自动回滚     —— 回滚要再断一次，伤害更大
#   · 只写配置 + con up + 轮询验证
#   · 802.11 标准：SSID 上限 32 字节（一个汉字 3 字节）
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
  echo "❌ 找不到无线连接 $CON"
  exit 1
fi

if [ "$OLD" = "$NEW" ]; then
  echo "√ 已经是这个名字：$NEW"
  exit 0
fi

echo "===== 修改 WiFi 名称 ====="
echo "原名称：$OLD"
echo "新名称：$NEW"

mkdir -p "$DIR" 2>/dev/null
printf '%s\n' "$OLD" > "$BAK"
echo "[1/3] 已记录原名（不会自动回滚，需要时用它改回）"

if ! nmcli con modify "$CON" 802-11-wireless.ssid "$NEW"; then
  echo "❌ 写配置失败，设置未改动"
  exit 1
fi
echo "[2/3] 已写入新名称"

# 只重应用，不 down（关键：down 会连带断有线）
nmcli con up "$CON" >/dev/null 2>&1 || true
echo "[3/3] 正在重启 AP 应用新名字…"

CUR=""
for i in $(seq 1 20); do
  sleep 1
  CUR=$(nmcli -g 802-11-wireless.ssid con show "$CON" 2>/dev/null)
  [ "$CUR" = "$NEW" ] && break
done

if [ "$CUR" = "$NEW" ]; then
  echo "✅ 成功：WiFi 名称已改为 $NEW"
  echo "   无线设备需用新名字重连（约 10~20 秒）；有线不受影响。"
  exit 0
fi

echo "❌ 读回不一致（当前=${CUR:-空}）"
echo "   配置里已是新名字，下次无线重连或重启会自动生效。"
echo "   本次不自动回滚（避免再断一次）。要改回原名可手动："
echo "   nmcli con modify $CON 802-11-wireless.ssid \"$OLD\" && nmcli con up $CON"
exit 1
