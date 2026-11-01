#!/usr/bin/env bash
# ============================================================
#  install-tailscale.sh —— 装 Tailscale，把内网暴露给 tailnet
#
#  用途：出门在外用手机/笔记本访问 192.168.50.x / 192.168.60.x 内网
#  特点：不需要公网 IP（走 DERP 中继），不改现有转发链路
#
#   ⚠️ 登录必须用户本人做（本脚本不代输账号、不代点授权）
#
#  用法：scp 到 ~/gr-install/ 后
#        sudo bash ~/gr-install/install-tailscale.sh
#        再用你自己的账号跑一次 tailscale up（脚本会打印命令）
# ============================================================
set -uo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)

[ "$(id -u)" -eq 0 ] || { echo "需要 root：sudo bash $0"; exit 2; }

echo "① 安装（官方源，幂等）"
if command -v tailscale >/dev/null 2>&1; then
  echo "  已安装：$(tailscale version 2>/dev/null | head -1)"
else
  echo "  首次安装，从 tailscale.com 取安装脚本（约 1-2 分钟）…"
  if curl -fsSL https://tailscale.com/install.sh | sh; then
    echo "  ✓ 安装完成"
  else
    echo "  × 安装失败：先确认本机已通过校园网认证、能出外网"
    exit 3
  fi
fi

echo "② 服务"
systemctl enable --now tailscaled >/dev/null 2>&1
if systemctl is-active tailscaled >/dev/null 2>&1; then
  echo "  ✓ tailscaled active"
else
  echo "  × tailscaled 没起来，看：journalctl -u tailscaled -n 30 --no-pager"
  exit 4
fi

echo "③ 下一步（由你本人执行，本脚本到此为止）"
echo ""
echo "   sudo tailscale up \\"
echo "     --advertise-routes=192.168.50.0/24,192.168.60.0/24 \\"
echo "     --accept-dns=false"
echo ""
echo "  执行后会打印一个 https://login.tailscale.com/... 链接，"
echo "  在浏览器里打开、登录你的 Tailscale 账号即可。"
echo ""
echo "  两个参数的含义："
echo "    --advertise-routes  把两个内网段广播给 tailnet（出门能访问内网设备）"
echo "    --accept-dns=false  不让 Tailscale 改本机 DNS —— 否则会和"
echo "                        NetworkManager 的 dnsmasq（192.168.50.1:53）打架"

echo ""
echo "④ 登录后的验收（这几条都过了才算成）"
cat <<'EOF'
  tailscale status                              # 能看到本机名字、状态 active
  tailscale ip -4                               # 拿到 100.x.y.z
  # 去 Tailscale 后台：Machines → 本机 → Edit route settings
  #   勾选 192.168.50.0/24 和 192.168.60.0/24（不勾则只能访问路由器本身）
  # 手机装 Tailscale 登录同一账号，关掉 WiFi 用流量访问：
  ping 192.168.50.1
EOF

echo ""
echo "⑤ 现状"
echo "  tailscaled：$(systemctl is-enabled tailscaled 2>/dev/null || echo not-found) / $(systemctl is-active tailscaled 2>/dev/null)"
echo "  提示：不想要了随时 sudo apt remove tailscale（不影响路由功能）"
