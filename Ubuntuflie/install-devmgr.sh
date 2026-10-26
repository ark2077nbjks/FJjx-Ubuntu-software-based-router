#!/usr/bin/env bash
# 一次性安装：设备管理助手（黑名单 + 流量统计）
# 用法：sudo bash ~/grayrouter-devmgr/install-devmgr.sh
set -uo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
TS=$(date +%m%d_%H%M)

[ "$(id -u)" -eq 0 ] || { echo "× 请用 sudo 跑：sudo bash $0"; exit 1; }

echo "① 装脚本"
install -m 755 "$DIR/grayrouter-devices.sh" /usr/local/bin/grayrouter-devices.sh || exit 1

echo "② 装开机恢复单元"
install -m 644 "$DIR/grayrouter-devices.service" /etc/systemd/system/grayrouter-devices.service || exit 1

echo "③ 更新遥控助手（白名单 + 设备命令）"
if [ -f /usr/local/bin/grayrouter-ctl.sh ]; then
  cp -a /usr/local/bin/grayrouter-ctl.sh "/usr/local/bin/grayrouter-ctl.sh.bak_devmgr_$TS"
  echo "   已备份 grayrouter-ctl.sh.bak_devmgr_$TS"
fi
install -m 755 "$DIR/grayrouter-ctl.sh" /usr/local/bin/grayrouter-ctl.sh || exit 1

echo "④ 清掉调试残留规则"
nft delete table inet dbgprobe 2>/dev/null && echo "   删了 dbgprobe" || true
nft delete table inet gray_dev 2>/dev/null && echo "   重建 gray_dev" || true

echo "⑤ 生效"
systemctl daemon-reload
systemctl enable grayrouter-devices.service >/dev/null 2>&1
bash /usr/local/bin/grayrouter-devices.sh apply || exit 1

echo ""
echo "⑥ 验证"
nft list table inet gray_dev 2>/dev/null | head -12
echo "--- 设备清单 ---"
bash /usr/local/bin/grayrouter-devices.sh list
echo ""
echo "√ 装好了。回面板「设备」页，点「刷新」就能看到「流量」和「操作」两列。"
