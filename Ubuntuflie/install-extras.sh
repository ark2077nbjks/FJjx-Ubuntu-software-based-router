#!/usr/bin/env bash
# ============================================================
#  install-extras.sh —— 安装附加 systemd 单元
#   （sqm / opt / audit / devices / lan2 / watch / traffic / beacon / wanfail / usbnet@）
#   由 install.sh 自动调用，也可单独跑：sudo bash install-extras.sh
# ============================================================
set -uo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
[ "$(id -u)" -eq 0 ] || { echo "需要 root：sudo bash $0"; exit 2; }
GR_OPT=${GR_OPT:-/opt/grayrouter}
GR_ETC=${GR_ETC:-/etc/grayrouter}
GR_VAR=${GR_VAR:-/var/lib/grayrouter}
SD=${SYSTEMD_DIR:-/etc/systemd/system}
mkdir -p "$GR_ETC" "$GR_VAR"

echo "① 单元模板（@BIN@ → $GR_OPT/bin）"
n=0
for f in "$HERE"/etc/units/*.service "$HERE"/etc/units/*.timer; do
  [ -e "$f" ] || continue
  b=$(basename "$f")
  sed "s#@BIN@#$GR_OPT/bin#g" "$f" > "$SD/$b"
  echo "  ✓ $b"; n=$((n+1))
done
[ "$n" -gt 0 ] || { echo "  × 没找到 etc/units/*，解压不完整？"; exit 3; }
systemctl daemon-reload

echo "② 默认配置（只在缺失时创建，不覆盖已有）"
[ -f "$GR_ETC/sqm.env" ] || { printf 'SQM_ENABLED=0\nDOWN_KBIT=0\nUP_KBIT=0\nMODE=auto\n' > "$GR_ETC/sqm.env"; echo "  + sqm.env（默认关闭）"; }
[ -f "$GR_ETC/limits.conf" ] || { : > "$GR_ETC/limits.conf"; echo "  + limits.conf（空）"; }

echo "③ 启用"
for u in grayrouter-sqm grayrouter-opt grayrouter-audit.timer grayrouter-devices grayrouter-lan2 \
         grayrouter-watch.timer grayrouter-traffic.timer grayrouter-beacon.timer grayrouter-wanfail.timer; do
  if systemctl enable "$u" >/dev/null 2>&1; then echo "  ✓ $u"; else echo "  × $u"; fi
done
systemctl enable 'grayrouter-usbnet@.service' >/dev/null 2>&1 && echo "  ✓ grayrouter-usbnet@.service（模板）"

echo "④ 首次执行（幂等，只做本地配置）"
if [ -x "$GR_OPT/bin/grayrouter-opt.sh" ]; then
  "$GR_OPT/bin/grayrouter-opt.sh" apply >/dev/null 2>&1 && echo "  ✓ opt apply" || echo "  ! opt apply 失败"
fi
if [ -x "$GR_OPT/bin/grayrouter-audit.sh" ]; then
  "$GR_OPT/bin/grayrouter-audit.sh" init >/dev/null 2>&1 && echo "  ✓ audit 初始化" || echo "  ! audit 初始化失败"
fi

echo "⑤ 结果"
echo "  failed 单元：$(systemctl --failed --no-legend | wc -l)"
echo "  已启用：$(systemctl list-unit-files 'grayrouter*' --no-legend | grep -c enabled)"
echo "  提示：SQM / 限速 / 多 WAN 需要面板或脚本再配置，默认不启用。"
