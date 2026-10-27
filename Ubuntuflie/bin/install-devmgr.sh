#!/usr/bin/env bash
# 一次性安装：设备管理助手（黑名单 + 流量统计）
# 用法：sudo bash ~/grayrouter-devmgr/install-devmgr.sh
set -uo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
TS=$(date +%m%d_%H%M)

[ "$(id -u)" -eq 0 ] || { echo "× 请用 sudo 跑：sudo bash $0"; exit 1; }

echo "⓪ 先给当前配置存一份快照（出问题可回滚）"
if [ -f "$DIR/grayrouter-snapshot.sh" ]; then
  install -m 755 "$DIR/grayrouter-snapshot.sh" /usr/local/bin/grayrouter-snapshot.sh || true
  /usr/local/bin/grayrouter-snapshot.sh save pre-install >/dev/null 2>&1 && echo "   已存：pre-install" || echo "   （快照没存成，继续）"
fi

echo "⓪.5 遥控通道保险（/run 被清也能自己恢复）"
install -m 644 "$DIR/60-grayrouter.conf" /etc/tmpfiles.d/grayrouter.conf || true
systemd-tmpfiles --create /etc/tmpfiles.d/grayrouter.conf >/dev/null 2>&1 || true
mkdir -p /run/grayrouter
[ -f /run/grayrouter/request ] || : > /run/grayrouter/request
chmod 666 /run/grayrouter/request
[ -f /run/grayrouter/result.json ] || echo '{}' > /run/grayrouter/result.json
chmod 644 /run/grayrouter/result.json
echo "   请求文件："$(ls -l /run/grayrouter/request | awk '{print $1, $3}')

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

echo "③.5 更新看门狗（自愈版：LAN/AP/SSH/路由/外网）"
if [ -f "$DIR/grayrouter-watch.sh" ]; then
  install -m 755 "$DIR/grayrouter-watch.sh" /usr/local/bin/grayrouter-watch.sh && echo "   已更新 grayrouter-watch.sh"
fi

echo "③.8 USB 网卡禁止自动挂起（udev）"
if [ -f "$DIR/99-usb-nic-no-suspend.rules" ]; then
  install -m 644 "$DIR/99-usb-nic-no-suspend.rules" /etc/udev/rules.d/99-usb-nic-no-suspend.rules
  udevadm control --reload 2>/dev/null || true
  echo "     √ 已装：RTL8153 网卡保持 power/control=on（不影响蓝牙）"
else
  echo "     （没找到规则文件，跳过）"
fi

echo "③.9 每设备流量计数表（nft 动态集合，覆盖有线+无线）"
install -m 755 "$DIR/grayrouter-acct.sh" /usr/local/bin/grayrouter-acct.sh || exit 1
bash /usr/local/bin/grayrouter-acct.sh apply || echo "   （计数表没建成，看报错）"

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
install -m 755 "$DIR/grayrouter-auth.sh" /usr/local/bin/grayrouter-auth.sh || exit 1
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
echo "④.5 有线 LAN（USB 网卡）开机规则"
install -m 755 "$DIR/lan-usb.sh" /usr/local/bin/lan-usb.sh || exit 1
install -m 644 "$DIR/grayrouter-lan2.service" /etc/systemd/system/grayrouter-lan2.service || exit 1
systemctl daemon-reload
systemctl enable --now grayrrouter-lan2.service >/dev/null 2>&1 || true
echo "   有线 LAN：$(systemctl is-active grayrrouter-lan2.service 2>/dev/null || echo 未知)"

install -m 644 "$DIR/grayrouter-watch.service" /etc/systemd/system/grayrouter-watch.service || true

echo "⑤.5 看门狗 + 快照"
systemctl enable --now grayrrouter-watch.timer >/dev/null 2>&1 || true
systemctl restart grayrrouter-watch.timer >/dev/null 2>&1 || true
echo "   看门狗：$(systemctl is-active grayrrouter-watch.timer 2>/dev/null || echo 未知)"
ls -1t /var/lib/grayrouter/snapshots/*.tar.gz 2>/dev/null | head -3 | sed 's/^/   快照：/'

echo "⑥ 验证"
nft list table inet gray_dev 2>/dev/null | head -12
echo "--- 设备清单 ---"
bash /usr/local/bin/grayrouter-devices.sh list
echo ""
echo "√ 装好了。回面板「设备」页点「刷新」看拉黑/流量；「流量」页下方新增「设备流量」表。"
systemctl list-timers grayrouter-traffic.timer --no-pager | head -3
