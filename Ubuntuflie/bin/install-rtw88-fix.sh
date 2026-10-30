#!/usr/bin/env bash
# 一键安装：驱动稳定参数 + 关无线省电 + 信标自愈 + 开机自动恢复软路由
# 可重复执行（幂等）。用法：sudo bash ~/grayrouter-fix/install-rtw88-fix.sh
set -u
[ "$(id -u)" -eq 0 ] || { echo "要用 sudo 跑"; exit 1; }
SRC="$(cd "$(dirname "$0")" && pwd)"
TS=$(date +%m%d_%H%M)
fail=0
ok(){ echo "   ✅ $*"; }
bad(){ echo "   ❌ $*"; fail=1; }

echo "① 驱动参数"
install -m 644 "$SRC/rtw88-stable.conf" /etc/modprobe.d/rtw88-stable.conf && ok "/etc/modprobe.d/rtw88-stable.conf" || bad "写 modprobe 失败"

echo "② 关无线省电"
CONF=/etc/NetworkManager/conf.d/default-wifi-powersave-on.conf
[ -f "$CONF" ] && cp -a "$CONF" "$CONF.bak_$TS"
if [ -f "$CONF" ]; then
    sed -i 's/^wifi\.powersave *= *3/wifi.powersave = 2/' "$CONF"
    grep -q '^wifi\.powersave = 2' "$CONF" || printf '\n[connection]\nwifi.powersave = 2\n' >> "$CONF"
else
    printf '[connection]\nwifi.powersave = 2\n' > "$CONF"
fi
grep -q '^wifi\.powersave = 2' "$CONF" && ok "wifi.powersave = 2" || bad "省电没改成功"

echo "③ 信标自愈"
install -m 755 "$SRC/grayrouter-beacon-watch.sh" /usr/local/bin/grayrouter-beacon-watch.sh && ok "脚本" || bad "脚本"
install -m 644 "$SRC/grayrouter-beacon.service" /etc/systemd/system/grayrouter-beacon.service && ok "service" || bad "service"
install -m 644 "$SRC/grayrouter-beacon.timer"   /etc/systemd/system/grayrouter-beacon.timer   && ok "timer"   || bad "timer"

echo "④ 开机自动恢复软路由"
install -m 755 "$SRC/grayrouter-boot-up.sh"   /usr/local/bin/grayrouter-boot-up.sh        && ok "脚本" || bad "启动脚本"
install -m 644 "$SRC/grayrouter-boot.service" /etc/systemd/system/grayrouter-boot.service  && ok "service" || bad "service"
mkdir -p /etc/grayrouter
if iw dev wlp2s0 info 2>/dev/null | grep -qw AP; then
    echo router > /etc/grayrouter/boot-mode
    ok "当前是软路由模式 → 标记 router"
else
    ok "当前不是 AP 模式 → 不写标记"
fi

echo "⑤ 重载 systemd"
systemctl daemon-reload; sleep 1
systemctl enable --now grayrrouter-beacon.timer  >/dev/null 2>&1
systemctl enable grayrrouter-boot.service        >/dev/null 2>&1
echo -n "   信标定时器："; systemctl is-enabled grayrrouter-beacon.timer 2>&1
echo -n "   信标定时器运行："; systemctl is-active grayrrouter-beacon.timer 2>&1
echo -n "   开机单元："; systemctl is-enabled grayrrouter-boot.service 2>&1
echo -n "   模式标记："; cat /etc/grayrouter/boot-mode 2>/dev/null || echo 无

echo
if [ "$fail" = "0" ]; then
    echo "全部完成。驱动参数重启后生效："
    echo "   cat /sys/module/rtw88_pci/parameters/disable_aspm      → 应为 Y"
    echo "   cat /sys/module/rtw88_core/parameters/disable_lps_deep → 应为 Y"
else
    echo "有步骤失败，把上面的 ❌ 行发我。"
fi
