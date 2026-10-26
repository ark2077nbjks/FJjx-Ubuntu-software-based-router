#!/usr/bin/env bash
# 一次性安装：设备管理助手（黑名单 + 流量统计）
# 用法：sudo bash ~/grayrouter-devmgr/install-devmgr.sh
set -uo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
TS=$(date +%m%d_%H%M)

[ "$(id -u)" -eq 0 ] || { echo "× 请用 sudo 跑：sudo bash $0"; exit 1; }

echo "① 装脚本"
install -m 755 "$DIR/grayrouter-devices.sh" /usr/local/bin/grayrouter-devices.sh || exit 1

install -m 755 "$DIR/grayrouter-roles.sh" /usr/local/bin/grayrouter-roles.sh || exit 1

echo "② 装开机恢复单元"
install -m 644 "$DIR/grayrouter-devices.service" /etc/systemd/system/grayrouter-devices.service || exit 1

echo "③ 更新遥控助手（白名单 + 设备命令）"
if [ -f /usr/local/bin/grayrouter-ctl.sh ]; then
  cp -a /usr/local/bin/grayrouter-ctl.sh "/usr/local/bin/grayrouter-ctl.sh.bak_devmgr_$TS"
  echo "   已备份 grayrouter-ctl.sh.bak_devmgr_$TS"
fi
install -m 755 "$DIR/grayrouter-ctl.sh" /usr/local/bin/grayrouter-ctl.sh || exit 1

echo "③.5 更新看门狗（网口自动探测版）"
if [ -f "$DIR/grayrouter-watch.sh" ]; then
  install -m 755 "$DIR/grayrouter-watch.sh" /usr/local/bin/grayrouter-watch.sh && echo "   已更新 grayrouter-watch.sh"
fi

echo "③.6 装每设备流量采样（每小时，只读计数器）"
install -m 755 "$DIR/grayrouter-traffic.py" /usr/local/bin/grayrouter-traffic.py || exit 1
install -m 755 "$DIR/grayrouter-traffic.sh" /usr/local/bin/grayrouter-traffic.sh || exit 1
install -m 644 "$DIR/grayrouter-traffic.service" /etc/systemd/system/grayrouter-traffic.service || exit 1
install -m 644 "$DIR/grayrouter-traffic.timer" /etc/systemd/system/grayrouter-traffic.timer || exit 1
mkdir -p /var/lib/grayrouter/traffic
systemctl enable --now grayrouter-traffic.timer >/dev/null 2>&1 || true

echo "③.7 装双 WAN 故障转移（默认关，面板里开）"
install -m 755 "$DIR/grayrouter-wanfail.sh" /usr/local/bin/grayrouter-wanfail.sh || exit 1
install -m 755 "$DIR/grayrouter-rescue.sh" /usr/local/bin/grayrouter-rescue.sh || exit 1
install -m 644 "$DIR/grayrouter-wanfail.service" /etc/systemd/system/grayrouter-wanfail.service || exit 1
install -m 644 "$DIR/grayrouter-wanfail.timer" /etc/systemd/system/grayrouter-wanfail.timer || exit 1
systemctl daemon-reload
systemctl enable --now grayrouter-wanfail.timer >/dev/null 2>&1 || true

echo "③.8 手机 USB 共享插上自动要 IP（udev + 模板服务）"
install -m 755 "$DIR/grayrouter-usbnet-up.sh" /usr/local/bin/grayrouter-usbnet-up.sh || exit 1
install -m 755 "$DIR/grayrouter-usbnet-now.sh" /usr/local/bin/grayrouter-usbnet-now.sh || exit 1
install -m 644 "$DIR/grayrouter-usbnet@.service" /etc/systemd/system/grayrouter-usbnet@.service || exit 1
install -m 644 "$DIR/70-grayrouter-usbnet.rules" /etc/udev/rules.d/70-grayrouter-usbnet.rules || exit 1
udevadm control --reload-rules 2>/dev/null || true
systemctl daemon-reload
# 已经插着的立刻激活一次
for n in $(ls /sys/class/net | grep -E '^(enx|usb|rndis|wwan)'); do
  ip -4 addr show dev "$n" 2>/dev/null | grep -q 'inet ' && continue
  systemctl start --no-block "grayrouter-usbnet@$n.service" 2>/dev/null || true
done

systemctl enable --now grayrrouter-wanfail.timer >/dev/null 2>&1 || true
systemctl restart grayrrouter-wanfail.timer >/dev/null 2>&1 || true

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
echo "√ 装好了。回面板「设备」页点「刷新」看拉黑/流量；「流量」页下方新增「设备流量」表。"
systemctl list-timers grayrouter-traffic.timer --no-pager | head -3
