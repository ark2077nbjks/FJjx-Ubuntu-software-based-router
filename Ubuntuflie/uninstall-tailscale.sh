#!/usr/bin/env bash
# ============================================================
#  uninstall-tailscale.sh —— 卸载 Tailscale，恢复到安装前
#
#  清四样东西：
#    ① tailscaled 服务（stop + disable）
#    ② 软件包 + apt 源 + keyring
#    ③ 它加的 5 条策略路由规则（ip rule）
#    ④ tailscale0 接口
#
#  用法：sudo bash ~/gr-install/uninstall-tailscale.sh
#  可选：加 --reboot 参数，清完自动重启（会断网 40 秒）
# ============================================================
set -uo pipefail
REBOOT=0
[ "${1:-}" = "--reboot" ] && REBOOT=1

[ "$(id -u)" -eq 0 ] || { echo "需要 root：sudo bash $0"; exit 2; }

echo "① 停服务"
if systemctl is-active tailscaled >/dev/null 2>&1; then
  systemctl stop tailscaled && echo "  ✓ tailscaled 已停止"
else
  echo "  - tailscaled 未运行"
fi
if systemctl is-enabled tailscaled >/dev/null 2>&1; then
  systemctl disable tailscaled 2>/dev/null && echo "  ✓ 已取消开机自启"
else
  echo "  - 未启用自启"
fi

echo "② 卸载软件包"
if dpkg -l 2>/dev/null | grep -qE '^ii\s+tailscale\s'; then
  apt-get purge -y tailscale tailscale-archive-keyring >/tmp/ts-purge.log 2>&1 \
    && echo "  ✓ 已 purge tailscale + keyring" \
    || echo "  × 卸载失败，看 /tmp/ts-purge.log"
else
  echo "  - 未安装（或已卸载）"
fi

echo "③ 清理 apt 源"
rm -f /etc/apt/sources.list.d/tailscale.list && echo "  ✓ 删除 tailscale.list"
rm -f /usr/share/keyrings/tailscale-archive-keyring.gpg && echo "  ✓ 删除 keyring"
apt-get update -qq >/dev/null 2>&1 && echo "  ✓ apt update 完成"

echo "④ 清理策略路由残留"
n=0
for p in 5210 5230 5250 5270 32765; do
  if ip rule del priority "$p" 2>/dev/null; then
    echo "  ✓ 删除规则 priority $p"; n=$((n+1))
  fi
done
[ "$n" -eq 0 ] && echo "  - 没有残留规则（或已清过）"

if ip link show tailscale0 >/dev/null 2>&1; then
  ip link del tailscale0 2>/dev/null && echo "  ✓ 删除 tailscale0 接口" || echo "  ! tailscale0 删不掉（重启后会消失）"
else
  echo "  - tailscale0 不存在"
fi

echo "⑤ 验收"
echo "  tailscale 命令 : $(command -v tailscale 2>/dev/null || echo '已移除')"
echo "  服务状态       : $(systemctl is-enabled tailscaled 2>/dev/null || echo '不存在')"
left=$(ip rule 2>/dev/null | grep -cE 'fwmark 0x(80000|1c9)|lookup (52|457)' || true)
echo "  残留规则条数   : $left"
echo "  接口           : $(ip link show tailscale0 >/dev/null 2>&1 && echo '仍在' || echo '已移除')"
echo "  网络健康       : failed=$(systemctl --failed --no-legend | wc -l)  ip_forward=$(sysctl -n net.ipv4.ip_forward)"
echo "  默认路由       : $(ip route | grep -m1 default || echo '无！')"
echo ""
if [ "$left" -gt 0 ]; then
  echo "  ⚠ 还有 $left 条残留规则，重启后彻底消失"
fi
echo "  ✓ 卸载完成。Tailscale 不会再影响转发。"
echo ""
if [ "$REBOOT" = "1" ]; then
  echo "  3 秒后重启…"
  sleep 3
  reboot
else
  echo "  要彻底清干净（含 tailscale0 和残留规则），重启一次即可："
  echo "      sudo reboot"
  echo "  或直接重跑带参数：sudo bash $0 --reboot"
fi
