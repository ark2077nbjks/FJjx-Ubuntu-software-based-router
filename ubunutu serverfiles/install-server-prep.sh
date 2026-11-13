#!/usr/bin/env bash
# ============================================================
#  install-server-prep.sh —— Ubuntu Server 版准备脚本
#
#  把一台 Ubuntu Server（或最小安装）准备成 GrayRouter 可用的环境：
#   1. 装依赖：network-manager / nftables / jq / iw / iproute2 / curl / python3
#   2. 网络栈：停 systemd-networkd，启 NetworkManager
#   3. netplan：renderer: networkd → NetworkManager
#   4. 备份所有改动
#   5. 验证
#
#  用法：
#    bash install-server-prep.sh --dry-run    # 演练，只看会做什么
#    sudo bash install-server-prep.sh         # 真执行
#
#  ⚠️ 切换网络栈会短暂断网。执行前确认能物理接触（HDMI+键盘）或 SSH 稳。
# ============================================================
set -uo pipefail

DRY=0
[ "${1:-}" = "--dry-run" ] && DRY=1

say()  { printf '%s\n' "$*"; }
ok()   { printf '✓ %s\n' "$*"; }
warn() { printf '! %s\n' "$*"; }
die()  { printf '✗ %s\n' "$*" >&2; exit 1; }
run()  {
  if [ "$DRY" = 1 ]; then
    say "  [演练] $*"
  else
    eval "$@"
  fi
}

[ "$(id -u)" -eq 0 ] || [ "$DRY" = 1 ] || die "需要 root：sudo bash $0"
[ "$DRY" = 1 ] && warn "=== 演练模式：不会改动任何东西 ==="

# ---------- 0. 现状检查 ----------
say "==== 0/6 现状检查 ===="
. /etc/os-release 2>/dev/null
say "  系统：${PRETTY_NAME:-未知}"
DESKTOP=0
dpkg -l 2>/dev/null | grep -q "^ii  ubuntu-desktop" && DESKTOP=1
[ "$DESKTOP" = 1 ] && say "  形态：Desktop（有桌面）" || say "  形态：Server / 最小安装"

NM_STATE=$(systemctl is-active NetworkManager 2>/dev/null || echo inactive)
ND_STATE=$(systemctl is-active systemd-networkd 2>/dev/null || echo inactive)
say "  NetworkManager：$NM_STATE"
say "  systemd-networkd：$ND_STATE"

# ---------- 1. 依赖检查 ----------
say ""
say "==== 1/6 依赖检查 ===="
MISSING=""
for c in nmcli nft iw jq ip tc curl python3; do
  if command -v "$c" >/dev/null 2>&1; then
    ok "$c"
  else
    warn "$c 缺失"
    case "$c" in
      nmcli) MISSING="$MISSING network-manager" ;;
      nft)   MISSING="$MISSING nftables" ;;
      iw)    MISSING="$MISSING iw" ;;
      jq)    MISSING="$MISSING jq" ;;
      ip|tc) MISSING="$MISSING iproute2" ;;
      curl)  MISSING="$MISSING curl" ;;
      python3) MISSING="$MISSING python3" ;;
    esac
  fi
done
# dnsmasq 由 NM 带，不单独要求
MISSING=$(printf '%s' "$MISSING" | xargs || true)

if [ -n "$MISSING" ]; then
  say ""
  say "  需要安装：$MISSING"
  run "apt-get update -qq"
  run "DEBIAN_FRONTEND=noninteractive apt-get install -y $MISSING"
  [ "$DRY" = 0 ] && ok "依赖已安装" || say "  [演练] 会安装：$MISSING"
else
  ok "依赖齐全，无需安装"
fi

# ---------- 2. 备份 ----------
say ""
say "==== 2/6 备份现有配置 ===="
BK="/var/lib/grayrouter/server-migration-backup/$(date '+%Y%m%d_%H%M%S')"
if [ "$DRY" = 0 ]; then
  mkdir -p "$BK" || die "无法创建备份目录"
  cp -a /etc/netplan "$BK/" 2>/dev/null || true
  cp -a /etc/NetworkManager "$BK/" 2>/dev/null || true
  systemctl list-unit-files > "$BK/units.txt" 2>/dev/null || true
  ip -br addr > "$BK/ip.txt" 2>/dev/null || true
  ip route > "$BK/route.txt" 2>/dev/null || true
  ok "已备份到 $BK"
else
  say "  [演练] 会备份到 $BK"
fi

# ---------- 3. netplan 渲染器 ----------
say ""
say "==== 3/6 netplan 渲染器 ===="
CHANGED=0
if [ -d /etc/netplan ]; then
  for f in /etc/netplan/*.yaml; do
    [ -e "$f" ] || continue
    if grep -q "renderer: networkd" "$f" 2>/dev/null; then
      warn "$(basename "$f")：renderer 是 networkd，要改"
      if [ "$DRY" = 1 ]; then
        say "  [演练] 会改成 renderer: NetworkManager"
      else
        sed -i 's/renderer: networkd/renderer: NetworkManager/g' "$f"
        ok "已改：$(basename "$f")"
      fi
      CHANGED=$((CHANGED+1))
    fi
  done
  [ "$CHANGED" -eq 0 ] && ok "netplan 已经指向 NetworkManager（或无需改）"
else
  warn "/etc/netplan 不存在（可能用别的网络管理），跳过"
fi
[ "$DRY" = 0 ] && [ "$CHANGED" -gt 0 ] && run "netplan apply 2>/dev/null || true"

# ---------- 4. 切换网络栈 ----------
say ""
say "==== 4/6 切换网络栈（会短暂断网） ===="
if [ "$ND_STATE" = "active" ]; then
  run "systemctl disable --now systemd-networkd 2>/dev/null || true"
  run "systemctl disable --now systemd-networkd.socket 2>/dev/null || true"
  ok "systemd-networkd 已停用"
else
  ok "systemd-networkd 未运行，无需停"
fi

if [ "$NM_STATE" != "active" ]; then
  run "systemctl enable --now NetworkManager"
  [ "$DRY" = 0 ] && sleep 3
  [ "$DRY" = 0 ] && ok "NetworkManager 已启动" || say "  [演练] 会启动 NetworkManager"
else
  ok "NetworkManager 已在运行"
fi

# ---------- 5. 验证网络 ----------
say ""
say "==== 5/6 验证 ===="
if [ "$DRY" = 1 ]; then
  say "  [演练] 跳过运行时验证"
else
  sleep 2
  S=$(systemctl is-active NetworkManager 2>/dev/null || echo inactive)
  [ "$S" = "active" ] && ok "NetworkManager active" || warn "NetworkManager 状态：$S"

  if nmcli -t general status 2>/dev/null | grep -q connected; then
    ok "nmcli 报告 connected"
  else
    warn "nmcli 未连接（检查网线/配置）"
  fi

  if command -v iw >/dev/null 2>&1 && iw dev 2>/dev/null | grep -q Interface; then
    ok "iw 能看到无线接口"
    if iw list 2>/dev/null | grep -A6 "Supported interface modes" | grep -q "AP"; then
      ok "无线支持 AP 模式"
    else
      warn "无线可能不支持 AP 模式（iw list 里没有 AP）"
    fi
  else
    warn "iw 看不到无线接口（没插无线网卡或缺驱动）"
  fi

  command -v nft >/dev/null 2>&1 && nft list tables >/dev/null 2>&1 && ok "nft 可用（需要 root）" || warn "nft 不可用"
fi

# ---------- 6. 小结 ----------
say ""
say "============================================================"
if [ "$DRY" = 1 ]; then
  say "  演练结束（没有改动任何东西）"
  say "  真执行：sudo bash $0"
else
  say "  ✓ Server 准备完成"
  say "  备份：$BK"
  say "  下一步：sudo bash ../Ubuntuflie/install.sh"
  say "  验证：bash check-server.sh"
fi
say "============================================================"
