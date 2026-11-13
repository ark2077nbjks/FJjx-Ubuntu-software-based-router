#!/usr/bin/env bash
# ============================================================
#  check-server.sh —— Ubuntu Server 适配自检（只读，不改任何东西）
#
#  输出每一项 PASS / FAIL / WARN，最后给总结。
#  用途：装之前看差距；装之后验证；排障时快速定位。
# ============================================================
set -uo pipefail

PASS=0; FAIL=0; WARN=0
p() { printf '  \033[32m[PASS]\033[0m %s\n' "$*"; PASS=$((PASS+1)); }
f() { printf '  \033[31m[FAIL]\033[0m %s\n' "$*"; FAIL=$((FAIL+1)); }
w() { printf '  \033[33m[WARN]\033[0m %s\n' "$*"; WARN=$((WARN+1)); }

say() { printf '%s\n' "$*"; }

say "============================================================"
say "  Ubuntu Server 适配自检（只读）"
say "============================================================"

# ---------- 1. 系统 ----------
say ""
say "【1】系统信息"
. /etc/os-release 2>/dev/null
say "  版本：${PRETTY_NAME:-未知}"
K=$(uname -r)
say "  内核：$K"
case "$K" in
  *generic*) p "generic 内核（支持 AP/驱动）" ;;
  *kvm*|*virtual*) w "虚拟化内核（$K）——容器/虚拟机环境功能可能受限" ;;
  *) w "内核变体：$K" ;;
esac

if dpkg -l 2>/dev/null | grep -q "^ii  ubuntu-desktop"; then
  w "检测到 Desktop（有桌面）——可以跑，但 Server 更省资源"
else
  p "Server / 最小安装（无桌面）"
fi

# ---------- 2. 网络管理栈 ----------
say ""
say "【2】网络管理栈（本方案硬依赖 NetworkManager）"
if command -v nmcli >/dev/null 2>&1; then
  p "nmcli 存在"
  if [ "$(systemctl is-active NetworkManager 2>/dev/null)" = "active" ]; then
    p "NetworkManager 正在运行"
  else
    f "NetworkManager 未运行（16 个脚本会失效）"
  fi
else
  f "nmcli 缺失——需要 apt install network-manager"
fi

ND=$(systemctl is-active systemd-networkd 2>/dev/null || echo inactive)
if [ "$ND" = "active" ]; then
  w "systemd-networkd 仍在运行（可能与 NM 抢网络，建议停用）"
else
  p "systemd-networkd 未运行"
fi

# netplan renderer
RP=0; RN=0
for f in /etc/netplan/*.yaml; do
  [ -e "$f" ] || continue
  grep -q "renderer: networkd" "$f" 2>/dev/null && RN=$((RN+1))
  grep -q "renderer: NetworkManager" "$f" 2>/dev/null && RP=$((RP+1))
done
if [ "$RN" -gt 0 ]; then
  f "有 $RN 个 netplan 文件仍用 networkd（NM 管不到）"
elif [ "$RP" -gt 0 ]; then
  p "netplan 已指向 NetworkManager"
else
  w "netplan 里没写 renderer（可能用默认值）"
fi

# ---------- 3. 命令依赖 ----------
say ""
say "【3】命令依赖（35 个脚本实际用到）"
check_cmd() {
  local c="$1" pkg="$2" critical="${3:-1}"
  if command -v "$c" >/dev/null 2>&1; then
    p "$c"
  elif [ "$critical" = 1 ]; then
    f "$c 缺失（apt install $pkg）"
  else
    w "$c 缺失（可选，apt install $pkg）"
  fi
}
check_cmd ip iproute2 1
check_cmd tc iproute2 1
check_cmd nmcli network-manager 1
check_cmd nft nftables 1
check_cmd iw iw 1
check_cmd jq jq 1
check_cmd curl curl 1
check_cmd python3 python3 1
check_cmd dnsmasq dnsmasq 0
check_cmd docker docker.io 0

# ---------- 4. 无线能力 ----------
say ""
say "【4】无线能力（AP 模式）"
if command -v iw >/dev/null 2>&1; then
  if iw dev 2>/dev/null | grep -q "Interface"; then
    IF=$(iw dev 2>/dev/null | awk '/Interface/{print $2; exit}')
    p "无线接口存在：$IF"
    if iw list 2>/dev/null | sed -n '/Supported interface modes/,/valid interface/p' | grep -q "\* AP"; then
      p "驱动支持 AP 模式"
    else
      f "驱动可能不支持 AP 模式（iw list 里没有 * AP）"
    fi
  else
    f "iw 看不到无线接口（没插网卡 / 缺驱动）"
  fi
else
  f "iw 缺失，无法检查无线"
fi

# ---------- 5. 网络状态 ----------
say ""
say "【5】当前网络状态"
if ip route show default 2>/dev/null | grep -q .; then
  p "有默认路由：$(ip route show default | head -1 | awk '{print $3, $5}')"
else
  f "没有默认路由（网络不通）"
fi
CODE=$(curl -s -m 5 -o /dev/null -w "%{http_code}" http://www.baidu.com 2>/dev/null || echo 000)
case "$CODE" in
  200|301|302) p "外网可达（HTTP $CODE）" ;;
  000)         w "外网不可达（curl 超时：未认证 / 未连网 / DNS 问题）" ;;
  *)           w "外网返回 HTTP $CODE（异常）" ;;
esac

# ---------- 6. 方案自身 ----------
say ""
say "【6】GrayRouter 方案状态"
if [ -x /usr/local/bin/grayrouter-ctl.sh ]; then
  p "脚本已安装（/usr/local/bin/grayrouter-*.sh）"
  N=$(ls /usr/local/bin/grayrouter-*.sh 2>/dev/null | wc -l)
  say "     共 $N 个"
else
  w "脚本未安装（还没跑 install.sh）"
fi

FAILED=$(systemctl --failed --no-legend 2>/dev/null | wc -l)
if [ "$FAILED" -eq 0 ]; then
  p "没有失败的 systemd 单元"
else
  f "有 $FAILED 个失败单元（systemctl --failed 看）"
fi

if [ -x /usr/local/bin/grayrouter-audit.sh ]; then
  if sudo -n /usr/local/bin/grayrouter-audit.sh verify >/dev/null 2>&1; then
    p "配置审计健康"
  else
    w "审计 verify 需要 sudo 或未通过（手动跑：sudo grayrrouter-audit.sh verify）"
  fi
fi

# ---------- 总结 ----------
say ""
say "============================================================"
say "  结果：PASS $PASS  ·  WARN $WARN  ·  FAIL $FAIL"
case "$FAIL" in
  0) say "  ✓ 环境就绪，可以跑 install.sh" ;;
  1|2) say "  有少量 FAIL，先修再装（见上面红色项）" ;;
  *) say "  FAIL 较多，建议先跑 install-server-prep.sh" ;;
esac
say "============================================================"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
