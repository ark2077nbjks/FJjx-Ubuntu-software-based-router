#!/bin/bash
# ============================================================
#  grayrouter-backup.sh —— 导出配置备份（只读，不改任何设置）
#
#  用法：grayrouter-backup.sh [输出目录]
#  产物：grayrouter-backup-<时间>.tar.gz
#
#  打包内容：
#    /etc/grayrouter/*            模式标记、SQM/多WAN/角色配置
#    dnsmasq-shared.d/*           静态 DHCP 租约（有的话）
#    connections/*.txt            每个 NetworkManager 连接的完整参数
#    meta/*                       网卡、路由、整形状态、系统版本、单元清单
#
#  恢复：解包后把 etc-grayrouter 放回 /etc/grayrouter，连接参数按 txt 重建
# ============================================================
set -uo pipefail

DEST_DIR=${1:-${HOME:-/root}}
mkdir -p "$DEST_DIR" 2>/dev/null || { echo "目录建不出来：$DEST_DIR（权限？）" >&2; exit 1; }

STAMP=$(date +%Y%m%d_%H%M%S)
OUT="$DEST_DIR/grayrouter-backup-$STAMP.tar.gz"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

mkdir -p "$TMP/meta" "$TMP/connections"

# 1) 配置目录
if [ -d /etc/grayrouter ]; then
  cp -a /etc/grayrouter "$TMP/etc-grayrouter" 2>/dev/null || true
fi

# 2) dnsmasq 静态租约
if [ -d /etc/NetworkManager/dnsmasq-shared.d ]; then
  cp -a /etc/NetworkManager/dnsmasq-shared.d "$TMP/dnsmasq-shared.d" 2>/dev/null || true
fi

# 3) NetworkManager 连接（纯文本，便于人工核对）
nmcli -t -f NAME connection show 2>/dev/null | while IFS= read -r c; do
  [ -z "$c" ] && continue
  safe=$(printf '%s' "$c" | tr '/ ' '__')
  nmcli connection show "$c" > "$TMP/connections/$safe.txt" 2>/dev/null
done

# 4) 运行态快照（只读）
ip -br addr    > "$TMP/meta/ip.txt"    2>/dev/null
ip route       > "$TMP/meta/route.txt" 2>/dev/null
uname -a       > "$TMP/meta/uname.txt" 2>/dev/null
cat /etc/os-release >> "$TMP/meta/uname.txt" 2>/dev/null
systemctl list-units 'grayrouter*' --all --no-pager > "$TMP/meta/units.txt" 2>/dev/null
bash /usr/local/bin/grayrouter-sqm.sh status > "$TMP/meta/sqm.txt" 2>/dev/null || true
bash /usr/local/bin/grayrouter-wanfail.sh get > "$TMP/meta/wanfail.txt" 2>/dev/null || true

# 5) 打包
tar czf "$OUT" -C "$TMP" . 2>/dev/null || { echo "打包失败" >&2; exit 1; }
chmod 600 "$OUT"

echo "$OUT"
