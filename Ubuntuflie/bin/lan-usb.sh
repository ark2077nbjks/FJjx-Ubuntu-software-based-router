#!/usr/bin/env bash
# ============================================================
#  lan-usb.sh —— 把 USB 网卡配成「有线 LAN」（B2：独立网段 + NAT）
#    用法：sudo bash lan-usb.sh [up|down|status]
#    up     ：建 192.168.60.1/24 的共享连接（NM 自带 DHCP + NAT）
#    down   ：删掉这条连接（回到没配的状态）
#    status ：看状态
# ============================================================
set -uo pipefail
NET=192.168.60.1/24
CON=LAN-USB
[ "$(id -u)" -eq 0 ] || { echo "需要 root：sudo bash $0 $*"; exit 1; }

find_if() {  # 找一块「USB 以太网卡」：名字 enx*，且不是手机共享(rndis/cdc)
  local n d
  for n in $(ls /sys/class/net); do
    case "$n" in enx*|usb*) ;; *) continue ;; esac
    d=$(readlink -f "/sys/class/net/$n/device/driver" 2>/dev/null | xargs -r basename)
    case "$d" in rndis_host|cdc_ether|cdc_ncm|usbnet) continue ;; esac
    echo "$n"; return 0
  done
  return 1
}

do_up() {
  local ifc; ifc=$(find_if) || { echo "没找到 USB 网卡（enx*）"; exit 2; }
  echo "USB 网卡：$ifc"

  # 1) 清掉被误判成「手机共享」的连接
  local old
  for old in $(nmcli -t -f NAME con show | grep -E "^USB共享-${ifc}$|^USB共享-" || true); do
    nmcli con delete "$old" >/dev/null 2>&1 && echo "已删掉误配连接：$old"
  done

  # 2) 建/更新 LAN 连接
  if nmcli -t -f NAME con show | grep -qx "$CON"; then
    nmcli con modify "$CON" ifname "$ifc" ipv4.method shared ipv4.addresses "$NET" ipv6.method ignore connection.autoconnect yes >/dev/null
    echo "已更新连接 $CON → $ifc / $NET"
  else
    nmcli con add type ethernet ifname "$ifc" con-name "$CON" \
      ipv4.method shared ipv4.addresses "$NET" ipv6.method ignore connection.autoconnect yes >/dev/null
    echo "已新建连接 $CON → $ifc / $NET"
  fi
  nmcli con up "$CON" >/dev/null 2>&1 || { echo "启用失败"; exit 3; }

  do_rules

  echo
  do_status
}

do_rules() {
  # 两个 LAN 网段互访（幂等：先删再建）
  nft delete table inet gray_lan2 2>/dev/null || true
  # 注意：链名不能用 'fwd'，它是 nft 保留字（会报 unexpected fwd）
  nft -f - <<'EOF'
table inet gray_lan2 {
  chain lanfwd {
    type filter hook forward priority -5; policy accept;
    ip saddr 192.168.60.0/24 ip daddr 192.168.50.0/24 accept
    ip saddr 192.168.50.0/24 ip daddr 192.168.60.0/24 accept
  }
}
EOF
}

do_down() {
  nmcli con delete "$CON" >/dev/null 2>&1 && echo "已删除 $CON" || echo "本来就没有 $CON"
  nft delete table inet gray_lan2 2>/dev/null && echo "已删除互访规则"
}

do_status() {
  echo "--- 状态 ---"
  local ifc; ifc=$(find_if) || { echo "USB 网卡：没找到"; return; }
  echo "网口：$ifc  carrier=$(cat /sys/class/net/$ifc/carrier 2>/dev/null || echo ?)"
  ip -4 -o addr show dev "$ifc" 2>/dev/null | awk '{print "地址：" $4}' || echo "地址：无"
  nmcli -t -f NAME,DEVICE,STATE con show --active 2>/dev/null | grep -i "$CON" || echo "连接 $CON：未激活"
  echo -n "NM 共享的 DHCP/NAT："; nft list tables 2>/dev/null | grep -c nm-shared >/dev/null && echo "已配" || echo "未见"
}

case "${1:-status}" in
  up) do_up ;;
  down) do_down ;;
  rules) do_rules ;;
  status) do_status ;;
  *) echo "用法：sudo bash $0 up|down|status|rules"; exit 2 ;;
esac
